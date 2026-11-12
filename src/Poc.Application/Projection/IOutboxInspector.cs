namespace Poc.Application.Projection;

public sealed record OutboxFailure(long Sequence, string Type, int Attempts, string LastError, DateTimeOffset CreatedAt);

/// <param name="Pending">Unprocessed messages, including failed ones.</param>
/// <param name="Failed">Messages that exhausted their attempts and are waiting for an operator.</param>
/// <param name="OldestPendingAt">When the oldest unprocessed message was written; the basis of "outbox lag".</param>
public sealed record OutboxStatus(int Pending, int Failed, DateTimeOffset? OldestPendingAt, IReadOnlyList<OutboxFailure> Failures);

public interface IOutboxInspector
{
    Task<OutboxStatus> GetStatusAsync(CancellationToken ct);
}
