using System.Net;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// Phase 4 exit criterion, part one: the PRD's 10 x 7 access matrix, taken from authz/validation.yaml, answered through
/// the HTTP API (a 404 for "cannot view", otherwise the permission map in the object response), and the matching action
/// endpoints refuse every denied-but-visible cell with a 403 naming the permission.
/// </summary>
public sealed class MatrixOverHttpTests : ApiTestBase
{
    /// <summary>The seven matrix columns: the cell's (resource, permission), the GET that exposes it, and the action it gates.</summary>
    private sealed record Column(string Resource, string Permission, string Url, string ActionUrl, object ActionBody, string ActionPermission);

    private static readonly Column[] Columns =
    [
        new("customer:acme", "view", "/api/customers/acme", "/api/customers/acme/domains", new { name = "probe" }, "create_domain"),
        new("domain:acme-uk", "admin", "/api/domains/acme-uk", "/api/domains/acme-uk/wallets", new { name = "probe" }, "create_wallet"),
        new("wallet:uk-ops", "spend", "/api/wallets/uk-ops", "/api/wallets/uk-ops/subscriptions", new { productName = "probe", quantity = 1 }, "create_subscription"),
        new("subscription:m365-e3", "change_quantity", "/api/subscriptions/m365-e3", "/api/subscriptions/m365-e3/quantity", new { quantity = 3 }, "change_quantity"),
        new("subscription:m365-e3", "cancel", "/api/subscriptions/m365-e3", "/api/subscriptions/m365-e3/cancel", new { }, "cancel"),
        new("wallet:ie-ops", "view", "/api/wallets/ie-ops", "/api/wallets/ie-ops/subscriptions", new { productName = "probe", quantity = 1 }, "create_subscription"),
        new("customer:globex", "view", "/api/customers/globex", "/api/customers/globex/domains", new { name = "probe" }, "create_domain"),
    ];

    public static TheoryData<string> Users => new(Poc.Domain.Directory.SeedUsers.All.Select(u => u.Id));

    private static Dictionary<(string User, string Resource, string Permission), bool> PrdMatrix()
    {
        var (_, assertions) = ValidationYaml.Load();
        var cells = Columns.Select(c => (c.Resource, c.Permission)).ToHashSet();
        var grouped = assertions
            .Where(a => a.Relationship.Subject.Type == "user" && cells.Contains((a.Relationship.Resource.ToString(), a.Relationship.Relation)))
            .GroupBy(a => (a.Relationship.Subject.Id, a.Relationship.Resource.ToString(), a.Relationship.Relation));
        // A tuple may be asserted more than once in the YAML (matrix cell and an extra boundary check); the copies must agree.
        return grouped.ToDictionary(g => g.Key, g => g.Select(a => a.Expected).Distinct().Single());
    }

    private static async Task<(HttpStatusCode Status, bool Granted)> AnswerAsync(HttpClient client, Column column)
    {
        using var response = await client.GetAsync(column.Url);
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, false);
        var dto = await response.JsonAsync();
        return (HttpStatusCode.OK, dto.Permission(column.Permission));
    }

    [Fact]
    public void The_matrix_read_from_the_yaml_is_complete()
    {
        Assert.Equal(70, PrdMatrix().Count); // 10 users x 7 columns, nothing silently dropped
    }

    [Fact]
    public void The_matrix_has_enough_denied_action_cells_for_the_refusal_probe_to_mean_something()
    {
        var denied = PrdMatrix().Count(c => !c.Value && c.Key.Permission != "view");
        Assert.True(denied >= 20, $"only {denied} denied action cells; the 403 probe would prove little");
    }

    [Theory]
    [MemberData(nameof(Users))]
    public async Task Every_cell_of_the_matrix_is_answered_correctly_over_HTTP(string user)
    {
        var matrix = PrdMatrix();
        var client = await AsAsync(user);
        var wrong = new List<string>();

        foreach (var column in Columns)
        {
            var expected = matrix[(user, column.Resource, column.Permission)];
            var (status, granted) = await AnswerAsync(client, column);
            if (status is not (HttpStatusCode.OK or HttpStatusCode.NotFound)) wrong.Add($"{user} {column.Resource}#{column.Permission}: unexpected status {status}");
            else if (granted != expected) wrong.Add($"{user} {column.Resource}#{column.Permission}: expected {expected} got {granted} ({status})");
        }

        Assert.Empty(wrong);
    }

    [Theory]
    [MemberData(nameof(Users))]
    public async Task A_cell_that_is_denied_is_also_refused_by_the_real_action_endpoint_never_trusting_the_permission_map(string user)
    {
        var client = await AsAsync(user);
        var matrix = PrdMatrix();

        foreach (var column in Columns)
        {
            // Only the action cells (not view) have a mutating endpoint whose permission equals the cell's.
            if (column.Permission == "view" || matrix[(user, column.Resource, column.Permission)]) continue;

            var (status, _) = await AnswerAsync(client, column);
            using var action = await client.PostJsonAsync(column.ActionUrl, column.ActionBody);
            if (status == HttpStatusCode.NotFound)
                await action.ExpectProblemAsync(HttpStatusCode.NotFound); // cannot even see it: the action must not reveal it
            else
                await action.ExpectProblemAsync(HttpStatusCode.Forbidden, permission: column.ActionPermission);
        }
    }

    [Fact]
    public async Task A_user_with_no_access_gets_nothing_anywhere_and_the_tree_is_empty()
    {
        var frank = await AsAsync("frank");

        foreach (var column in Columns)
            await (await frank.GetAsync(column.Url)).ExpectProblemAsync(HttpStatusCode.NotFound);
        var tree = await (await frank.GetAsync("/api/tree")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(0, tree.GetArrayLength());
    }
}
