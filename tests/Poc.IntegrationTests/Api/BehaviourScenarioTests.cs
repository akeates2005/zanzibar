using System.Diagnostics;
using System.Net;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// Phase 4 exit criterion, part three: every behaviour scenario in the PRD, through HTTP. Each test ends with the
/// no-drift fitness check from <see cref="ApiTestBase"/>.
/// </summary>
public sealed class BehaviourScenarioTests : ApiTestBase
{
    private async Task<HttpStatusCode> StatusAsync(string user, string url)
    {
        using var response = await (await AsAsync(user)).GetAsync(url);
        return response.StatusCode;
    }

    private async Task AssertCannotSeeAsync(string user, params string[] urls)
    {
        foreach (var url in urls)
            Assert.True(HttpStatusCode.NotFound == await StatusAsync(user, url), $"{user} should get 404 for {url}");
    }

    // ---- 1 -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Alice_moving_uk_rnd_to_acme_ie_takes_all_access_from_Bob_and_Heidi_on_their_next_request()
    {
        foreach (var user in new[] { "bob", "heidi" })
        {
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(user, "/api/wallets/uk-rnd"));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(user, "/api/subscriptions/github"));
        }

        var alice = await AsAsync("alice");
        var moved = await (await alice.PostJsonAsync("/api/wallets/uk-rnd/move", new { targetDomainId = "acme-ie" })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("acme-ie", moved.GetProperty("domainId").GetString());

        await AssertCannotSeeAsync("bob", "/api/wallets/uk-rnd", "/api/subscriptions/github");
        await AssertCannotSeeAsync("heidi", "/api/wallets/uk-rnd", "/api/subscriptions/github");
        Assert.Equal(HttpStatusCode.OK, await StatusAsync("alice", "/api/subscriptions/github"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync("bob", "/api/wallets/uk-ops")); // the rest of acme-uk is untouched
    }

    [Fact]
    public async Task Moving_a_wallet_to_another_customers_domain_is_refused_with_422_and_changes_nothing()
    {
        var priya = await AsAsync("priya"); // can create wallets in Globex's domain too, so only the same-customer rule stops her

        await (await priya.PostJsonAsync("/api/wallets/uk-rnd/move", new { targetDomainId = "globex-main" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "cross_customer_move");

        var wallet = await (await priya.GetAsync("/api/wallets/uk-rnd")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("acme-uk", wallet.GetProperty("domainId").GetString());
    }

    [Fact]
    public async Task A_move_needs_create_wallet_on_the_target_not_only_move_on_the_wallet()
    {
        // Bob administers acme-uk (so he holds `move` on uk-rnd) but not acme-ie, the target.
        var bob = await AsAsync("bob");

        await (await bob.PostJsonAsync("/api/wallets/uk-rnd/move", new { targetDomainId = "acme-ie" }))
            .ExpectProblemAsync(HttpStatusCode.NotFound); // he cannot even see acme-ie, so it must look non-existent

        var wallet = await (await (await AsAsync("alice")).GetAsync("/api/wallets/uk-rnd")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("acme-uk", wallet.GetProperty("domainId").GetString());
    }

    // ---- 2 -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Alice_removing_uk_contractors_from_uk_it_costs_Heidi_everything_but_Bob_keeps_his()
    {
        Assert.Equal(HttpStatusCode.OK, await StatusAsync("heidi", "/api/domains/acme-uk"));

        var alice = await AsAsync("alice");
        await (await alice.DeleteAsync("/api/groups/uk-it/members?type=group&memberId=uk-contractors")).ExpectAsync(HttpStatusCode.OK);

        await AssertCannotSeeAsync("heidi", "/api/domains/acme-uk", "/api/wallets/uk-ops", "/api/subscriptions/m365-e3");
        var bob = await (await (await AsAsync("bob")).GetAsync("/api/domains/acme-uk")).ExpectAsync(HttpStatusCode.OK);
        Assert.True(bob.Permission("admin"));
    }

    // ---- 3 -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Carol_creating_a_subscription_can_view_it_and_change_its_quantity_but_not_cancel_it_and_the_audit_shows_why()
    {
        var carol = await AsAsync("carol");

        var created = await (await carol.PostJsonAsync("/api/wallets/uk-ops/subscriptions", new { productName = "Visio", quantity = 4 }))
            .ExpectAsync(HttpStatusCode.Created);
        var id = created.GetProperty("id").GetString()!;
        Assert.True(created.Permission("view"));
        Assert.True(created.Permission("change_quantity"));
        Assert.False(created.Permission("cancel"));

        var changed = await (await carol.PostJsonAsync($"/api/subscriptions/{id}/quantity", new { quantity = 9 })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(9, changed.GetProperty("quantity").GetInt32());
        await (await carol.PostJsonAsync($"/api/subscriptions/{id}/cancel")).ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "cancel");
        await (await carol.GetAsync("/api/subscriptions/azure-plan")).ExpectProblemAsync(HttpStatusCode.NotFound); // sibling stays hidden

        var audit = await (await (await AsAsync("alice")).GetAsync($"/api/access/audit?type=subscription&id={id}")).ExpectAsync(HttpStatusCode.OK);
        var entry = Assert.Single(audit.EnumerateArray());
        Assert.Equal("Grant", entry.GetProperty("action").GetString());
        Assert.Equal("operator", entry.GetProperty("relation").GetString());
        Assert.Equal("carol", entry.GetProperty("subjectId").GetString());
        Assert.Equal("system", entry.GetProperty("actor").GetString());
        Assert.Equal("Applied", entry.GetProperty("status").GetString());
    }

    // ---- 4, 5 ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Carol_cannot_top_up_uk_ops_and_is_told_which_permission_she_lacks()
    {
        var carol = await AsAsync("carol");
        var problem = await (await carol.PostJsonAsync("/api/wallets/uk-ops/top-up", new { amount = 10 }))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, permission: "top_up");
        Assert.Contains("top_up", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var wallet = await (await (await AsAsync("alice")).GetAsync("/api/wallets/uk-ops")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(5000m, wallet.GetProperty("balance").GetDecimal()); // and nothing changed
    }

    [Fact]
    public async Task Dave_asking_for_wallet_uk_ops_gets_404_not_403()
    {
        var dave = await AsAsync("dave");
        var problem = await (await dave.GetAsync("/api/wallets/uk-ops")).ExpectProblemAsync(HttpStatusCode.NotFound);
        Assert.False(problem.TryGetProperty("permission", out _), "a 404 must not hint that a permission is involved");
    }

    // ---- 6, 7, 8 -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Grace_granting_an_Acme_group_on_a_Globex_wallet_gets_422_and_nothing_is_written()
    {
        var grace = await AsAsync("grace");

        await (await grace.PutJsonAsync("/api/access/wallet/globex-ops/grants", new { relation = "viewer", subject = new { type = "group", id = "uk-it" } }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "group_owner_mismatch");

        var grants = await (await grace.GetAsync("/api/access/wallet/globex-ops/grants")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(0, grants.GetProperty("direct").GetArrayLength());
        var audit = await (await grace.GetAsync("/api/access/audit?type=wallet&id=globex-ops")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(0, audit.GetArrayLength());
    }

    [Fact]
    public async Task Alice_nesting_uk_it_inside_uk_contractors_is_refused_as_a_cycle()
    {
        var alice = await AsAsync("alice");

        await (await alice.PutJsonAsync("/api/groups/uk-contractors/members", new { type = "group", id = "uk-it" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code: "group_cycle");

        var detail = await (await alice.GetAsync("/api/groups/uk-contractors")).ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(detail.GetProperty("members").EnumerateArray(), m => m.GetProperty("subject").GetProperty("id").GetString() == "uk-it");
        Assert.Equal(HttpStatusCode.OK, await StatusAsync("heidi", "/api/domains/acme-uk")); // nothing broke
    }

    [Fact]
    public async Task Alice_deleting_acme_uk_while_it_has_wallets_gets_409_and_it_survives()
    {
        var alice = await AsAsync("alice");

        await (await alice.DeleteAsync("/api/domains/acme-uk")).ExpectProblemAsync(HttpStatusCode.Conflict);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync("alice", "/api/domains/acme-uk"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync("bob", "/api/wallets/uk-ops"));
    }

    // ---- 9 -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Alice_revoking_Daves_operator_grant_makes_his_next_request_for_m365_e3_a_404()
    {
        Assert.Equal(HttpStatusCode.OK, await StatusAsync("dave", "/api/subscriptions/m365-e3"));

        var alice = await AsAsync("alice");
        await (await alice.DeleteAsync("/api/access/subscription/m365-e3/grants?relation=operator&subjectType=user&subjectId=dave"))
            .ExpectAsync(HttpStatusCode.OK);

        await AssertCannotSeeAsync("dave", "/api/subscriptions/m365-e3");
    }

    // ---- 10: the engine pauses -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_create_while_the_engine_is_frozen_is_refused_with_503_never_allowed_and_nothing_is_created()
    {
        var alice = await AsAsync("alice");
        Host.Engine.Pause();
        var clock = Stopwatch.StartNew();

        var problem = await (await alice.PostJsonAsync("/api/domains/acme-uk/wallets", new { name = "Created while frozen" }))
            .ExpectProblemAsync(HttpStatusCode.ServiceUnavailable);
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the request hung for {clock.Elapsed}; the deadline must bound it");
        Assert.DoesNotContain("SpiceDB", problem.ToString(), StringComparison.OrdinalIgnoreCase); // no internals leaked
        // Reads fail closed too: no cached or default answer is ever served.
        await (await alice.GetAsync("/api/wallets/uk-ops")).ExpectProblemAsync(HttpStatusCode.ServiceUnavailable);

        Host.Engine.Resume();
        await Host.WaitUntilReadyAsync(TimeSpan.FromSeconds(30));
        var children = await (await alice.GetAsync("/api/domain/acme-uk/children")).ExpectAsync(HttpStatusCode.OK);
        Assert.DoesNotContain(children.EnumerateArray(), c => c.GetProperty("name").GetString() == "Created while frozen");
    }

    [Fact]
    public async Task A_create_whose_authorisation_cannot_land_returns_202_pending_stays_invisible_then_appears_when_the_engine_recovers()
    {
        var alice = await AsAsync("alice");
        Host.Faults.Down = true; // the permission check succeeds; the tuple write that follows fails: the only way to reach 202

        var accepted = await (await alice.PostJsonAsync("/api/domains/acme-uk/wallets", new { name = "Pending wallet" })).ExpectAsync(HttpStatusCode.Accepted);
        Assert.True(accepted.GetProperty("authorisationPending").GetBoolean());
        var id = accepted.GetProperty("id").GetString()!;

        await (await alice.GetAsync($"/api/wallets/{id}")).ExpectProblemAsync(HttpStatusCode.NotFound); // saved, but invisible until authorised

        Host.Faults.Down = false;
        await EventuallyAsync(async () => (await alice.GetAsync($"/api/wallets/{id}")).StatusCode == HttpStatusCode.OK, "the background worker never made the wallet visible");
        var wallet = await (await alice.GetAsync($"/api/wallets/{id}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Pending wallet", wallet.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_delete_whose_authorisation_cannot_land_returns_202_and_the_object_is_gone_once_it_does()
    {
        var priya = await AsAsync("priya");
        var created = await (await priya.PostJsonAsync("/api/customers", new { name = "Short lived" })).ExpectAsync(HttpStatusCode.Created);
        var id = created.GetProperty("id").GetString()!;

        Host.Faults.Down = true;
        var accepted = await (await priya.DeleteAsync($"/api/customers/{id}")).ExpectAsync(HttpStatusCode.Accepted);
        Assert.True(accepted.GetProperty("authorisationPending").GetBoolean());
        Host.Faults.Down = false;

        await EventuallyAsync(
            async () => (await Host.Services.GetService<Poc.Application.Projection.IOutboxInspector>()!.GetStatusAsync(Ct)).Pending == 0,
            "the delete's tuples were never removed");
        await (await priya.GetAsync($"/api/customers/{id}")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string failure, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }

        Assert.Fail(failure);
    }
}

internal static class ServiceExtensions
{
    public static T? GetService<T>(this IServiceProvider services) => (T?)services.GetService(typeof(T));
}
