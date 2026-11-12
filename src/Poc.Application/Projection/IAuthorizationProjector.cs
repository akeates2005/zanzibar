namespace Poc.Application.Projection;

/// <param name="Processed">Messages applied to the engine in this flush.</param>
/// <param name="Remaining">Messages still unprocessed afterwards, including failed and blocked ones.</param>
/// <param name="Failed">Messages that failed in this flush for a reason a retry will not fix.</param>
/// <param name="EngineUnavailable">The flush stopped because the engine was unreachable or timed out.</param>
public sealed record FlushOutcome(int Processed, int Remaining, int Failed, bool EngineUnavailable)
{
    /// <summary>True when nothing is waiting: every structural tuple is in the engine, so the API can return 200/201.</summary>
    public bool FullyApplied => Remaining == 0;
}

public interface IAuthorizationProjector
{
    /// <summary>Applies pending outbox messages in sequence order. Only one flush runs at a time.</summary>
    Task<FlushOutcome> FlushAsync(CancellationToken ct);
}
