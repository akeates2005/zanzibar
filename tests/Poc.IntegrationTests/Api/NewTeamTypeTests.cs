using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Authorization;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// NEXT-STEPS step 3, "Done when": a type with grantable roles added to the deployed schema works through the API with no
/// C# change. The API here runs schema.zed plus a <c>shipping/parcel</c> type this application has no table, constant or
/// code for. Its objects exist only as <c>parent</c> tuples, written here as the owning team's projector would write them.
/// </summary>
public sealed class NewTeamTypeTests : ApiTestBase
{
    private const string Parcel = "shipping/parcel";

    private const string ParcelDefinition = """

        definition shipping/parcel {
            relation parent: customer
            relation handler: user | group#member

            permission admin = parent->admin
            permission view = handler + admin + parent->support_view
            permission manage_access = admin
        }
        """;

    private string? _schemaPath;

    public override async Task InitializeAsync()
    {
        _schemaPath = Path.Combine(Path.GetTempPath(), $"poc-schema-{Guid.NewGuid():N}.zed");
        await File.WriteAllTextAsync(_schemaPath, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "authz", "schema.zed")) + ParcelDefinition);
        Host = await ApiHost.StartAsync(settings => settings["Poc:SchemaPath"] = _schemaPath);

        // The shipping team's projector, not this application, creates parcels: p1 under Acme, p2 under Globex.
        await Host.Services.GetRequiredService<IRelationshipStore>().WriteAsync(
        [
            RelationshipUpdate.Touch(new Relationship(new ResourceRef(Parcel, "p1"), "parent", new SubjectRef("customer", "acme"))),
            RelationshipUpdate.Touch(new Relationship(new ResourceRef(Parcel, "p2"), "parent", new SubjectRef("customer", "globex"))),
        ], Ct);
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_schemaPath is not null) File.Delete(_schemaPath);
    }

    private static string Q(string id) => $"type={Uri.EscapeDataString(Parcel)}&id={id}";

    [Fact]
    public async Task The_access_page_data_for_another_teams_object_comes_from_the_engine()
    {
        var alice = await AsAsync("alice");
        var page = await (await alice.GetAsync($"/api/access/object?{Q("p1")}")).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal("p1", page.GetProperty("name").GetString()); // no application record, so the id
        Assert.Equal(["admin", "manage_access", "view"], page.GetProperty("permissions").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(page.Permission("manage_access"));
        // Alice cannot view the platform, so it is a label with no id; her customer is openable (PRD: Resource browser).
        Assert.Equal(["platform:-:False", "customer:acme:True"], page.GetProperty("ancestors").EnumerateArray()
            .Select(a => $"{a.GetProperty("type").GetString()}:{a.GetProperty("id").GetString() ?? "-"}:{a.GetProperty("viewable").GetBoolean()}"));
    }

    [Fact]
    public async Task Grants_on_another_teams_object_offer_its_roles_and_show_inherited_administrators()
    {
        var grants = await (await (await AsAsync("alice")).GetAsync($"/api/access/grants?{Q("p1")}")).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(["handler"], grants.GetProperty("grantableRelations").EnumerateArray().Select(r => r.GetString()));
        var inherited = grants.GetProperty("inherited").EnumerateArray()
            .Select(i => $"{i.GetProperty("sourceType").GetString()}#{i.GetProperty("relation").GetString()}@{i.GetProperty("subject").GetProperty("id").GetString()}")
            .ToList();
        Assert.Contains("customer#administrator@alice", inherited);
        Assert.Contains("platform#administrator@priya", inherited);
    }

    [Fact]
    public async Task A_role_on_another_teams_object_can_be_granted_takes_effect_and_can_be_revoked()
    {
        var alice = await AsAsync("alice");
        var dave = await AsAsync("dave");
        await (await dave.GetAsync($"/api/access/object?{Q("p1")}")).ExpectProblemAsync(HttpStatusCode.NotFound);

        await (await alice.PutJsonAsync($"/api/access/grants?{Q("p1")}", new { relation = "handler", subject = new { type = "user", id = "dave" } })).ExpectAsync(HttpStatusCode.OK);
        var holders = await (await alice.GetAsync($"/api/access/effective?{Q("p1")}&permission=view")).ExpectAsync(HttpStatusCode.OK);
        Assert.Contains("dave", holders.EnumerateArray().Select(h => h.GetProperty("id").GetString()));
        // A handler can view the parcel but not manage its access: 403 naming the permission, not 404.
        await (await dave.GetAsync($"/api/access/object?{Q("p1")}")).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "manage_access");

        await (await alice.DeleteAsync($"/api/access/grants?{Q("p1")}&relation=handler&subjectType=user&subjectId=dave")).ExpectAsync(HttpStatusCode.OK);
        await (await dave.GetAsync($"/api/access/object?{Q("p1")}")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_group_scope_rule_uses_the_engines_hierarchy_for_another_teams_object()
    {
        // uk-it belongs to Acme: offered and grantable on the Acme parcel, neither on the Globex parcel.
        var alice = await AsAsync("alice");
        var offered = await (await alice.GetAsync($"/api/access/grantable-groups?{Q("p1")}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Contains("uk-it", offered.EnumerateArray().Select(g => g.GetProperty("id").GetString()));
        await (await alice.PutJsonAsync($"/api/access/grants?{Q("p1")}", new { relation = "handler", subject = new { type = "group", id = "uk-it" } })).ExpectAsync(HttpStatusCode.OK);

        var grace = await AsAsync("grace");
        var offeredOnGlobex = await (await grace.GetAsync($"/api/access/grantable-groups?{Q("p2")}")).ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain("uk-it", offeredOnGlobex.EnumerateArray().Select(g => g.GetProperty("id").GetString()));
        await (await grace.PutJsonAsync($"/api/access/grants?{Q("p2")}", new { relation = "handler", subject = new { type = "group", id = "uk-it" } }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "group_owner_mismatch");
    }

    [Fact]
    public async Task An_object_with_no_parent_tuple_does_not_exist()
    {
        var priya = await AsAsync("priya");
        await (await priya.GetAsync($"/api/access/object?{Q("nowhere")}")).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await priya.PutJsonAsync($"/api/access/grants?{Q("nowhere")}", new { relation = "handler", subject = new { type = "user", id = "dave" } }))
            .ExpectProblemAsync(HttpStatusCode.NotFound);
    }
}
