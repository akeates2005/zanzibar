using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.Application.Common;
using Poc.Application.Persistence;
using Poc.Domain.Commerce;
using Poc.Domain.Directory;
using Poc.Infrastructure;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Resilience;

/// <summary>
/// The projector against failures shaped like real ones: a response lost after the write landed, a multi-step plan that
/// breaks part way, an engine that flaps. Whatever happens, the stores must converge and reconciliation must agree.
/// </summary>
public sealed class OutboxFaultTests : PocTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private Task AddWalletAsync(string id, string domainId = "acme-uk", string by = "alice") =>
        CommitAsync(sp => sp.GetRequiredService<IRepository<Wallet>>().Add(Wallet.Create(domainId, id, by, Now, id)));

    private async Task<int> TupleCountAsync(string type, string id) =>
        (await Host.Get<IRelationshipStore>().ReadAsync(new RelationshipFilter(type, id), Ct)).Count;

    private async Task AssertConvergedAsync()
    {
        var report = await Host.Get<ReconciliationRunner>().RunAsync(repair: false, Ct);
        Assert.True(report.InSync, $"missing [{string.Join(", ", report.Missing)}] unexpected [{string.Join(", ", report.Unexpected)}]");
        Assert.Equal(0, (await Host.Get<Poc.Application.Projection.IOutboxInspector>().GetStatusAsync(Ct)).Pending);
    }

    [Fact]
    public async Task A_response_lost_after_the_write_landed_is_replayed_harmlessly_and_burns_no_attempt()
    {
        await AddWalletAsync("lost-reply");
        Host.Faults.LoseResponseAfterWrite = true;

        var first = await FlushAsync();

        Assert.True(first.EngineUnavailable);
        Assert.Equal(1, await TupleCountAsync("wallet", "lost-reply")); // the engine did get it
        var message = (await QueryAsync(db => db.OutboxMessages.AsNoTracking().OrderBy(m => m.Sequence).ToListAsync(Ct))).Last();
        Assert.Equal(0, message.Attempts);
        Assert.Null(message.ProcessedAt);

        Host.Faults.LoseResponseAfterWrite = false;
        var second = await FlushAsync();

        Assert.True(second.FullyApplied);
        Assert.Equal(1, await TupleCountAsync("wallet", "lost-reply")); // replay did not duplicate it
        await AssertConvergedAsync();
    }

    [Fact]
    public async Task A_lost_response_on_a_move_replays_to_the_same_final_parent()
    {
        await AddWalletAsync("lost-move");
        await FlushAsync();
        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<IRepository<Wallet>>();
            (await repo.FindAsync("lost-move", Ct))!.MoveTo("acme-ie");
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        });
        Host.Faults.LoseResponseAfterWrite = true;
        await FlushAsync();
        Host.Faults.LoseResponseAfterWrite = false;

        await FlushAsync();

        Assert.True(await HasTupleAsync("wallet:lost-move#parent@domain:acme-ie"));
        Assert.False(await HasTupleAsync("wallet:lost-move#parent@domain:acme-uk"));
        await AssertConvergedAsync();
    }

    [Fact]
    public async Task A_group_deletion_that_breaks_part_way_is_completed_by_the_retry()
    {
        // GroupDeleted is seven deletes (its own tuples, then every place it is a subject, one per resource type).
        Host.Faults.ResetCounters();
        Host.Faults.FailDeleteCallNumber = 4;
        await InScopeAsync(async sp =>
        {
            var groups = sp.GetRequiredService<IRepository<Group>>();
            var group = (await groups.FindAsync("uk-contractors", Ct))!;
            group.Delete();
            groups.Remove(group);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        });

        var first = await FlushAsync();

        Assert.True(first.EngineUnavailable);
        Assert.Equal(1, first.Remaining);
        // Part way: the group's own tuples and its nesting in uk-it are gone, but a later step has not run yet.
        Assert.Equal(0, await TupleCountAsync("group", "uk-contractors"));
        Assert.False(await HasTupleAsync("group:uk-it#member@group:uk-contractors#member"));

        var second = await FlushAsync();

        Assert.True(second.FullyApplied);
        Assert.DoesNotContain(await AllTuplesAsync(), t => t.Resource.Id == "uk-contractors" || t.Subject.Id == "uk-contractors");
        Assert.False(await CanAsync("heidi", DomainSchemaPermission, Res("domain", "acme-uk")));
        Assert.True(await CanAsync("bob", DomainSchemaPermission, Res("domain", "acme-uk")));
        await AssertConvergedAsync();
    }

    private const string DomainSchemaPermission = "admin";

    [Fact]
    public async Task A_flapping_engine_converges_to_exactly_the_right_state_with_per_object_order_kept()
    {
        Host.Faults.ResetCounters();
        // A mix of creates, moves and deletes, written while half of all engine calls fail.
        for (var i = 0; i < 20; i++) await AddWalletAsync($"flap-{i}");
        foreach (var i in Enumerable.Range(0, 20).Where(n => n % 2 == 0))
        {
            await InScopeAsync(async sp =>
            {
                var repo = sp.GetRequiredService<IRepository<Wallet>>();
                (await repo.FindAsync($"flap-{i}", Ct))!.MoveTo("acme-ie");
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
            });
        }

        foreach (var i in Enumerable.Range(0, 20).Where(n => n % 5 == 0))
        {
            await InScopeAsync(async sp =>
            {
                var repo = sp.GetRequiredService<IRepository<Wallet>>();
                var wallet = (await repo.FindAsync($"flap-{i}", Ct))!;
                wallet.Delete();
                repo.Remove(wallet);
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
            });
        }

        Host.Faults.FailureRate = 0.5;
        var flushes = 0;
        for (; flushes < 400; flushes++)
        {
            if ((await FlushAsync()).FullyApplied) break;
        }

        Assert.True(flushes < 400, "the outbox never drained");
        Host.Faults.FailureRate = 0;
        Assert.True(flushes > 5, "the fault injection did not actually bite");

        for (var i = 0; i < 20; i++)
        {
            var parents = (await Host.Get<IRelationshipStore>().ReadAsync(new RelationshipFilter("wallet", $"flap-{i}", "parent"), Ct)).Select(r => r.Subject.Id).ToList();
            if (i % 5 == 0) Assert.Empty(parents); // deleted
            else Assert.Equal([i % 2 == 0 ? "acme-ie" : "acme-uk"], parents); // exactly one parent, the right one
        }

        await AssertConvergedAsync();
    }

    [Fact]
    public async Task A_default_grant_whose_subscription_was_created_during_an_outage_is_applied_exactly_once_on_recovery()
    {
        await CommitAsync(sp => sp.GetRequiredService<IRepository<Subscription>>().Add(
            Subscription.Create("uk-ops", "Visio", 4, "carol", Now, "outage-sub")));
        Host.Faults.Down = true;
        for (var i = 0; i < 6; i++) Assert.True((await FlushAsync()).EngineUnavailable);

        Host.Faults.Down = false;
        await FlushAsync();
        await FlushAsync(); // and again: a further flush must not repeat the grant

        Assert.True(await CanAsync("carol", "change_quantity", Res("subscription", "outage-sub")));
        var grants = (await AuditAsync()).Where(a => a.ResourceId == "outage-sub").ToList();
        var grant = Assert.Single(grants);
        Assert.Equal((Poc.Domain.Access.GrantAction.Grant, Poc.Domain.Access.GrantStatus.Applied, Actor.SystemUserId), (grant.Action, grant.Status, grant.ActorUserId));
        await AssertConvergedAsync();
    }

    [Fact]
    public async Task A_grant_that_fails_leaves_a_failed_audit_row_and_can_simply_be_issued_again()
    {
        var target = new GrantTarget(Res("domain", "acme-ie"), "viewer", SubjectRef.User("erin"));
        Host.Faults.Down = true;
        await Assert.ThrowsAsync<AuthorizationUnavailableException>(() => InScopeAsync(sp => sp.GetRequiredService<AccessService>().GrantAsync(Actor.ForUser("alice"), target, Ct)));
        Host.Faults.Down = false;
        Assert.False(await CanAsync("erin", "view", Res("domain", "acme-ie")));

        await InScopeAsync(sp => sp.GetRequiredService<AccessService>().GrantAsync(Actor.ForUser("alice"), target, Ct));

        Assert.True(await CanAsync("erin", "view", Res("domain", "acme-ie")));
        Assert.Equal(["Failed", "Applied"], (await AuditAsync()).Where(a => a.ResourceId == "acme-ie" && a.SubjectId == "erin").Select(a => a.Status.ToString()).ToArray());
    }

    [Fact]
    public async Task Reconciliation_finds_drift_planted_while_the_engine_was_failing_and_repair_converges()
    {
        await AddWalletAsync("drift-w");
        await FlushAsync();
        // The engine silently loses a tuple (as a restart or a bug might): nothing in the outbox knows.
        await Host.Get<IRelationshipStore>().DeleteAsync(new RelationshipFilter("wallet", "drift-w", "parent"), Ct);

        var found = await Host.Get<ReconciliationRunner>().RunAsync(repair: false, Ct);
        Assert.Equal(["wallet:drift-w#parent@domain:acme-uk"], found.Missing.Select(m => m.ToString()).ToArray());

        var repaired = await Host.Get<ReconciliationRunner>().RunAsync(repair: true, Ct);
        Assert.True(repaired.InSync);
        Assert.True(await CanAsync("alice", "view", Res("wallet", "drift-w")));
    }

    [Fact]
    public async Task Group_creation_during_an_outage_is_pending_then_visible_and_the_owner_edge_is_exact()
    {
        Host.Faults.Down = true;
        GroupCommandResult result = default!;
        await InScopeAsync(async sp => result = await sp.GetRequiredService<AccessService>().CreateGroupAsync(Actor.ForUser("alice"), GroupOwnerType.Customer, "acme", "Later", Ct, "later"));
        Assert.True(result.AuthorisationPending);

        Host.Faults.Down = false;
        await FlushAsync();

        Assert.Equal(["customer:acme"], (await Host.Get<IRelationshipStore>().ReadAsync(new RelationshipFilter("group", "later", "owner"), Ct)).Select(t => t.Subject.ToString()).ToArray());
        await AssertConvergedAsync();
    }
}
