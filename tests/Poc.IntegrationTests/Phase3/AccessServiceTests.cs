using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;
using Poc.Domain.Access;
using Poc.Domain.Directory;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Phase3;

/// <summary>Grants, revokes, group membership and the three rules the schema cannot express, against a real engine.</summary>
public sealed class AccessServiceTests : PocTestBase
{
    private static readonly Actor Alice = Actor.ForUser("alice");
    private static readonly Actor Priya = Actor.ForUser("priya");
    private static readonly string[] CycleGroups = ["g-a", "g-b", "g-c"];

    private async Task<T> AsAsync<T>(Func<AccessService, Task<T>> action)
    {
        await using var scope = Host.Scope();
        return await action(scope.ServiceProvider.GetRequiredService<AccessService>());
    }

    private static GrantTarget Grant(string type, string id, string relation, SubjectRef subject) => new(Res(type, id), relation, subject);

    /// <summary>Asserts a command is refused and leaves neither a tuple nor an audit row behind.</summary>
    private async Task<T> AssertNothingWrittenAsync<T>(Func<Task> command)
        where T : Exception
    {
        var tuplesBefore = (await AllTuplesAsync()).Select(t => t.ToString()).Order().ToList();
        var auditBefore = (await AuditAsync()).Count;

        var ex = await Assert.ThrowsAsync<T>(command);

        Assert.Equal(tuplesBefore, (await AllTuplesAsync()).Select(t => t.ToString()).Order());
        Assert.Equal(auditBefore, (await AuditAsync()).Count);
        return ex;
    }

    // ---- Grants and revokes take effect at once ------------------------------------------------------------------

    [Fact]
    public async Task A_grant_takes_effect_on_the_next_check_and_is_audited_with_the_actor_and_zed_token()
    {
        Assert.False(await CanAsync("erin", DomainSchema.Permissions.View, Res("domain", "acme-uk")));

        var result = await AsAsync(a => a.GrantAsync(Alice, Grant("domain", "acme-uk", DomainSchema.Relations.Viewer, SubjectRef.User("erin")), Ct));

        Assert.True(await CanAsync("erin", DomainSchema.Permissions.View, Res("domain", "acme-uk")));
        Assert.False(await CanAsync("erin", WalletSchema.Permissions.View, Res("wallet", "uk-ops"))); // viewer does not flow down
        var entry = (await AuditAsync()).Last();
        Assert.Equal(result.AuditId, entry.Id);
        Assert.Equal((GrantAction.Grant, GrantStatus.Applied, "alice", "domain", "acme-uk", "viewer", "user", "erin"),
            (entry.Action, entry.Status, entry.ActorUserId, entry.ResourceType, entry.ResourceId, entry.Relation, entry.SubjectType, entry.SubjectId));
        Assert.Equal(result.ZedToken, entry.ZedToken);
    }

    [Fact]
    public async Task A_grant_records_its_zed_token_on_the_row_it_changed()
    {
        var result = await AsAsync(a => a.GrantAsync(Alice, Grant("customer", "acme", CustomerSchema.Relations.Viewer, SubjectRef.User("frank")), Ct));
        Assert.Equal(result.ZedToken, await QueryAsync(db => db.Customers.Where(c => c.Id == "acme").Select(c => c.AuthzToken).SingleAsync(Ct)));
    }

    [Fact]
    public async Task Revoking_an_operator_grant_hides_the_subscription_on_the_next_request()
    {
        // PRD scenario: Alice revokes Dave's operator grant; Dave's next request for m365-e3 is a 404 (no view).
        Assert.True(await CanAsync("dave", SubscriptionSchema.Permissions.View, Res("subscription", "m365-e3")));

        await AsAsync(a => a.RevokeAsync(Alice, Grant("subscription", "m365-e3", SubscriptionSchema.Relations.Operator, SubjectRef.User("dave")), Ct));

        Assert.False(await CanAsync("dave", SubscriptionSchema.Permissions.View, Res("subscription", "m365-e3")));
        Assert.False(await CanAsync("dave", SubscriptionSchema.Permissions.ChangeQuantity, Res("subscription", "m365-e3")));
        var entry = (await AuditAsync()).Last();
        Assert.Equal((GrantAction.Revoke, GrantStatus.Applied), (entry.Action, entry.Status));
    }

