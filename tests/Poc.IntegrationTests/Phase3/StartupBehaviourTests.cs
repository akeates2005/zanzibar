using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Authorization;
using Poc.Application.Common;
using Poc.Domain.Access;
using Poc.Domain.Commerce;
using Poc.Infrastructure;
using Poc.Infrastructure.Startup;
using Poc.IntegrationTests.Fixtures;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Phase3;

/// <summary>What start-up leaves behind: audit, tokens, ready state, and a reset that really resets both stores.</summary>
public sealed class StartupBehaviourTests : PocTestBase
{
    [Fact]
    public async Task The_audit_is_complete_from_the_first_row_with_the_system_actor_and_every_row_applied()
    {
        var audit = await AuditAsync();

        Assert.Equal(13, audit.Count); // 5 memberships + 8 grants
        Assert.All(audit, a =>
        {
            Assert.Equal(GrantAction.Grant, a.Action);
            Assert.Equal(GrantStatus.Applied, a.Status);
            Assert.Equal(Actor.SystemUserId, a.ActorUserId);
            Assert.False(string.IsNullOrEmpty(a.ZedToken));
        });
        Assert.Equal(Enumerable.Range(1, 13).Select(i => (long)i), audit.Select(a => a.Id));
    }

    [Fact]
    public async Task The_audit_matches_the_seed_grant_tuples_one_for_one()
    {
        var (seed, _) = ValidationYaml.Load();
        var grantTuples = seed.Where(t => t.Relation is not ("parent" or "owner")).Select(t => t.ToString()).Order().ToList();
        var audited = (await AuditAsync()).Select(a =>
            $"{a.ResourceType}:{a.ResourceId}#{a.Relation}@{a.SubjectType}:{a.SubjectId}{(a.SubjectRelation is null ? "" : "#" + a.SubjectRelation)}").Order().ToList();

        Assert.Equal(grantTuples, audited);
    }

    [Fact]
    public async Task Every_structural_row_carries_the_zed_token_of_its_tuple_write()
    {
        Assert.All(await QueryAsync(db => db.Customers.AsNoTracking().ToListAsync(Ct)), c => Assert.False(string.IsNullOrEmpty(c.AuthzToken), c.Id));
        Assert.All(await QueryAsync(db => db.Domains.AsNoTracking().ToListAsync(Ct)), d => Assert.False(string.IsNullOrEmpty(d.AuthzToken), d.Id));
        Assert.All(await QueryAsync(db => db.Wallets.AsNoTracking().ToListAsync(Ct)), w => Assert.False(string.IsNullOrEmpty(w.AuthzToken), w.Id));
        Assert.All(await QueryAsync(db => db.Subscriptions.AsNoTracking().ToListAsync(Ct)), s => Assert.False(string.IsNullOrEmpty(s.AuthzToken), s.Id));
        Assert.All(await QueryAsync(db => db.Groups.AsNoTracking().ToListAsync(Ct)), g => Assert.False(string.IsNullOrEmpty(g.AuthzToken), g.Id));
    }

    [Fact]
    public async Task The_application_database_holds_the_whole_seed_scenario_and_no_grants_or_roles()
    {
        Assert.Equal(10, await QueryAsync(db => db.Users.CountAsync(Ct)));
        Assert.Equal(2, await QueryAsync(db => db.Customers.CountAsync(Ct)));
        Assert.Equal(3, await QueryAsync(db => db.Domains.CountAsync(Ct)));
        Assert.Equal(4, await QueryAsync(db => db.Wallets.CountAsync(Ct)));
        Assert.Equal(5, await QueryAsync(db => db.Subscriptions.CountAsync(Ct)));
        Assert.Equal(4, await QueryAsync(db => db.Groups.CountAsync(Ct)));
        Assert.Equal(0, await QueryAsync(db => db.OutboxMessages.CountAsync(m => m.ProcessedAt == null, Ct)));
        Assert.All(await QueryAsync(db => db.Customers.AsNoTracking().ToListAsync(Ct)), c => Assert.Equal(Actor.SystemUserId, c.CreatedBy));
    }

    [Fact]
    public async Task The_wallet_balances_come_only_from_top_ups()
    {
        Assert.Equal(5000m, await QueryAsync(db => db.Wallets.Where(w => w.Id == "uk-ops").Select(w => w.Balance).SingleAsync(Ct)));
    }

    [Fact]
    public async Task The_system_is_ready_after_startup_and_counts_its_runs()
    {
        Assert.True(Host.Get<RuntimeState>().IsReady);
        Assert.Equal(1, Host.Get<RuntimeState>().StartupCount);
        await Host.ResetAsync();
        Assert.Equal(2, Host.Get<RuntimeState>().StartupCount);
    }

    [Fact]
    public async Task Running_start_up_again_resets_both_stores_to_the_seed_scenario()
    {
        var (seed, _) = ValidationYaml.Load();
        // Make a mess in both stores: an extra customer (database + tuple) and an extra grant (tuple + audit).
        await CommitAsync(sp => sp.GetRequiredService<Poc.Application.Persistence.IRepository<Customer>>().Add(
            Customer.Create("Initech", "priya", DateTimeOffset.UtcNow, "initech")));
        await FlushAsync();
        await InScopeAsync(sp => sp.GetRequiredService<Poc.Application.Access.AccessService>().GrantAsync(
            Actor.System, new Poc.Application.Access.GrantTarget(Res("customer", "acme"), "viewer", SubjectRef.User("frank")), Ct));
        Assert.True(await CanAsync("frank", "view", Res("customer", "acme")));

        await Host.ResetAsync();

        Assert.Equal(seed.Select(t => t.ToString()).Order(), (await AllTuplesAsync()).Select(t => t.ToString()).Order());
        Assert.False(await CanAsync("frank", "view", Res("customer", "acme")));
        Assert.Equal(2, await QueryAsync(db => db.Customers.CountAsync(Ct)));
        Assert.Equal(13, (await AuditAsync()).Count);
        Assert.Equal(1L, (await AuditAsync()).First().Id); // identity counters restart with the data
        Assert.True((await Host.Get<ReconciliationRunner>().RunAsync(repair: false, Ct)).InSync);
    }

    [Fact]
    public async Task Start_up_survives_the_engine_losing_its_data_which_is_what_a_memory_datastore_restart_does()
    {
        // A restart wipes SpiceDB's memory. Simulate it by deleting every tuple, then run start-up again.
        var store = Host.Get<IRelationshipStore>();
        foreach (var type in new[] { "platform", "group", "customer", "domain", "wallet", "subscription" })
            await store.DeleteAsync(new RelationshipFilter(type), Ct);
        Assert.False(await CanAsync("priya", "view", Res("customer", "acme")));

        await Host.ResetAsync();

        Assert.True(await CanAsync("priya", "view", Res("customer", "acme")));
    }

    [Fact]
    public async Task If_the_engine_fails_during_start_up_the_system_is_left_not_ready_and_the_next_run_recovers()
    {
        Host.Faults.Down = true;

        await Assert.ThrowsAsync<AuthorizationUnavailableException>(() => Host.ResetAsync());
        Assert.False(Host.Get<RuntimeState>().IsReady);

        Host.Faults.Down = false;
        await Host.ResetAsync();
        Assert.True(Host.Get<RuntimeState>().IsReady);
        Assert.True(await CanAsync("priya", "view", Res("customer", "acme")));
    }

    [Fact]
    public async Task Concurrent_start_up_runs_are_serialised()
    {
        await Task.WhenAll(Host.ResetAsync(), Host.ResetAsync(), Host.ResetAsync());

        var (seed, _) = ValidationYaml.Load();
        Assert.Equal(seed.Select(t => t.ToString()).Order(), (await AllTuplesAsync()).Select(t => t.ToString()).Order());
        Assert.Equal(13, (await AuditAsync()).Count);
    }
}

/// <summary>Start-up against an engine that is absent, late, or refuses the key.</summary>
public sealed class StartupEngineAvailabilityTests
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Start_up_gives_up_with_a_timeout_when_the_engine_never_appears()
    {
        await using var host = await PocHost.CreateAsync(runStartup: false, o =>
        {
            o.SpiceDb.Endpoint = $"http://127.0.0.1:{NativeSpiceDbHost.FreePort()}";
            o.EngineStartupTimeout = TimeSpan.FromSeconds(1.5);
        });

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => host.ResetAsync());

