using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Authorization;
using Poc.Application.Persistence;
using Poc.Application.Reconciliation;
using Poc.Domain.Commerce;
using Poc.Infrastructure;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Phase3;

/// <summary>Reconciliation compares database references with engine tuples in both directions and never repairs by itself.</summary>
public sealed class ReconciliationTests : PocTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private Task<ReconciliationReport> RunAsync(bool repair = false) => Host.Get<ReconciliationRunner>().RunAsync(repair, Ct);

    private static string[] Names(IEnumerable<Relationship> relationships) => relationships.Select(r => r.ToString()).Order().ToArray();

    [Fact]
    public async Task A_freshly_seeded_system_is_in_sync()
    {
        var report = await RunAsync();
        Assert.True(report.InSync);
        Assert.Empty(report.Missing);
        Assert.Empty(report.Unexpected);
    }

    [Fact]
    public async Task A_tuple_deleted_behind_the_applications_back_is_reported_missing()
    {
        await Host.Get<IRelationshipStore>().DeleteAsync(new RelationshipFilter("wallet", "uk-ops", "parent"), Ct);

        var report = await RunAsync();

        Assert.False(report.InSync);
        Assert.Equal(["wallet:uk-ops#parent@domain:acme-uk"], Names(report.Missing));
        Assert.Empty(report.Unexpected);
        Assert.False(await CanAsync("alice", "view", Res("wallet", "uk-ops"))); // the drift has real consequences: access to the wallet is gone
    }

    [Fact]
    public async Task A_tuple_added_behind_the_applications_back_is_reported_unexpected()
    {
        // A second parent would let another domain's admins reach the wallet: exactly the drift worth catching.
        await Host.Get<IRelationshipStore>().WriteAsync(
            [RelationshipUpdate.Touch(ValidationYaml.ParseTuple("wallet:ie-ops#parent@domain:globex-main"))], Ct);

        var report = await RunAsync();

        Assert.Equal(["wallet:ie-ops#parent@domain:globex-main"], Names(report.Unexpected));
        Assert.Empty(report.Missing);
        Assert.True(await CanAsync("grace", "view", Res("wallet", "ie-ops")), "the injected parent really does leak access");
    }

    [Fact]
    public async Task A_group_owner_edge_is_reconciled_too()
    {
        await Host.Get<IRelationshipStore>().WriteAsync([RelationshipUpdate.Touch(ValidationYaml.ParseTuple("group:uk-it#owner@customer:globex"))], Ct);
        await Host.Get<IRelationshipStore>().DeleteAsync(new RelationshipFilter("group", "finance-ap", "owner"), Ct);

        var report = await RunAsync();

        Assert.Equal(["group:uk-it#owner@customer:globex"], Names(report.Unexpected));
        Assert.Equal(["group:finance-ap#owner@customer:acme"], Names(report.Missing));
    }

    [Fact]
    public async Task A_tuple_for_an_object_that_no_longer_exists_is_reported_unexpected()
    {
        await Host.Get<IRelationshipStore>().WriteAsync([RelationshipUpdate.Touch(ValidationYaml.ParseTuple("wallet:ghost#parent@domain:acme-uk"))], Ct);
        Assert.Equal(["wallet:ghost#parent@domain:acme-uk"], Names((await RunAsync()).Unexpected));
    }

    [Fact]
    public async Task Detection_alone_repairs_nothing()
    {
        await Host.Get<IRelationshipStore>().DeleteAsync(new RelationshipFilter("wallet", "uk-ops", "parent"), Ct);

        await RunAsync(repair: false);
        await RunAsync(repair: false);

        Assert.False(await HasTupleAsync("wallet:uk-ops#parent@domain:acme-uk"));
        Assert.False((await RunAsync()).InSync);
    }

    [Fact]
    public async Task An_explicit_repair_restores_missing_tuples_and_removes_unexpected_ones()
    {
        var store = Host.Get<IRelationshipStore>();
        await store.DeleteAsync(new RelationshipFilter("wallet", "uk-ops", "parent"), Ct);
        await store.WriteAsync([RelationshipUpdate.Touch(ValidationYaml.ParseTuple("wallet:ie-ops#parent@domain:globex-main"))], Ct);

        var repaired = await RunAsync(repair: true);

        Assert.True(repaired.InSync);
        Assert.True(await HasTupleAsync("wallet:uk-ops#parent@domain:acme-uk"));
        Assert.False(await HasTupleAsync("wallet:ie-ops#parent@domain:globex-main"));
        Assert.True(await CanAsync("alice", "view", Res("wallet", "uk-ops")));
        Assert.False(await CanAsync("grace", "view", Res("wallet", "ie-ops")));
    }

    [Fact]
    public async Task Repair_is_idempotent_and_a_second_run_is_a_no_op()
    {
        await Host.Get<IRelationshipStore>().DeleteAsync(new RelationshipFilter("wallet", "uk-ops", "parent"), Ct);
        await RunAsync(repair: true);
        var again = await RunAsync(repair: true);
        Assert.True(again.InSync);
    }

    [Fact]
    public async Task Objects_whose_messages_are_still_in_flight_are_not_reported_as_drift()
    {
        await CommitAsync(sp => sp.GetRequiredService<IRepository<Wallet>>().Add(Wallet.Create("acme-uk", "In flight", "alice", Now, "in-flight")));

        var during = await RunAsync();

        Assert.True(during.InSync, "a tuple that is about to be written is not drift");
        Assert.Equal(1, during.SkippedPending);
        await FlushAsync();
        var after = await RunAsync();
        Assert.True(after.InSync);
        Assert.Equal(0, after.SkippedPending);
    }

    [Fact]
    public async Task Drift_hiding_behind_a_dead_message_is_not_hidden_forever_but_stays_attributed_to_it()
    {
        // A dead message keeps its object "pending", so the missing tuple is not double-reported as drift; the System
        // screen reports the dead message instead. Once the message is gone the drift is visible.
        await CommitAsync(sp => sp.GetRequiredService<IRepository<Wallet>>().Add(Wallet.Create("acme-uk", "Stuck", "alice", Now, "stuck")));
        Host.Faults.PoisonWrite = u => u.Any(x => x.Relationship.Resource.Id == "stuck");
        for (var i = 0; i < 5; i++) await FlushAsync();

        var report = await RunAsync();
        Assert.Equal(1, report.SkippedPending);

        await QueryAsync(db => db.OutboxMessages.ExecuteDeleteAsync(Ct));
        Assert.Equal(["wallet:stuck#parent@domain:acme-uk"], Names((await RunAsync()).Missing));
    }

    [Fact]
    public async Task Grants_are_outside_the_scope_of_structural_reconciliation()
    {
        // Reconciliation covers parent and owner references only; grants live solely in the engine (PRD: Grants).
        await Host.Get<IRelationshipStore>().DeleteAsync(new RelationshipFilter("customer", "acme", "viewer"), Ct);
        Assert.True((await RunAsync()).InSync);
    }

    [Fact]
    public async Task The_runner_remembers_the_latest_report_for_the_system_screen()
    {
        var runner = Host.Get<ReconciliationRunner>();
        Assert.Null(runner.Latest);
        var report = await runner.RunAsync(repair: false, Ct);
        Assert.Same(report, runner.Latest);
    }

    [Fact]
    public async Task The_report_is_deterministic_and_sorted()
    {
        var store = Host.Get<IRelationshipStore>();
        await store.DeleteAsync(new RelationshipFilter("wallet", Relation: "parent"), Ct);

        var names = Names((await RunAsync()).Missing);

        Assert.Equal(4, names.Length);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }
}