    [Fact]
    public async Task Revoking_removes_only_the_named_grant()
    {
        await AsAsync(a => a.GrantAsync(Alice, Grant("wallet", "uk-ops", WalletSchema.Relations.Viewer, SubjectRef.User("dave")), Ct));
        await AsAsync(a => a.RevokeAsync(Alice, Grant("wallet", "uk-ops", WalletSchema.Relations.Viewer, SubjectRef.User("dave")), Ct));

        Assert.False(await CanAsync("dave", WalletSchema.Permissions.View, Res("wallet", "uk-ops")));
        Assert.True(await CanAsync("dave", SubscriptionSchema.Permissions.ChangeQuantity, Res("subscription", "m365-e3"))); // the other grant survives
    }

    [Fact]
    public async Task Revoking_a_group_grant_matches_the_group_subject_exactly_and_leaves_user_grants_alone()
    {
        await AsAsync(a => a.GrantAsync(Alice, Grant("domain", "acme-ie", DomainSchema.Relations.Viewer, SubjectRef.User("bob")), Ct));
        await AsAsync(a => a.GrantAsync(Alice, Grant("domain", "acme-ie", DomainSchema.Relations.Viewer, SubjectRef.GroupMembers("uk-it")), Ct));
        Assert.True(await CanAsync("heidi", DomainSchema.Permissions.View, Res("domain", "acme-ie")));

        await AsAsync(a => a.RevokeAsync(Alice, Grant("domain", "acme-ie", DomainSchema.Relations.Viewer, SubjectRef.GroupMembers("uk-it")), Ct));

        Assert.False(await CanAsync("heidi", DomainSchema.Permissions.View, Res("domain", "acme-ie")));
        Assert.True(await CanAsync("bob", DomainSchema.Permissions.View, Res("domain", "acme-ie"))); // Bob's direct grant remains
    }

    [Fact]
    public async Task Repeating_a_grant_or_a_revoke_is_safe_and_each_attempt_is_audited()
    {
        var grant = Grant("customer", "acme", CustomerSchema.Relations.Viewer, SubjectRef.User("frank"));
        await AsAsync(a => a.GrantAsync(Alice, grant, Ct));
        await AsAsync(a => a.GrantAsync(Alice, grant, Ct));
        Assert.Single((await AllTuplesAsync()), t => t.Subject.Id == "frank");

        await AsAsync(a => a.RevokeAsync(Alice, grant, Ct));
        await AsAsync(a => a.RevokeAsync(Alice, grant, Ct));
        Assert.DoesNotContain((await AllTuplesAsync()), t => t.Subject.Id == "frank");
        Assert.Equal(4, (await AuditAsync()).Count(a => a.SubjectId == "frank" && a.Status == GrantStatus.Applied));
    }

    // ---- Permission checks ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Someone_without_manage_access_is_refused_naming_the_permission_and_nothing_is_written()
    {
        var ex = await AssertNothingWrittenAsync<PermissionDeniedException>(() => AsAsync(a => a.GrantAsync(
            Actor.ForUser("frank"), Grant("customer", "acme", CustomerSchema.Relations.Viewer, SubjectRef.User("frank")), Ct)));

        Assert.Equal("manage_access", ex.Permission);
        Assert.Equal(Res("customer", "acme"), ex.Resource);
    }

    [Fact]
    public async Task A_viewer_cannot_grant_and_a_spender_cannot_grant_on_their_wallet()
    {
        await AssertNothingWrittenAsync<PermissionDeniedException>(() => AsAsync(a => a.GrantAsync(
            Actor.ForUser("erin"), Grant("customer", "acme", CustomerSchema.Relations.Viewer, SubjectRef.User("frank")), Ct)));
        await AssertNothingWrittenAsync<PermissionDeniedException>(() => AsAsync(a => a.GrantAsync(
            Actor.ForUser("carol"), Grant("wallet", "uk-ops", WalletSchema.Relations.Viewer, SubjectRef.User("frank")), Ct)));
    }

