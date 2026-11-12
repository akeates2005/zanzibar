using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Poc.Application.Persistence;
using Poc.Domain.Commerce;
using Poc.Infrastructure;
using Poc.Infrastructure.Startup;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Phase3;

/// <summary>The two hosted services: the outbox flusher (every 2 s in production) and reconciliation (every 60 s).</summary>
public sealed class WorkerTests : PocTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private T Worker<T>()
        where T : class, IHostedService => Host.Services.GetServices<IHostedService>().OfType<T>().Single();

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }

        return false;
    }

    private Task AddWalletAsync(string id) =>
        CommitAsync(sp => sp.GetRequiredService<IRepository<Wallet>>().Add(Wallet.Create("acme-uk", id, "alice", Now, id)));

    [Fact]
    public async Task The_flush_worker_applies_a_pending_message_without_anyone_calling_flush()
    {
        var worker = Worker<OutboxFlushWorker>();
        await worker.StartAsync(Ct);
        try
        {
            await AddWalletAsync("by-worker");
            Assert.True(await WaitAsync(() => HasTupleAsync("wallet:by-worker#parent@domain:acme-uk")), "worker never projected the message");
        }
        finally
        {
            await worker.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task The_flush_worker_catches_up_after_an_outage_by_itself()
    {
        Host.Faults.Down = true;
        await AddWalletAsync("after-outage");
        var worker = Worker<OutboxFlushWorker>();
        await worker.StartAsync(Ct);
        try
        {
            await Task.Delay(400); // several ticks while the engine is down
            Assert.Equal(1, await QueryAsync(db => db.OutboxMessages.CountAsync(m => m.ProcessedAt == null, Ct))); // still waiting; the engine cannot be read while it is down
            Host.Faults.Down = false;
            Assert.True(await WaitAsync(() => HasTupleAsync("wallet:after-outage#parent@domain:acme-uk")), "worker did not catch up");
        }
        finally
        {
            await worker.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task The_flush_worker_does_nothing_until_start_up_has_completed()
    {
        await AddWalletAsync("too-early");
        Host.Get<RuntimeState>().MarkNotReady();
        var worker = Worker<OutboxFlushWorker>();
        await worker.StartAsync(Ct);
        try
        {
            await Task.Delay(500);
            Assert.False(await HasTupleAsync("wallet:too-early#parent@domain:acme-uk"));
            Host.Get<RuntimeState>().MarkReady();
            Assert.True(await WaitAsync(() => HasTupleAsync("wallet:too-early#parent@domain:acme-uk")));
        }
        finally
        {
            await worker.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task The_flush_worker_survives_a_failing_flush_and_keeps_going()
    {
        Host.Faults.PoisonWrite = u => u.Any(x => x.Relationship.Resource.Id == "bad");
        await AddWalletAsync("bad");
        var worker = Worker<OutboxFlushWorker>();
        await worker.StartAsync(Ct);
        try
        {
            await Task.Delay(300);
            await AddWalletAsync("good-after-bad");
            Assert.True(await WaitAsync(() => HasTupleAsync("wallet:good-after-bad#parent@domain:acme-uk")), "one bad message stopped the worker");
        }
        finally
        {
            await worker.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task The_reconciliation_worker_runs_on_its_own_and_reports_but_never_repairs()
    {
        await Host.Get<Poc.Application.Authorization.IRelationshipStore>().DeleteAsync(
            new Poc.Application.Authorization.RelationshipFilter("wallet", "uk-ops", "parent"), Ct);
        var worker = Worker<ReconciliationWorker>();
        await worker.StartAsync(Ct);
        try
        {
            var runner = Host.Get<ReconciliationRunner>();
            Assert.True(await WaitAsync(() => Task.FromResult(runner.Latest is { InSync: false })), "scheduled reconciliation never reported the drift");
            await Task.Delay(500);
            Assert.False(await HasTupleAsync("wallet:uk-ops#parent@domain:acme-uk")); // still missing: the job does not repair
        }
        finally
        {
            await worker.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task Workers_stop_promptly_on_shutdown()
    {
        var flush = Worker<OutboxFlushWorker>();
        var reconcile = Worker<ReconciliationWorker>();
        await flush.StartAsync(Ct);
        await reconcile.StartAsync(Ct);

        var stopped = Task.WhenAll(flush.StopAsync(Ct), reconcile.StopAsync(Ct));

        Assert.Same(stopped, await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(5))));
    }
}
