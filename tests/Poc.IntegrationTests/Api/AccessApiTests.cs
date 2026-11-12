using System.Net;
using System.Text.Json;

namespace Poc.IntegrationTests.Api;

/// <summary>Grant management, effective access, the permission explorer, the audit and groups over HTTP.</summary>
public sealed class AccessApiTests : ApiTestBase
{
    private static string[] Ids(JsonElement array, string field = "id") =>
        array.EnumerateArray().Select(e => e.GetProperty(field).GetString()!).Order().ToArray();

    private static object UserSubject(string id) => new { type = "user", id };

    private static object GroupSubject(string id) => new { type = "group", id };

    // ---- Listing grants -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Direct_grants_show_who_granted_them_and_when()
    {
        var grants = await (await (await AsAsync("alice")).GetAsync("/api/access/domain/acme-uk/grants")).ExpectAsync(HttpStatusCode.OK);

        var direct = Assert.Single(grants.GetProperty("direct").EnumerateArray());
        Assert.Equal("administrator", direct.GetProperty("relation").GetString());
        var subject = direct.GetProperty("subject");
        Assert.Equal(("group", "uk-it", "member", "UK IT"), (subject.GetProperty("type").GetString(), subject.GetProperty("id").GetString(), subject.GetProperty("relation").GetString(), subject.GetProperty("displayName").GetString()));
        Assert.Equal("system", direct.GetProperty("grantedBy").GetString());
        Assert.NotEqual(JsonValueKind.Null, direct.GetProperty("grantedAt").ValueKind);
        Assert.Equal(["administrator", "viewer"], grants.GetProperty("grantableRelations").EnumerateArray().Select(r => r.GetString()!).ToArray());
    }

    [Fact]
    public async Task Inherited_grants_come_from_each_ancestors_administrators_and_the_platforms_support_labelled_with_their_source()
    {
        var grants = await (await (await AsAsync("bob")).GetAsync("/api/access/wallet/uk-ops/grants")).ExpectAsync(HttpStatusCode.OK);

        var inherited = grants.GetProperty("inherited").EnumerateArray()
            .Select(g => (g.GetProperty("sourceType").GetString()!, g.GetProperty("sourceName").GetString()!, g.GetProperty("relation").GetString()!,
                g.GetProperty("effect").GetString()!, g.GetProperty("subject").GetProperty("displayName").GetString()!))
            .OrderBy(g => g.Item1, StringComparer.Ordinal).ThenBy(g => g.Item3, StringComparer.Ordinal).ThenBy(g => g.Item5, StringComparer.Ordinal).ToList();

        Assert.Equal(
        [
            ("customer", "Acme Ltd", "administrator", "admin", "Alice Armstrong"),
            ("domain", "Acme UK", "administrator", "admin", "UK IT"),
            ("platform", "Platform", "administrator", "admin", "Priya Patel"),
            ("platform", "Platform", "support", "view", "Support team"),
        ], inherited);
        var direct = grants.GetProperty("direct").EnumerateArray().Single();
        Assert.Equal(("spender", "Finance AP"), (direct.GetProperty("relation").GetString(), direct.GetProperty("subject").GetProperty("displayName").GetString()));
    }