    [Fact]
    public async Task A_domain_admin_may_grant_below_their_domain_but_not_above_it()
    {
        var bob = Actor.ForUser("bob");
        await AsAsync(a => a.GrantAsync(bob, Grant("wallet", "uk-ops", WalletSchema.Relations.Viewer, SubjectRef.User("frank")), Ct)); // inherited admin
        Assert.True(await CanAsync("frank", WalletSchema.Permissions.View, Res("wallet", "uk-ops")));

        await AssertNothingWrittenAsync<PermissionDeniedException>(() => AsAsync(a => a.GrantAsync(
            bob, Grant("customer", "acme", CustomerSchema.Relations.Viewer, SubjectRef.User("frank")), Ct)));
        await AssertNothingWrittenAsync<PermissionDeniedException>(() => AsAsync(a => a.GrantAsync(
            bob, Grant("wallet", "ie-ops", WalletSchema.Relations.Viewer, SubjectRef.User("frank")), Ct))); // a sibling domain
    }

    [Fact]
    public async Task Support_may_view_everything_but_cannot_manage_access()
    {
        await AssertNothingWrittenAsync<PermissionDeniedException>(() => AsAsync(a => a.GrantAsync(
            Actor.ForUser("sam"), Grant("customer", "acme", CustomerSchema.Relations.Viewer, SubjectRef.User("frank")), Ct)));
    }

    [Fact]
    public async Task Only_a_platform_admin_can_grant_on_the_platform()
    {
        await AssertNothingWrittenAsync<PermissionDeniedException>(() => AsAsync(a => a.GrantAsync(
            Alice, Grant("platform", "main", PlatformSchema.Relations.Support, SubjectRef.User("frank")), Ct)));
        await AsAsync(a => a.GrantAsync(Priya, Grant("platform", "main", PlatformSchema.Relations.Support, SubjectRef.User("frank")), Ct));
        Assert.True(await CanAsync("frank", CustomerSchema.Permissions.View, Res("customer", "globex")));
    }

    // ---- Rule 1: a group may be granted only under its own customer ---------------------------------------------

    [Fact]
    public async Task Rule1_Grace_cannot_grant_an_Acme_group_on_a_Globex_wallet()
    {
        // PRD scenario: Grace tries to grant an Acme group on a Globex wallet; 422, nothing written.
        var ex = await AssertNothingWrittenAsync<RuleViolationException>(() => AsAsync(a => a.GrantAsync(
            Actor.ForUser("grace"), Grant("wallet", "globex-ops", WalletSchema.Relations.Viewer, SubjectRef.GroupMembers("uk-it")), Ct)));

        Assert.Equal(GrantRules.Codes.GroupOwnerMismatch, ex.Code);
    }

    [Fact]
    public async Task Rule1_an_Acme_group_can_be_granted_at_every_level_under_Acme()
    {
        var members = SubjectRef.GroupMembers("finance-ap");
        await AsAsync(a => a.GrantAsync(Alice, Grant("customer", "acme", CustomerSchema.Relations.Viewer, members), Ct));
        await AsAsync(a => a.GrantAsync(Alice, Grant("domain", "acme-ie", DomainSchema.Relations.Viewer, members), Ct));
        await AsAsync(a => a.GrantAsync(Alice, Grant("wallet", "ie-ops", WalletSchema.Relations.Spender, members), Ct));
        await AsAsync(a => a.GrantAsync(Alice, Grant("subscription", "m365-bp", SubscriptionSchema.Relations.Operator, members), Ct));
        Assert.True(await CanAsync("carol", SubscriptionSchema.Permissions.ChangeQuantity, Res("subscription", "m365-bp")));
    }

    [Fact]
    public async Task Rule1_a_customer_group_cannot_be_granted_on_the_platform()
    {
        var ex = await AssertNothingWrittenAsync<RuleViolationException>(() => AsAsync(a => a.GrantAsync(
            Priya, Grant("platform", "main", PlatformSchema.Relations.Support, SubjectRef.GroupMembers("uk-it")), Ct)));
        Assert.Equal(GrantRules.Codes.GroupOwnerMismatch, ex.Code);
    }

    [Fact]
    public async Task Rule1_a_platform_group_can_be_granted_anywhere_but_only_by_a_platform_administrator()
    {
        var support = SubjectRef.GroupMembers("support-team");
        await AssertNothingWrittenAsync<PermissionDeniedException>(() => AsAsync(a => a.GrantAsync(
            Alice, Grant("customer", "acme", CustomerSchema.Relations.Viewer, support), Ct))); // Alice administers Acme but is not a platform admin
        var ex = await Assert.ThrowsAsync<PermissionDeniedException>(() => AsAsync(a => a.RevokeAsync(
            Alice, Grant("customer", "acme", CustomerSchema.Relations.Viewer, support), Ct)));
        Assert.Equal("admin", ex.Permission);
        Assert.Equal(PlatformSchema.Main, ex.Resource);

        await AsAsync(a => a.GrantAsync(Priya, Grant("customer", "acme", CustomerSchema.Relations.Viewer, support), Ct));
        await AsAsync(a => a.GrantAsync(Priya, Grant("customer", "globex", CustomerSchema.Relations.Viewer, support), Ct));
        Assert.True(await CanAsync("sam", CustomerSchema.Permissions.View, Res("customer", "acme")));
    }

