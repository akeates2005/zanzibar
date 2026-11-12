using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Poc.Application.Reconciliation;
using Poc.Infrastructure.Projection;
using Poc.Infrastructure.Startup;

namespace Poc.Infrastructure;

/// <summary>Runs reconciliation and keeps the latest result for the System screen. Used by the worker and on demand.</summary>
public sealed partial class ReconciliationRunner(ReconciliationService service, ILogger<ReconciliationRunner> logger)
{
    public ReconciliationReport? Latest { get; private set; }

    public async Task<ReconciliationReport> RunAsync(bool repair, CancellationToken ct)
    {
        var report = await service.RunAsync(ct).ConfigureAwait(false);
        if (repair && !report.InSync)
        {
            await service.RepairAsync(report, ct).ConfigureAwait(false);
            LogRepaired(report.Missing.Count, report.Unexpected.Count);
            report = await service.RunAsync(ct).ConfigureAwait(false);
        }

        Latest = report;
        if (!report.InSync) LogDrift(report.Missing.Count, report.Unexpected.Count);
        return report;
    }

    [LoggerMessage(EventId = 40, Level = LogLevel.Warning, Message = "Reconciliation found drift: {Missing} missing, {Unexpected} unexpected tuple(s)")]
    private partial void LogDrift(int missing, int unexpected);

    [LoggerMessage(EventId = 41, Level = LogLevel.Warning, Message = "Reconciliation repaired drift: wrote {Missing}, deleted {Unexpected}")]
    private partial void LogRepaired(int missing, int unexpected);
}

/// <summary>Every <see cref="PocOptions.OutboxFlushInterval"/>, applies pending outbox messages.</summary>
public sealed partial class OutboxFlushWorker(
    OutboxProjector projector, RuntimeState state, PocOptions options, ILogger<OutboxFlushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.OutboxFlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!state.IsReady) continue;
                try
                {
                    await projector.FlushAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFlushFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    [LoggerMessage(EventId = 50, Level = LogLevel.Error, Message = "Background outbox flush failed")]
    private partial void LogFlushFailed(Exception exception);
}

/// <summary>Every <see cref="PocOptions.ReconciliationInterval"/>, compares the database with the engine. Never repairs.</summary>
public sealed partial class ReconciliationWorker(
    ReconciliationRunner runner, RuntimeState state, PocOptions options, ILogger<ReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ReconciliationInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!state.IsReady) continue;
                try
                {
                    await runner.RunAsync(repair: false, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRunFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    [LoggerMessage(EventId = 51, Level = LogLevel.Error, Message = "Scheduled reconciliation failed")]
    private partial void LogRunFailed(Exception exception);
}