        Assert.IsType<AuthorizationUnavailableException>(ex.InnerException);
        Assert.False(host.Get<RuntimeState>().IsReady);
    }

    [Fact]
    public async Task Start_up_waits_for_a_late_engine_and_then_completes()
    {
        var port = NativeSpiceDbHost.FreePort();
        const string key = "late-engine-key";
        await using var host = await PocHost.CreateAsync(runStartup: false, o =>
        {
            o.SpiceDb.Endpoint = $"http://127.0.0.1:{port}";
            o.SpiceDb.PresharedKey = key;
            o.EngineStartupTimeout = TimeSpan.FromSeconds(30);
        });

        var startup = Task.Run(() => host.ResetAsync());
        await Task.Delay(1500);
        Assert.False(startup.IsCompleted, "start-up should still be waiting for the engine");
        Assert.False(host.Get<RuntimeState>().IsReady);

        await using var lateEngine = await NativeSpiceDbHost.StartAsync(port, key);
        await startup.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(host.Get<RuntimeState>().IsReady);
        Assert.True(await host.Get<IAuthorizer>().CheckAsync(SubjectRef.User("priya"), "view", new ResourceRef("customer", "acme"), Ct));
    }

    [Fact]
    public async Task A_wrong_preshared_key_fails_fast_instead_of_waiting_out_the_timeout()
    {
        await using var host = await PocHost.CreateAsync(runStartup: false, o =>
        {
            o.SpiceDb.PresharedKey = "not-the-key";
            o.EngineStartupTimeout = TimeSpan.FromSeconds(30);
        });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AuthorizationUnavailableException>(() => host.ResetAsync());

        Assert.False(ex.IsTransient);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}: a misconfiguration must not be retried for the whole timeout");
        Assert.False(host.Get<RuntimeState>().IsReady);
    }
}