    // ---- Rules 2 and 3: nesting ----------------------------------------------------------------------------------

    [Fact]
    public async Task Rule3_Alice_cannot_nest_uk_it_inside_uk_contractors_because_that_closes_a_cycle()
    {
        // PRD scenario: uk-contractors is already inside uk-it, so putting uk-it inside it would loop.
        var ex = await AssertNothingWrittenAsync<RuleViolationException>(() => AsAsync(a => a.AddGroupMemberAsync(
            Alice, "uk-contractors", SubjectRef.GroupMembers("uk-it"), Ct)));

        Assert.Equal(GrantRules.Codes.GroupCycle, ex.Code);
        Assert.True(await CanAsync("heidi", DomainSchema.Permissions.Admin, Res("domain", "acme-uk"))); // and nothing broke
    }

    [Fact]
    public async Task Rule3_indirect_cycles_through_several_levels_are_caught()
    {
        await AsAsync(async a =>
        {
            foreach (var id in CycleGroups)
                await a.CreateGroupAsync(Alice, GroupOwnerType.Customer, "acme", id, Ct, id);
            await a.AddGroupMemberAsync(Alice, "g-b", SubjectRef.GroupMembers("g-a"), Ct); // a inside b
            await a.AddGroupMemberAsync(Alice, "g-c", SubjectRef.GroupMembers("g-b"), Ct); // b inside c
            return 0;
        });

        var ex = await AssertNothingWrittenAsync<RuleViolationException>(() => AsAsync(a => a.AddGroupMemberAsync(
            Alice, "g-a", SubjectRef.GroupMembers("g-c"), Ct))); // c inside a would close a -> b -> c -> a

        Assert.Equal(GrantRules.Codes.GroupCycle, ex.Code);
    }

    [Fact]
    public async Task Rule3_nesting_a_group_in_itself_is_refused()
    {
        var ex = await AssertNothingWrittenAsync<RuleViolationException>(() => AsAsync(a => a.AddGroupMemberAsync(
            Alice, "uk-it", SubjectRef.GroupMembers("uk-it"), Ct)));
        Assert.Equal(GrantRules.Codes.GroupSelfNesting, ex.Code);
    }

    [Fact]
    public async Task Nesting_without_a_cycle_works_and_grants_flow_through()
    {
        await AsAsync(a => a.AddGroupMemberAsync(Alice, "finance-ap", SubjectRef.GroupMembers("uk-contractors"), Ct));
        Assert.True(await CanAsync("heidi", WalletSchema.Permissions.Spend, Res("wallet", "uk-ops"))); // heidi: uk-contractors -> finance-ap -> wallet
    }

    [Fact]
    public async Task Rule2_groups_with_different_owners_cannot_be_nested()
    {
        await AsAsync(a => a.CreateGroupAsync(Actor.ForUser("grace"), GroupOwnerType.Customer, "globex", "Globex IT", Ct, "globex-it"));

        var ex = await AssertNothingWrittenAsync<RuleViolationException>(() => AsAsync(a => a.AddGroupMemberAsync(
            Alice, "uk-it", SubjectRef.GroupMembers("globex-it"), Ct)));
        Assert.Equal(GrantRules.Codes.GroupOwnerMismatch, ex.Code);
        await AssertNothingWrittenAsync<RuleViolationException>(() => AsAsync(a => a.AddGroupMemberAsync(
            Priya, "support-team", SubjectRef.GroupMembers("uk-it"), Ct))); // a customer group inside a platform group
    }

