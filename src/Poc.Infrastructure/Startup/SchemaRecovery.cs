using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Poc.Application.Authorization;

namespace Poc.Infrastructure.Startup;

public enum EngineStatus
{
    /// <summary>The engine answers and holds the schema.</summary>
    Ok,

    /// <summary>The engine answers but holds no schema: it has lost its data (a memory-datastore restart).</summary>
    SchemaMissing,

    /// <summary>The engine did not answer.</summary>
    Unreachable,
}

/// <summary>
/// SpiceDB's memory datastore loses everything if its container restarts (PRD: Start-up and reconciliation). Probing reads
/// the schema; if it is gone while the API is serving, the API marks itself unready, runs the full start-up sequence again
/// (which rebuilds both stores) and logs the reset. Probing is done by the health check and by <see cref="EngineWatchdog"/>,
/// so recovery does not depend on anyone asking for health. A recovery that fails is retried until it succeeds.
/// </summary>
public sealed partial class SchemaRecovery(
    ISchemaStore schema,
    StartupSequence startup,
    RuntimeState state,
    PocOptions options,
    ILogger<SchemaRecovery> logger) : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private int _recovering;

    /// <summary>Whether a rebuild after schema loss is running right now.</summary>
    public bool IsRecovering => Volatile.Read(ref _recovering) == 1;

    public async Task<EngineStatus> ProbeAsync(CancellationToken ct)
    {
        string? text;
        try
        {
            text = await schema.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (AuthorizationUnavailableException)
        {
            return EngineStatus.Unreachable;
        }

        if (text is not null) return EngineStatus.Ok;

        // Schema missing. During start-up or a reset that is expected (the schema is written early in the sequence), so only
        // a system that believes it is ready has anything to recover from.
        if (state.IsReady) BeginRecovery();
        return EngineStatus.SchemaMissing;
    }

    private void BeginRecovery()
    {
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0) return;
        state.MarkNotReady(); // /api answers 503 from this moment, instead of reading an engine that has no schema
        LogSchemaLost();
        _ = Task.Run(RecoverAsync);
    }

    private async Task RecoverAsync()
    {
        try
        {
            var token = _stopping.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await startup.RunAsync(token).ConfigureAwait(false);
                    LogRecovered();
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogRecoveryFailed(ex);
                    await Task.Delay(options.RecoveryRetryInterval, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            Volatile.Write(ref _recovering, 0);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    [LoggerMessage(EventId = 80, Level = LogLevel.Warning,
        Message = "SpiceDB has lost its schema (a memory-datastore restart). The system is now unready and will rebuild both stores; changes made since start-up are lost.")]
    private partial void LogSchemaLost();

    [LoggerMessage(EventId = 81, Level = LogLevel.Information, Message = "Recovered from schema loss: both stores were rebuilt")]
    private partial void LogRecovered();

    [LoggerMessage(EventId = 82, Level = LogLevel.Error, Message = "Recovery from schema loss failed; will retry")]
    private partial void LogRecoveryFailed(Exception exception);
}

/// <summary>Every <see cref="PocOptions.SchemaCheckInterval"/>, checks that the engine still holds its schema.</summary>
public sealed partial class EngineWatchdog(SchemaRecovery recovery, PocOptions options, ILogger<EngineWatchdog> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SchemaCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await recovery.ProbeAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogProbeFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    [LoggerMessage(EventId = 83, Level = LogLevel.Error, Message = "Engine schema check failed")]
    private partial void LogProbeFailed(Exception exception);
}
