using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Poc.Application.Authorization;
using Poc.Application.Persistence;
using Poc.Application.Projection;
using Poc.Domain.Commerce;
using Poc.Infrastructure.Persistence;

namespace Poc.Infrastructure.Projection;

/// <summary>
/// Turns outbox messages into structural tuples. Messages are applied in sequence order and only one flush runs at a
/// time, so order is preserved. Writes and deletes are idempotent, so a message that is replayed after a crash is harmless.
/// </summary>
public sealed partial class OutboxProjector(
    IServiceScopeFactory scopes,
    IRelationshipStore tuples,
    PocOptions options,
    TimeProvider time,
    ILogger<OutboxProjector> logger) : IAuthorizationProjector, IOutboxInspector, IDisposable
{
    private const int ErrorLimit = 2000;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task<FlushOutcome> FlushAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FlushCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> while no flush can run, for operations that rewrite both stores. The action must
    /// not call <see cref="FlushAsync"/>, which would wait for this same gate.
    /// </summary>
    public async Task RunExclusiveAsync(Func<Task> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FlushOutcome> FlushCoreAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocDbContext>>();
        var stamps = scope.ServiceProvider.GetRequiredService<IAuthzTokenStore>();
        var policy = scope.ServiceProvider.GetRequiredService<DefaultGrantPolicy>();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var pending = await db.OutboxMessages.AsNoTracking()
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.Sequence)
            .ToListAsync(ct).ConfigureAwait(false);

        var blocked = new HashSet<ResourceRef>();
        int processed = 0, failed = 0;
        var engineUnavailable = false;

        foreach (var message in pending)
        {
            ProjectionPlan plan;
            Poc.Domain.Common.IDomainEvent domainEvent;
            try
            {
                domainEvent = OutboxEventSerializer.Deserialize(message.Type, message.Payload);
                plan = EventTupleMapper.Map(domainEvent);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
            {
                // A message we cannot even interpret cannot be ordered against others, so it blocks nothing; it just fails.
                await RecordFailureAsync(factory, message.Sequence, ex, countAttempt: true, ct).ConfigureAwait(false);
                failed++;
                continue;
            }

            if (blocked.Contains(plan.Target)) continue; // an earlier message for this object has not been applied
            if (message.Attempts >= options.MaxOutboxAttempts)
            {
                blocked.Add(plan.Target);
                continue;
            }

            try
            {
                string? token = null;
                if (plan.Updates.Count > 0)
                    token = await tuples.WriteAsync(plan.Updates, ct).ConfigureAwait(false);
                foreach (var filter in plan.Deletes)
                    token = await tuples.DeleteAsync(filter, ct).ConfigureAwait(false);

                if (token is not null) await stamps.StampAsync(plan.Target, token, ct).ConfigureAwait(false);
                if (domainEvent is SubscriptionCreated created) await policy.ApplyAsync(created, ct).ConfigureAwait(false);

                await MarkProcessedAsync(factory, message.Sequence, ct).ConfigureAwait(false);
                processed++;
            }
            catch (AuthorizationUnavailableException ex) when (ex.IsTransient)
            {
                // The engine is down or slow: record it, do not count it against the message, and stop, because the
                // remaining messages would fail the same way and order must be kept.
                await RecordFailureAsync(factory, message.Sequence, ex, countAttempt: false, ct).ConfigureAwait(false);
                LogEngineUnavailable(message.Sequence, message.Type);
                engineUnavailable = true;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordFailureAsync(factory, message.Sequence, ex, countAttempt: true, ct).ConfigureAwait(false);
                LogMessageFailed(message.Sequence, message.Type, message.Attempts + 1, ex);
                blocked.Add(plan.Target);
                failed++;
            }
        }

        var remaining = await db.OutboxMessages.CountAsync(m => m.ProcessedAt == null, ct).ConfigureAwait(false);
        return new FlushOutcome(processed, remaining, failed, engineUnavailable);
    }

    public async Task<OutboxStatus> GetStatusAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var unprocessed = await db.OutboxMessages.AsNoTracking().Where(m => m.ProcessedAt == null).OrderBy(m => m.Sequence).ToListAsync(ct).ConfigureAwait(false);
        var failures = unprocessed
            .Where(m => m.Attempts >= options.MaxOutboxAttempts)
            .Select(m => new OutboxFailure(m.Sequence, m.Type, m.Attempts, m.LastError ?? "", m.CreatedAt))
            .ToList();
        var oldest = unprocessed.Count == 0 ? (DateTimeOffset?)null : unprocessed.Min(m => m.CreatedAt);
        return new OutboxStatus(unprocessed.Count, failures.Count, oldest, failures);
    }

    private async Task MarkProcessedAsync(IDbContextFactory<PocDbContext> factory, long sequence, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        await db.OutboxMessages.Where(m => m.Sequence == sequence).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.ProcessedAt, now)
            .SetProperty(m => m.Attempts, m => m.Attempts + 1)
            .SetProperty(m => m.LastError, (string?)null), ct).ConfigureAwait(false);
    }

    private static async Task RecordFailureAsync(
        IDbContextFactory<PocDbContext> factory, long sequence, Exception exception, bool countAttempt, CancellationToken ct)
    {
        var text = exception.Message.Length > ErrorLimit ? exception.Message[..ErrorLimit] : exception.Message;
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.OutboxMessages.Where(m => m.Sequence == sequence);
        if (countAttempt)
            await query.ExecuteUpdateAsync(s => s.SetProperty(m => m.Attempts, m => m.Attempts + 1).SetProperty(m => m.LastError, text), ct).ConfigureAwait(false);
        else
            await query.ExecuteUpdateAsync(s => s.SetProperty(m => m.LastError, text), ct).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning, Message = "Outbox flush paused: engine unavailable at message {Sequence} ({Type}); will retry")]
    private partial void LogEngineUnavailable(long sequence, string type);

    [LoggerMessage(EventId = 21, Level = LogLevel.Error, Message = "Outbox message {Sequence} ({Type}) failed (attempt {Attempt})")]
    private partial void LogMessageFailed(long sequence, string type, int attempt, Exception exception);
}