    [Fact]
    public async Task Removing_the_contractors_from_uk_it_costs_Heidi_everything_but_Bob_keeps_his()
    {
        // PRD scenario: Alice removes uk-contractors from uk-it.
        await AsAsync(a => a.RemoveGroupMemberAsync(Alice, "uk-it", SubjectRef.GroupMembers("uk-contractors"), Ct));

        foreach (var permission in new[] { "view", "admin", "create_wallet" })
            Assert.False(await CanAsync("heidi", permission, Res("domain", "acme-uk")), $"heidi {permission}");
        Assert.False(await CanAsync("heidi", WalletSchema.Permissions.Spend, Res("wallet", "uk-ops")));
        Assert.True(await CanAsync("bob", DomainSchema.Permissions.Admin, Res("domain", "acme-uk")));
        Assert.True(await CanAsync("bob", WalletSchema.Permissions.Spend, Res("wallet", "uk-ops")));
    }

    // ---- Membership permissions ----------------------------------------------------------------------------------

    [Fact]
    public async Task Managing_a_group_needs_manage_on_it_which_is_admin_of_its_owner()
    {
        await AssertNothingWrittenAsync<PermissionDeniedException>(() => AsAsync(a => a.AddGroupMemberAsync(
            Actor.ForUser("bob"), "uk-it", SubjectRef.User("frank"), Ct))); // Bob administers a domain, not the customer
        var ex = await Assert.ThrowsAsync<PermissionDeniedException>(() => AsAsync(a => a.AddGroupMemberAsync(
            Actor.ForUser("alice"), "support-team", SubjectRef.User("frank"), Ct))); // a platform group
        Assert.Equal("manage", ex.Permission);

        await AsAsync(a => a.AddGroupMemberAsync(Alice, "uk-it", SubjectRef.User("frank"), Ct));
        await AsAsync(a => a.AddGroupMemberAsync(Priya, "support-team", SubjectRef.User("frank"), Ct));
        Assert.True(await CanAsync("frank", DomainSchema.Permissions.Admin, Res("domain", "acme-uk")));
        Assert.True(await CanAsync("frank", CustomerSchema.Permissions.View, Res("customer", "globex")));
    }