    [Fact]
    public async Task Platform_grants_have_no_inherited_section()
    {
        var grants = await (await (await AsAsync("priya")).GetAsync("/api/access/platform/main/grants")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(0, grants.GetProperty("inherited").GetArrayLength());
        Assert.Equal(["administrator", "support"], grants.GetProperty("direct").EnumerateArray().Select(g => g.GetProperty("relation").GetString()!).Order().ToArray());
    }

    [Fact]
    public async Task Listing_grants_needs_manage_access_so_a_viewer_is_told_and_a_stranger_is_not()
    {
        await (await (await AsAsync("erin")).GetAsync("/api/access/customer/acme/grants")).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "manage_access");
        await (await (await AsAsync("frank")).GetAsync("/api/access/customer/acme/grants")).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await (await AsAsync("sam")).GetAsync("/api/access/customer/acme/grants")).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "manage_access");
    }

    [Theory]
    [InlineData("/api/access/group/uk-it/grants")]         // groups are managed through their own routes
    [InlineData("/api/access/nonsense/x/grants")]
    [InlineData("/api/access/customer/Bad%23Id/grants")]
    public async Task Access_routes_for_something_that_cannot_take_grants_are_404(string url)
    {
        await (await (await AsAsync("priya")).GetAsync(url)).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    // ---- Grant and revoke -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Granting_and_revoking_round_trip_through_the_listing_and_the_audit()
    {
        var alice = await AsAsync("alice");

        var granted = await (await alice.PutJsonAsync("/api/access/domain/acme-ie/grants", new { relation = "viewer", subject = UserSubject("erin") })).ExpectAsync(HttpStatusCode.OK);
        Assert.True(granted.GetProperty("auditId").GetInt64() > 0);
        var listed = await (await alice.GetAsync("/api/access/domain/acme-ie/grants")).JsonAsync();
        var direct = Assert.Single(listed.GetProperty("direct").EnumerateArray());
        Assert.Equal(("viewer", "erin", "alice"), (direct.GetProperty("relation").GetString(), direct.GetProperty("subject").GetProperty("id").GetString(), direct.GetProperty("grantedBy").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await (await AsAsync("erin")).GetAsync("/api/domains/acme-ie")).StatusCode);

        await (await alice.DeleteAsync("/api/access/domain/acme-ie/grants?relation=viewer&subjectType=user&subjectId=erin")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(0, (await (await alice.GetAsync("/api/access/domain/acme-ie/grants")).JsonAsync()).GetProperty("direct").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await (await AsAsync("erin")).GetAsync("/api/domains/acme-ie")).StatusCode);

        var audit = await (await alice.GetAsync("/api/access/audit?type=domain&id=acme-ie")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Grant", "Revoke"], audit.EnumerateArray().Select(a => a.GetProperty("action").GetString()!).ToArray());
        Assert.All(audit.EnumerateArray(), a => Assert.Equal("Applied", a.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task A_group_can_be_granted_a_role_and_its_members_gain_it()
    {
        var alice = await AsAsync("alice");
        await (await alice.PutJsonAsync("/api/access/wallet/ie-ops/grants", new { relation = "spender", subject = GroupSubject("uk-it") })).ExpectAsync(HttpStatusCode.OK);

        foreach (var member in new[] { "bob", "heidi" }) // heidi through uk-contractors
            Assert.True((await (await (await AsAsync(member)).GetAsync("/api/wallets/ie-ops")).JsonAsync()).Permission("spend"));
    }

    [Theory]
    [InlineData("parent", "user", "frank", "unknown_relation")]
    [InlineData("operator", "user", "frank", "unknown_relation")]
    [InlineData("viewer", "user", "ghost", "unknown_subject")]
    [InlineData("viewer", "group", "ghost", "unknown_subject")]
    [InlineData("viewer", "customer", "acme", "invalid_subject")]
    [InlineData("viewer", "platform", "main", "invalid_subject")]
    public async Task A_malformed_grant_is_422_with_a_code_before_anything_is_written(string relation, string subjectType, string subjectId, string code)
    {
        var alice = await AsAsync("alice");

        await (await alice.PutJsonAsync("/api/access/customer/acme/grants", new { relation, subject = new { type = subjectType, id = subjectId } }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: code);

        var audit = await (await alice.GetAsync("/api/access/audit?type=customer&id=acme")).JsonAsync();
        Assert.DoesNotContain(audit.EnumerateArray(), a => a.GetProperty("subjectId").GetString() == subjectId);
    }

    [Fact]
    public async Task A_grant_request_missing_fields_is_422_not_a_server_error()
    {
        var alice = await AsAsync("alice");
        await (await alice.PutJsonAsync("/api/access/customer/acme/grants", new { })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "missing_field");
        await (await alice.PutJsonAsync("/api/access/customer/acme/grants", new { relation = "viewer" })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "missing_field");
        await (await alice.DeleteAsync("/api/access/customer/acme/grants")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "missing_field");
    }

    [Fact]
    public async Task Only_platform_admins_may_grant_a_platform_group_anywhere()
    {
        var support = GroupSubject("support-team");
        await (await (await AsAsync("alice")).PutJsonAsync("/api/access/customer/acme/grants", new { relation = "viewer", subject = support }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "admin");
        await (await (await AsAsync("priya")).PutJsonAsync("/api/access/customer/acme/grants", new { relation = "viewer", subject = support })).ExpectAsync(HttpStatusCode.OK);
    }

    // ---- Effective access -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Effective_access_resolves_through_groups_nesting_and_inheritance()
    {
        var alice = await AsAsync("alice");

        var spenders = await (await alice.GetAsync("/api/access/wallet/uk-ops/effective?permission=spend")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["alice", "bob", "carol", "heidi", "priya"], Ids(spenders));

        var viewers = await (await alice.GetAsync("/api/access/wallet/uk-ops/effective?permission=view")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["alice", "bob", "carol", "heidi", "priya", "sam"], Ids(viewers)); // support can view

        var cancellers = await (await alice.GetAsync("/api/access/subscription/m365-e3/effective?permission=cancel")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["alice", "bob", "heidi", "priya"], Ids(cancellers));
        Assert.Equal("Alice Armstrong", cancellers[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Effective_access_follows_a_change_immediately()
    {
        var alice = await AsAsync("alice");
        await (await alice.DeleteAsync("/api/groups/uk-it/members?type=group&memberId=uk-contractors")).ExpectAsync(HttpStatusCode.OK);

        var spenders = await (await alice.GetAsync("/api/access/wallet/uk-ops/effective?permission=spend")).JsonAsync();
        Assert.DoesNotContain("heidi", Ids(spenders));
    }

    [Fact]
    public async Task Effective_access_refuses_an_unknown_or_missing_permission()
    {
        var alice = await AsAsync("alice");
        await (await alice.GetAsync("/api/access/wallet/uk-ops/effective?permission=cancel")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "unknown_permission"); // a subscription permission
        await (await alice.GetAsync("/api/access/wallet/uk-ops/effective")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "missing_field");
    }

    // ---- Permission explorer --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_explorer_says_allowed_or_denied_for_any_user_and_shows_why()
    {
        var alice = await AsAsync("alice");

        var allowed = await (await alice.PostJsonAsync("/api/access/explain", new { userId = "heidi", permission = "spend", resourceType = "wallet", resourceId = "uk-ops" })).ExpectAsync(HttpStatusCode.OK);
        Assert.True(allowed.GetProperty("allowed").GetBoolean());
        var steps = allowed.GetProperty("trace").EnumerateArray().ToList();
        var root = steps[0];
        Assert.Equal((0, "wallet", "uk-ops", "spend"), (root.GetProperty("depth").GetInt32(), root.GetProperty("resourceType").GetString(), root.GetProperty("resourceId").GetString(), root.GetProperty("permission").GetString()));
        Assert.True(root.GetProperty("allowed").GetBoolean());
        Assert.Contains(steps, n => n.GetProperty("resourceType").GetString() == "group" && n.GetProperty("resourceId").GetString() == "uk-contractors");
        Assert.True(steps.Skip(1).All(n => n.GetProperty("depth").GetInt32() >= 1), "only the root is at depth 0");

        var denied = await (await alice.PostJsonAsync("/api/access/explain", new { userId = "dave", permission = "spend", resourceType = "wallet", resourceId = "uk-ops" })).ExpectAsync(HttpStatusCode.OK);
        Assert.False(denied.GetProperty("allowed").GetBoolean());
        Assert.False(denied.GetProperty("trace").EnumerateArray().First().GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task The_explorer_needs_manage_access_on_the_chosen_object_and_hides_what_the_caller_cannot_see()
    {
        object Body(string type, string id) => new { userId = "heidi", permission = "view", resourceType = type, resourceId = id };

        await (await (await AsAsync("carol")).PostJsonAsync("/api/access/explain", Body("wallet", "uk-ops"))).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "manage_access");
        await (await (await AsAsync("bob")).PostJsonAsync("/api/access/explain", Body("customer", "acme"))).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await (await AsAsync("frank")).PostJsonAsync("/api/access/explain", Body("wallet", "uk-ops"))).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await (await AsAsync("bob")).PostJsonAsync("/api/access/explain", Body("wallet", "uk-ops"))).ExpectAsync(HttpStatusCode.OK); // Bob manages access on his domain's wallets
    }

    [Fact]
    public async Task The_explorer_refuses_bad_requests_without_consulting_the_engine_about_nonsense()
    {
        var alice = await AsAsync("alice");
        await (await alice.PostJsonAsync("/api/access/explain", new { userId = "heidi", permission = "view", resourceType = "nonsense", resourceId = "x" })).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await alice.PostJsonAsync("/api/access/explain", new { userId = "heidi", permission = "view", resourceType = "wallet", resourceId = "A#B" })).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await alice.PostJsonAsync("/api/access/explain", new { userId = "ghost", permission = "view", resourceType = "wallet", resourceId = "uk-ops" })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "unknown_subject");
        await (await alice.PostJsonAsync("/api/access/explain", new { userId = "heidi", permission = "cancel", resourceType = "wallet", resourceId = "uk-ops" })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "unknown_permission");
        await (await alice.PostJsonAsync("/api/access/explain", new { })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "missing_field");
    }

    [Fact]
    public async Task The_audit_endpoint_needs_manage_access_and_a_well_formed_target()
    {
        await (await (await AsAsync("frank")).GetAsync("/api/access/audit?type=customer&id=acme")).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await (await AsAsync("erin")).GetAsync("/api/access/audit?type=customer&id=acme")).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "manage_access");
        await (await (await AsAsync("alice")).GetAsync("/api/access/audit")).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await (await AsAsync("alice")).GetAsync("/api/access/audit?type=group&id=uk-it")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    // ---- Grantable groups (the Access tab's group picker) -----------------------------------------------------

    [Fact]
    public async Task A_domain_admin_is_offered_the_customers_groups_even_though_he_cannot_view_the_customer_or_its_group_list()
    {
        var bob = await AsAsync("bob");
        await (await bob.GetAsync("/api/customers/acme/groups")).ExpectProblemAsync(HttpStatusCode.NotFound); // why the picker needs its own list

        var groups = await (await bob.GetAsync("/api/access/domain/acme-uk/grantable-groups")).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(["finance-ap", "uk-contractors", "uk-it"], Ids(groups));
        Assert.All(groups.EnumerateArray(), g => Assert.Equal("Customer", g.GetProperty("ownerType").GetString()));
    }

    [Fact]
    public async Task Platform_groups_are_offered_only_to_platform_admins_and_every_offered_group_passes_the_service_rules()
    {
        var alice = await AsAsync("alice");
        var priya = await AsAsync("priya");

        Assert.DoesNotContain("support-team", Ids(await (await alice.GetAsync("/api/access/wallet/uk-ops/grantable-groups")).JsonAsync()));
        var forPriya = await (await priya.GetAsync("/api/access/wallet/uk-ops/grantable-groups")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["finance-ap", "support-team", "uk-contractors", "uk-it"], Ids(forPriya));
        Assert.Equal(["support-team"], Ids(await (await priya.GetAsync("/api/access/platform/main/grantable-groups")).ExpectAsync(HttpStatusCode.OK))); // a customer's groups cannot be granted on the platform

        // Each offered group really is grantable there (the offer and the rule must agree).
        foreach (var id in Ids(forPriya))
            await (await priya.PutJsonAsync("/api/access/wallet/uk-ops/grants", new { relation = "viewer", subject = new { type = "group", id } })).ExpectAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Another_customers_groups_are_never_offered_and_the_list_needs_manage_access()
    {
        Assert.Equal(0, (await (await (await AsAsync("grace")).GetAsync("/api/access/wallet/globex-ops/grantable-groups")).ExpectAsync(HttpStatusCode.OK)).GetArrayLength());
        await (await (await AsAsync("erin")).GetAsync("/api/access/customer/acme/grantable-groups")).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "manage_access");
        await (await (await AsAsync("frank")).GetAsync("/api/access/customer/acme/grantable-groups")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Group_detail_carries_the_callers_permissions_so_edit_controls_can_be_disabled()
    {
        var admin = await (await (await AsAsync("alice")).GetAsync("/api/groups/uk-it")).ExpectAsync(HttpStatusCode.OK);
        Assert.True(admin.Permission("manage"));
        Assert.True(admin.Permission("view"));

        var member = await (await (await AsAsync("bob")).GetAsync("/api/groups/uk-it")).ExpectAsync(HttpStatusCode.OK);
        Assert.False(member.Permission("manage")); // a member can see the group but not change it
        Assert.True(member.Permission("view"));
    }

    // ---- Groups ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Customer_groups_are_listed_only_to_those_who_can_view_them()
    {
        Assert.Equal(["finance-ap", "uk-contractors", "uk-it"], Ids(await (await (await AsAsync("alice")).GetAsync("/api/customers/acme/groups")).ExpectAsync(HttpStatusCode.OK)));
        Assert.Equal(["finance-ap", "uk-contractors", "uk-it"], Ids(await (await (await AsAsync("priya")).GetAsync("/api/customers/acme/groups")).ExpectAsync(HttpStatusCode.OK)));
        // Heidi can view the customer's groups she belongs to (uk-contractors directly, uk-it through nesting) but not Carol's.
        // She cannot view Acme itself, so the list route is a 404 for her; Erin can view Acme but no group.
        await (await (await AsAsync("heidi")).GetAsync("/api/customers/acme/groups")).ExpectProblemAsync(HttpStatusCode.NotFound);
        Assert.Equal(0, (await (await (await AsAsync("erin")).GetAsync("/api/customers/acme/groups")).ExpectAsync(HttpStatusCode.OK)).GetArrayLength());
        await (await (await AsAsync("grace")).GetAsync("/api/customers/acme/groups")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_customer_admin_creates_a_group_manages_its_members_and_deletes_it()
    {
        var alice = await AsAsync("alice");
        var created = await (await alice.PostJsonAsync("/api/customers/acme/groups", new { name = "Auditors" })).ExpectAsync(HttpStatusCode.Created);
        var id = created.GetProperty("id").GetString()!;
        Assert.Contains(id, Ids(await (await alice.GetAsync("/api/customers/acme/groups")).JsonAsync()));

        await (await alice.PutJsonAsync($"/api/groups/{id}/members", UserSubject("frank"))).ExpectAsync(HttpStatusCode.OK);
        await (await alice.PutJsonAsync($"/api/groups/{id}/members", GroupSubject("finance-ap"))).ExpectAsync(HttpStatusCode.OK);
        var detail = await (await alice.GetAsync($"/api/groups/{id}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["finance-ap", "frank"], detail.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("subject").GetProperty("id").GetString()!).Order().ToArray());
        Assert.Equal(HttpStatusCode.OK, (await (await AsAsync("frank")).GetAsync($"/api/groups/{id}")).StatusCode); // a member can see their group

        await (await alice.DeleteAsync($"/api/groups/{id}/members?type=user&memberId=frank")).ExpectAsync(HttpStatusCode.OK);
        await (await (await AsAsync("frank")).GetAsync($"/api/groups/{id}")).ExpectProblemAsync(HttpStatusCode.NotFound);

        await (await alice.DeleteAsync($"/api/groups/{id}")).ExpectAsync(HttpStatusCode.NoContent);
        await (await alice.GetAsync($"/api/groups/{id}")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Group_detail_shows_members_and_everywhere_the_group_is_granted_or_nested()
    {
        var detail = await (await (await AsAsync("alice")).GetAsync("/api/groups/uk-it")).ExpectAsync(HttpStatusCode.OK);

        var members = detail.GetProperty("members").EnumerateArray().Select(m => (m.GetProperty("subject").GetProperty("type").GetString()!, m.GetProperty("subject").GetProperty("displayName").GetString()!)).Order().ToArray();
        Assert.Equal([("group", "UK contractors"), ("user", "Bob Baker")], members);
        var grantedOn = Assert.Single(detail.GetProperty("grantedOn").EnumerateArray());
        Assert.Equal(("domain", "acme-uk", "Acme UK", "administrator"),
            (grantedOn.GetProperty("resourceType").GetString(), grantedOn.GetProperty("resourceId").GetString(), grantedOn.GetProperty("resourceName").GetString(), grantedOn.GetProperty("relation").GetString()));

        var contractors = await (await (await AsAsync("alice")).GetAsync("/api/groups/uk-contractors")).JsonAsync();
        var nested = Assert.Single(contractors.GetProperty("grantedOn").EnumerateArray());
        Assert.Equal(("group", "UK IT"), (nested.GetProperty("resourceType").GetString(), nested.GetProperty("resourceName").GetString()));
    }

    [Fact]
    public async Task Group_changes_need_manage_on_the_group_so_a_mere_member_is_refused()
    {
        var bob = await AsAsync("bob"); // a member of uk-it, not an administrator of Acme
        await (await bob.PutJsonAsync("/api/groups/uk-it/members", UserSubject("frank"))).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "manage");
        await (await bob.DeleteAsync("/api/groups/uk-it")).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "manage");
        await (await (await AsAsync("frank")).PutJsonAsync("/api/groups/uk-it/members", UserSubject("frank"))).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Membership_requests_for_unknown_or_malformed_members_are_422()
    {
        var alice = await AsAsync("alice");
        await (await alice.PutJsonAsync("/api/groups/uk-it/members", UserSubject("ghost"))).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "unknown_subject");
        await (await alice.PutJsonAsync("/api/groups/uk-it/members", GroupSubject("ghost"))).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "unknown_subject");
        await (await alice.PutJsonAsync("/api/groups/uk-it/members", new { type = "customer", id = "acme" })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "invalid_subject");
        await (await alice.PutJsonAsync("/api/groups/uk-it/members", new { })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "missing_field");
    }

    [Fact]
    public async Task Platform_groups_are_for_platform_admins_and_visible_to_their_own_members()
    {
        var priya = await AsAsync("priya");
        Assert.Equal(["support-team"], Ids(await (await priya.GetAsync("/api/platform/groups")).ExpectAsync(HttpStatusCode.OK)));
        Assert.Equal(["support-team"], Ids(await (await (await AsAsync("sam")).GetAsync("/api/platform/groups")).ExpectAsync(HttpStatusCode.OK))); // Sam is a member
        await (await (await AsAsync("alice")).GetAsync("/api/platform/groups")).ExpectProblemAsync(HttpStatusCode.NotFound);

        await (await (await AsAsync("sam")).PostJsonAsync("/api/platform/groups", new { name = "Nope" })).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "create_group");
        var created = await (await priya.PostJsonAsync("/api/platform/groups", new { name = "Engineers" })).ExpectAsync(HttpStatusCode.Created);
        await (await priya.PutJsonAsync($"/api/groups/{created.GetProperty("id").GetString()}/members", UserSubject("frank"))).ExpectAsync(HttpStatusCode.OK);
        await (await (await AsAsync("alice")).PutJsonAsync($"/api/groups/{created.GetProperty("id").GetString()}/members", UserSubject("frank"))).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Creating_a_group_needs_create_group_on_the_owner()
    {
        await (await (await AsAsync("erin")).PostJsonAsync("/api/customers/acme/groups", new { name = "X" })).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "create_group");
        await (await (await AsAsync("grace")).PostJsonAsync("/api/customers/acme/groups", new { name = "X" })).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await (await AsAsync("alice")).PostJsonAsync("/api/customers/acme/groups", new { })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "missing_field");
    }
}
