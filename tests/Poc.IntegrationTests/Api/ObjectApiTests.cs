using System.Net;
using System.Text;
using System.Text.Json;
using Poc.Application.Authorization;

namespace Poc.IntegrationTests.Api;

/// <summary>Customers, domains, wallets and subscriptions over HTTP: lifecycle, validation, listings and the tree.</summary>
public sealed class ObjectApiTests : ApiTestBase
{
    private static readonly string[] BobsTree = ["Acme UK", "Azure Plan", "GitHub Enterprise", "Microsoft 365 E3", "UK Operations", "UK R&D"];

    private static string Id(JsonElement dto) => dto.GetProperty("id").GetString()!;

    private static string[] Names(JsonElement array) => array.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).Order().ToArray();

    // ---- Creation chain and the shape of responses ---------------------------------------------------------------

    [Fact]
    public async Task Priya_builds_a_whole_branch_and_every_response_describes_it_for_the_caller()
    {
        var priya = await AsAsync("priya");

        var customerResponse = await priya.PostJsonAsync("/api/customers", new { name = "Initech" });
        var customer = await customerResponse.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal($"/api/customers/{Id(customer)}", customerResponse.Headers.Location?.ToString());
        Assert.Equal("priya", customer.GetProperty("createdBy").GetString());
        Assert.True(customer.Permission("delete")); // a platform admin may delete customers
        Assert.Equal(["Platform"], customer.GetProperty("ancestors").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToArray());

        var domain = await (await priya.PostJsonAsync($"/api/customers/{Id(customer)}/domains", new { name = "Main" })).ExpectAsync(HttpStatusCode.Created);
        var wallet = await (await priya.PostJsonAsync($"/api/domains/{Id(domain)}/wallets", new { name = "Ops" })).ExpectAsync(HttpStatusCode.Created);
        var subscription = await (await priya.PostJsonAsync($"/api/wallets/{Id(wallet)}/subscriptions", new { productName = "Tools", quantity = 3 })).ExpectAsync(HttpStatusCode.Created);

        Assert.Equal(Id(domain), wallet.GetProperty("domainId").GetString());
        Assert.Equal(0m, wallet.GetProperty("balance").GetDecimal());
        Assert.Equal("Active", subscription.GetProperty("status").GetString());
        Assert.Equal(["Platform", "Initech", "Main", "Ops"], subscription.GetProperty("ancestors").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToArray());
        Assert.All(subscription.GetProperty("ancestors").EnumerateArray(), a => Assert.True(a.GetProperty("viewable").GetBoolean()));
    }

    [Fact]
    public async Task The_permission_map_holds_every_permission_of_the_type_and_matches_the_schema()
    {
        var wallet = await (await (await AsAsync("carol")).GetAsync("/api/wallets/uk-ops")).ExpectAsync(HttpStatusCode.OK);
        var keys = wallet.GetProperty("permissions").EnumerateObject().Select(p => p.Name).Order().ToArray();

        Assert.Equal(SchemaCatalog.PermissionsOf("wallet").Order(), keys);
        Assert.True(wallet.Permission("spend"));
        Assert.True(wallet.Permission("create_subscription"));
        Assert.False(wallet.Permission("top_up"));
        Assert.False(wallet.Permission("move"));
    }

    [Fact]
    public async Task Ancestors_the_caller_cannot_view_appear_as_labels_without_an_id()
    {
        var sub = await (await (await AsAsync("dave")).GetAsync("/api/subscriptions/m365-e3")).ExpectAsync(HttpStatusCode.OK);

        var ancestors = sub.GetProperty("ancestors").EnumerateArray().ToList();
        Assert.Equal(["Platform", "Acme Ltd", "Acme UK", "UK Operations"], ancestors.Select(a => a.GetProperty("name").GetString()!).ToArray());
        Assert.All(ancestors, a =>
        {
            Assert.False(a.GetProperty("viewable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, a.GetProperty("id").ValueKind); // a label, never something to open
        });
    }

    // ---- Updates --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Renaming_bumps_the_row_version_and_needs_edit()
    {
        var alice = await AsAsync("alice");
        var before = await (await alice.GetAsync("/api/domains/acme-ie")).ExpectAsync(HttpStatusCode.OK);

        var renamed = await (await alice.PatchJsonAsync("/api/domains/acme-ie", new { name = "Acme Éire" })).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal("Acme Éire", renamed.GetProperty("name").GetString());
        Assert.Equal(before.GetProperty("rowVersion").GetInt32() + 1, renamed.GetProperty("rowVersion").GetInt32());
        await (await (await AsAsync("erin")).PatchJsonAsync("/api/customers/acme", new { name = "Hacked" })).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "edit");
        await (await (await AsAsync("bob")).PatchJsonAsync("/api/domains/acme-ie", new { name = "Hacked" })).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Topping_up_adds_to_the_balance_and_only_to_positive_amounts()
    {
        var alice = await AsAsync("alice");

        var topped = await (await alice.PostJsonAsync("/api/wallets/uk-ops/top-up", new { amount = 250.50m })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(5250.50m, topped.GetProperty("balance").GetDecimal());
        foreach (var bad in new[] { 0m, -5m })
            await (await alice.PostJsonAsync("/api/wallets/uk-ops/top-up", new { amount = bad })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "domain_rule");
        Assert.Equal(5250.50m, (await (await alice.GetAsync("/api/wallets/uk-ops")).JsonAsync()).GetProperty("balance").GetDecimal());
    }

    [Fact]
    public async Task A_subscriptions_quantity_and_cancellation_follow_their_rules()
    {
        var alice = await AsAsync("alice");

        await (await alice.PostJsonAsync("/api/subscriptions/m365-e3/quantity", new { quantity = 0 })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity);
        var changed = await (await alice.PostJsonAsync("/api/subscriptions/m365-e3/quantity", new { quantity = 40 })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(40, changed.GetProperty("quantity").GetInt32());

        var cancelled = await (await alice.PostJsonAsync("/api/subscriptions/m365-e3/cancel")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        await (await alice.PostJsonAsync("/api/subscriptions/m365-e3/cancel")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity);
        await (await alice.PostJsonAsync("/api/subscriptions/m365-e3/quantity", new { quantity = 5 })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity);
    }

    // ---- Validation -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_refused(string name)
    {
        var priya = await AsAsync("priya");
        await (await priya.PostJsonAsync("/api/customers", new { name })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity);
        await (await priya.PostJsonAsync("/api/customers", new { })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "missing_field");
    }

    [Fact]
    public async Task An_overlong_name_and_a_nonsense_quantity_are_refused()
    {
        var priya = await AsAsync("priya");
        await (await priya.PostJsonAsync("/api/customers", new { name = new string('x', 201) })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity);
        await (await priya.PostJsonAsync("/api/wallets/uk-ops/subscriptions", new { productName = "X", quantity = -1 })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Malformed_json_is_a_400_with_problem_details()
    {
        var priya = await AsAsync("priya");
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");
        await (await priya.PostAsync("/api/customers", content)).ExpectProblemAsync(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/api/customers/Not_A_Valid_Id")]
    [InlineData("/api/customers/a%23member")]
    [InlineData("/api/wallets/%2A")]
    [InlineData("/api/customers/does-not-exist")]
    public async Task Malformed_or_unknown_ids_are_404_and_never_reach_the_engine_as_a_tuple(string url)
    {
        await (await (await AsAsync("priya")).GetAsync(url)).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unknown_routes_are_404_problem_details()
    {
        await (await (await AsAsync("priya")).GetAsync("/api/no/such/route/at/all")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_domain_name_must_be_unique_within_its_customer()
    {
        var alice = await AsAsync("alice");
        await (await alice.PostJsonAsync("/api/customers/acme/domains", new { name = "Acme UK" })).ExpectProblemAsync(HttpStatusCode.Conflict);
        await (await (await AsAsync("grace")).PostJsonAsync("/api/customers/globex/domains", new { name = "Acme UK" })).ExpectAsync(HttpStatusCode.Created); // another customer may reuse it
    }

    // ---- Create permissions ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_customer_is_for_platform_admins_and_the_platform_hides_itself_from_those_who_cannot_see_it()
    {
        await (await (await AsAsync("sam")).PostJsonAsync("/api/customers", new { name = "X" })).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "create_customer");
        await (await (await AsAsync("alice")).PostJsonAsync("/api/customers", new { name = "X" })).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await (await AsAsync("frank")).PostJsonAsync("/api/customers", new { name = "X" })).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_customer_admin_can_create_a_domain_but_a_viewer_cannot()
    {
        await (await (await AsAsync("alice")).PostJsonAsync("/api/customers/acme/domains", new { name = "Acme DE" })).ExpectAsync(HttpStatusCode.Created);
        await (await (await AsAsync("erin")).PostJsonAsync("/api/customers/acme/domains", new { name = "Nope" })).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "create_domain");
    }

    // ---- Deletes --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_needs_admin_on_the_parent_not_on_the_object_itself()
    {
        // Bob administers acme-uk and so runs its wallets, but deleting a wallet needs admin on its parent: he has that
        // too (acme-uk is the parent). Alice cannot delete the customer she administers: that needs the platform.
        await (await (await AsAsync("alice")).DeleteAsync("/api/customers/acme")).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "delete");

        var priya = await AsAsync("priya");
        var customer = await (await priya.PostJsonAsync("/api/customers", new { name = "Temp" })).ExpectAsync(HttpStatusCode.Created);
        await (await priya.DeleteAsync($"/api/customers/{Id(customer)}")).ExpectAsync(HttpStatusCode.NoContent);
        await (await priya.GetAsync($"/api/customers/{Id(customer)}")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_wallet_administrator_can_run_a_wallet_but_cannot_remove_it()
    {
        var alice = await AsAsync("alice");
        await (await alice.PutJsonAsync("/api/access/wallet/ie-ops/grants", new { relation = "administrator", subject = new { type = "user", id = "frank" } })).ExpectAsync(HttpStatusCode.OK);
        var frank = await AsAsync("frank");

        await (await frank.PostJsonAsync("/api/wallets/ie-ops/top-up", new { amount = 1 })).ExpectAsync(HttpStatusCode.OK);
        await (await frank.DeleteAsync("/api/wallets/ie-ops")).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "delete");
    }

    [Fact]
    public async Task Objects_with_children_cannot_be_deleted_so_no_child_outlives_the_parent_its_admins_come_from()
    {
        var alice = await AsAsync("alice");
        await (await alice.DeleteAsync("/api/wallets/uk-ops")).ExpectProblemAsync(HttpStatusCode.Conflict); // has subscriptions
        await (await (await AsAsync("priya")).DeleteAsync("/api/customers/acme")).ExpectProblemAsync(HttpStatusCode.Conflict); // has domains
        await (await alice.DeleteAsync("/api/subscriptions/azure-plan")).ExpectAsync(HttpStatusCode.NoContent);
        await (await alice.GetAsync("/api/subscriptions/azure-plan")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_customer_that_still_owns_groups_cannot_be_deleted()
    {
        var priya = await AsAsync("priya");
        var customer = await (await priya.PostJsonAsync("/api/customers", new { name = "Has groups" })).ExpectAsync(HttpStatusCode.Created);
        await (await priya.PostJsonAsync($"/api/customers/{Id(customer)}/groups", new { name = "Admins" })).ExpectAsync(HttpStatusCode.Created);

        await (await priya.DeleteAsync($"/api/customers/{Id(customer)}")).ExpectProblemAsync(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Deleting_a_whole_branch_leaf_first_leaves_no_tuples_behind()
    {
        var priya = await AsAsync("priya");
        var customer = Id(await (await priya.PostJsonAsync("/api/customers", new { name = "Gone soon" })).ExpectAsync(HttpStatusCode.Created));
        var domain = Id(await (await priya.PostJsonAsync($"/api/customers/{customer}/domains", new { name = "D" })).ExpectAsync(HttpStatusCode.Created));
        var wallet = Id(await (await priya.PostJsonAsync($"/api/domains/{domain}/wallets", new { name = "W" })).ExpectAsync(HttpStatusCode.Created));
        var sub = Id(await (await priya.PostJsonAsync($"/api/wallets/{wallet}/subscriptions", new { productName = "S", quantity = 1 })).ExpectAsync(HttpStatusCode.Created));

        foreach (var url in new[] { $"/api/subscriptions/{sub}", $"/api/wallets/{wallet}", $"/api/domains/{domain}", $"/api/customers/{customer}" })
            await (await priya.DeleteAsync(url)).ExpectAsync(HttpStatusCode.NoContent);

        var store = (IRelationshipStore)Host.Services.GetService(typeof(IRelationshipStore))!;
        foreach (var (type, id) in new[] { ("subscription", sub), ("wallet", wallet), ("domain", domain), ("customer", customer) })
            Assert.Empty(await store.ReadAsync(new RelationshipFilter(type, id), Ct));
    }

    // ---- Children and the tree ------------------------------------------------------------------------------------

    [Fact]
    public async Task Children_lists_only_what_the_caller_can_view_and_needs_view_on_the_parent()
    {
        Assert.Equal(["Acme Ireland", "Acme UK"], Names(await (await (await AsAsync("alice")).GetAsync("/api/customer/acme/children")).ExpectAsync(HttpStatusCode.OK)));
        Assert.Equal(["UK Operations", "UK R&D"], Names(await (await (await AsAsync("bob")).GetAsync("/api/domain/acme-uk/children")).ExpectAsync(HttpStatusCode.OK)));

        await (await (await AsAsync("bob")).GetAsync("/api/customer/acme/children")).ExpectProblemAsync(HttpStatusCode.NotFound); // cannot see Acme
        // Erin can view Acme but, as a customer viewer, nothing below it: the list must be empty, not the domains she cannot view.
        Assert.Equal(0, (await (await (await AsAsync("erin")).GetAsync("/api/customer/acme/children")).ExpectAsync(HttpStatusCode.OK)).GetArrayLength());
        await (await (await AsAsync("carol")).GetAsync("/api/domain/acme-uk/children")).ExpectProblemAsync(HttpStatusCode.NotFound);
        // Carol can see the wallet but spenders do not inherit view of subscriptions: an empty list, not a leak.
        Assert.Equal(0, (await (await (await AsAsync("carol")).GetAsync("/api/wallet/uk-ops/children")).ExpectAsync(HttpStatusCode.OK)).GetArrayLength());
        await (await (await AsAsync("dave")).GetAsync("/api/wallet/uk-ops/children")).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await (await AsAsync("priya")).GetAsync("/api/subscription/m365-e3/children")).ExpectProblemAsync(HttpStatusCode.NotFound); // a leaf has none
    }

    [Fact]
    public async Task The_platform_lists_every_customer_to_its_admin_and_to_support()
    {
        Assert.Equal(["Acme Ltd", "Globex Corporation"], Names(await (await (await AsAsync("priya")).GetAsync("/api/platform/main/children")).ExpectAsync(HttpStatusCode.OK)));
        Assert.Equal(["Acme Ltd", "Globex Corporation"], Names(await (await (await AsAsync("sam")).GetAsync("/api/platform/main/children")).ExpectAsync(HttpStatusCode.OK)));
        await (await (await AsAsync("alice")).GetAsync("/api/platform/main/children")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_tree_shows_each_user_exactly_what_they_may_view_with_unviewable_ancestors_as_labels()
    {
        var priya = await (await (await AsAsync("priya")).GetAsync("/api/tree")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(14, priya.GetArrayLength()); // 2 customers + 3 domains + 4 wallets + 5 subscriptions

        var bob = await (await (await AsAsync("bob")).GetAsync("/api/tree")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(BobsTree, Names(bob));
        var domain = bob.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "acme-uk");
        var crumbs = domain.GetProperty("ancestors").EnumerateArray().ToList();
        Assert.Equal(["Platform", "Acme Ltd"], crumbs.Select(c => c.GetProperty("name").GetString()!).ToArray());
        Assert.All(crumbs, c => Assert.False(c.GetProperty("viewable").GetBoolean())); // Bob cannot view either ancestor

        var dave = await (await (await AsAsync("dave")).GetAsync("/api/tree")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Microsoft 365 E3"], Names(dave));
        var erin = await (await (await AsAsync("erin")).GetAsync("/api/tree")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Acme Ltd"], Names(erin)); // a customer viewer sees the customer and nothing below it
        var sam = await (await (await AsAsync("sam")).GetAsync("/api/tree")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(14, sam.GetArrayLength());

        var acme = priya.EnumerateArray().Single(n => n.GetProperty("id").GetString() == "acme");
        Assert.True(Assert.Single(acme.GetProperty("ancestors").EnumerateArray()).GetProperty("viewable").GetBoolean());
    }

    [Fact]
    public async Task A_newly_created_object_appears_in_its_creators_tree_immediately()
    {
        var alice = await AsAsync("alice");
        var created = await (await alice.PostJsonAsync("/api/domains/acme-ie/wallets", new { name = "Fresh" })).ExpectAsync(HttpStatusCode.Created);

        var tree = await (await alice.GetAsync("/api/tree")).ExpectAsync(HttpStatusCode.OK);
        Assert.Contains(tree.EnumerateArray(), n => n.GetProperty("id").GetString() == Id(created));
    }

    // ---- Directory ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task User_search_matches_name_email_and_id_and_treats_wildcards_literally()
    {
        var frank = await AsAsync("frank"); // any signed-in user, even one with no access at all
        var alice = await (await frank.GetAsync("/api/users?query=alice")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["alice"], alice.EnumerateArray().Select(u => u.GetProperty("id").GetString()!).ToArray());
        Assert.Equal("alice@zanzibar.example", alice[0].GetProperty("email").GetString());

        Assert.Equal(["heidi"], (await (await frank.GetAsync("/api/users?query=Hughes")).JsonAsync()).EnumerateArray().Select(u => u.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(10, (await (await frank.GetAsync("/api/users?query=zanzibar.example")).JsonAsync()).GetArrayLength());
        Assert.Equal(0, (await (await frank.GetAsync("/api/users?query=%25")).JsonAsync()).GetArrayLength()); // a literal %, not "everything"
        Assert.Equal(0, (await (await frank.GetAsync("/api/users?query=_")).JsonAsync()).GetArrayLength());
        Assert.Equal(10, (await (await frank.GetAsync("/api/users")).JsonAsync()).GetArrayLength());
    }
}