    // ---- Malformed requests ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("customer", "acme", "parent", "user", "frank", null, GrantRules.Codes.UnknownRelation)]            // structural relation
    [InlineData("customer", "acme", "operator", "user", "frank", null, GrantRules.Codes.UnknownRelation)]          // wrong object type
    [InlineData("wallet", "uk-ops", "view", "user", "frank", null, GrantRules.Codes.UnknownRelation)]              // a permission
    [InlineData("customer", "acme", "viewer", "user", "no-such-user", null, GrantRules.Codes.UnknownSubject)]      // not in the directory
    [InlineData("customer", "acme", "viewer", "group", "no-such-group", "member", GrantRules.Codes.UnknownSubject)]
    [InlineData("customer", "acme", "viewer", "customer", "acme", null, GrantRules.Codes.InvalidSubject)]         // wrong subject type
    [InlineData("customer", "acme", "viewer", "group", "uk-it", null, GrantRules.Codes.InvalidSubject)]           // group without #member
    [InlineData("customer", "acme", "viewer", "user", "frank", "member", GrantRules.Codes.InvalidSubject)]        // user with a relation
    public async Task Malformed_grants_are_refused_with_a_stable_code_before_anything_is_written(
        string type, string id, string relation, string subjectType, string subjectId, string? subjectRelation, string code)
    {
        var ex = await AssertNothingWrittenAsync<RuleViolationException>(() => AsAsync(a => a.GrantAsync(
            Alice, Grant(type, id, relation, new SubjectRef(subjectType, subjectId, subjectRelation)), Ct)));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task Granting_on_an_object_that_does_not_exist_is_not_found_for_the_system_actor()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => AsAsync(a => a.GrantAsync(
            Actor.System, Grant("wallet", "no-such-wallet", WalletSchema.Relations.Viewer, SubjectRef.User("frank")), Ct)));
    }

    [Fact]
    public async Task Granting_on_an_unknown_object_as_a_user_is_forbidden_not_not_found_so_existence_is_not_leaked()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => AsAsync(a => a.GrantAsync(
            Alice, Grant("wallet", "no-such-wallet", WalletSchema.Relations.Viewer, SubjectRef.User("frank")), Ct)));
    }

    // ---- Engine failure ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_tuple_write_failure_marks_the_audit_row_failed_and_surfaces_as_engine_unavailable()
    {
        Host.Faults.Down = true;

        await Assert.ThrowsAsync<AuthorizationUnavailableException>(() => AsAsync(a => a.GrantAsync(
            Alice, Grant("domain", "acme-ie", DomainSchema.Relations.Viewer, SubjectRef.User("erin")), Ct)));

        var entry = (await AuditAsync()).Last();
        Assert.Equal((GrantStatus.Failed, "erin", null), (entry.Status, entry.SubjectId, entry.ZedToken));
        Host.Faults.Down = false;
        Assert.False(await CanAsync("erin", DomainSchema.Permissions.View, Res("domain", "acme-ie"))); // no partial grant
    }

    [Fact]
    public async Task A_failed_revoke_is_audited_and_leaves_the_access_in_place()
    {
        Host.Faults.Down = true;
        await Assert.ThrowsAsync<AuthorizationUnavailableException>(() => AsAsync(a => a.RevokeAsync(
            Alice, Grant("subscription", "m365-e3", SubscriptionSchema.Relations.Operator, SubjectRef.User("dave")), Ct)));
        Host.Faults.Down = false;

        Assert.Equal((GrantAction.Revoke, GrantStatus.Failed), ((await AuditAsync()).Last().Action, (await AuditAsync()).Last().Status));
        Assert.True(await CanAsync("dave", SubscriptionSchema.Permissions.ChangeQuantity, Res("subscription", "m365-e3")));
    }

    [Fact]
    public async Task An_engine_that_cannot_answer_the_permission_check_never_results_in_a_grant()
    {
        // The check itself uses the real authoriser, so break the connection to prove fail-closed on the check path.
        await using var broken = await PocHost.CreateAsync(runStartup: false, o => o.SpiceDb.Endpoint = $"http://127.0.0.1:{Fixtures.NativeSpiceDbHost.FreePort()}");
        await broken.EnsureDatabaseAsync();
        await using var scope = broken.Scope();

        await Assert.ThrowsAsync<AuthorizationUnavailableException>(() => scope.ServiceProvider.GetRequiredService<AccessService>().GrantAsync(
            Alice, Grant("customer", "acme", CustomerSchema.Relations.Viewer, SubjectRef.User("frank")), Ct));
        Assert.Equal(0, await broken.Get<IDbContextFactory<Poc.Infrastructure.Persistence.PocDbContext>>().CreateDbContext().GrantAudit.CountAsync(Ct));
    }

    // ---- Group lifecycle -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_group_writes_its_owner_tuple_and_makes_it_visible_to_the_owners_admins()
    {
        var result = await AsAsync(a => a.CreateGroupAsync(Alice, GroupOwnerType.Customer, "acme", "Auditors", Ct));

        Assert.False(result.AuthorisationPending);
        Assert.True(await HasTupleAsync($"group:{result.GroupId}#owner@customer:acme"));
        Assert.True(await CanAsync("alice", GroupSchema.Permissions.Manage, Res("group", result.GroupId)));
        Assert.True(await CanAsync("priya", GroupSchema.Permissions.Manage, Res("group", result.GroupId))); // platform admin reaches it too
        Assert.False(await CanAsync("grace", GroupSchema.Permissions.View, Res("group", result.GroupId))); // another customer's admin does not
    }

    [Fact]
    public async Task Creating_a_group_while_the_engine_is_down_reports_authorisation_pending_and_recovers()
    {
        Host.Faults.Down = true;
        var result = await AsAsync(a => a.CreateGroupAsync(Alice, GroupOwnerType.Customer, "acme", "Late", Ct, "late-group"));
        Assert.True(result.AuthorisationPending);
        Host.Faults.Down = false;

        await FlushAsync();

        Assert.True(await HasTupleAsync("group:late-group#owner@customer:acme"));
    }

    [Fact]
    public async Task Creating_a_group_for_an_unknown_customer_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => AsAsync(a => a.CreateGroupAsync(Alice, GroupOwnerType.Customer, "no-such-customer", "X", Ct)));
    }

    [Fact]
    public async Task A_group_id_that_could_corrupt_a_tuple_is_refused()
    {
        await Assert.ThrowsAsync<Poc.Domain.Common.DomainRuleException>(() => AsAsync(a => a.CreateGroupAsync(
            Alice, GroupOwnerType.Customer, "acme", "Evil", Ct, "evil#member")));
    }

    [Fact]
    public async Task Deleting_an_unknown_group_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => AsAsync(a => a.DeleteGroupAsync(Alice, "no-such-group", Ct)));
    }
}
