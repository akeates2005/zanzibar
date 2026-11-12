using Poc.Application.Authorization;

namespace Poc.IntegrationTests.Fixtures;

/// <summary>
/// Wraps the real store to simulate the ways a network and an engine really fail, so resilience paths run through the real
/// projector and commands. Every other call passes straight through to the real SpiceDB.
/// </summary>
public sealed class FaultInjectingRelationshipStore(IRelationshipStore inner) : IRelationshipStore
{
    private readonly object _gate = new();
    private Random _random = new(1);
    private int _deleteCalls;

    /// <summary>While true every call fails as if the engine were unreachable (transient).</summary>
    public bool Down { get; set; }

    /// <summary>When set, a write for which this returns true is refused as if the engine rejected it (not transient).</summary>
    public Func<IReadOnlyCollection<RelationshipUpdate>, bool>? PoisonWrite { get; set; }

    /// <summary>
    /// The write reaches the engine and succeeds, but the caller is told it failed (a lost response, a timeout after the
    /// fact). The classic way to find out whether replaying is really harmless.
    /// </summary>
    public bool LoseResponseAfterWrite { get; set; }

    /// <summary>Each write or delete fails transiently with this probability, decided by a seeded generator so a run repeats.</summary>
    public double FailureRate { get; set; }

    /// <summary>Makes the Nth delete call from now (1 = the next) fail once, to break a multi-step plan part way through.</summary>
    public int? FailDeleteCallNumber { get; set; }

    public int WriteCalls { get; private set; }

    public void ResetCounters()
    {
        lock (_gate)
        {
            _deleteCalls = 0;
            _random = new Random(1);
        }
    }

    public async Task<string> WriteAsync(IReadOnlyCollection<RelationshipUpdate> updates, CancellationToken ct)
    {
        WriteCalls++;
        ThrowIfDown("WriteRelationships");
        MaybeFlap("WriteRelationships");
        if (PoisonWrite?.Invoke(updates) == true)
            throw new AuthorizationUnavailableException("WriteRelationships", "InvalidArgument: injected poison", isTransient: false);
        var token = await inner.WriteAsync(updates, ct);
        if (LoseResponseAfterWrite)
            throw new AuthorizationUnavailableException("WriteRelationships", "DeadlineExceeded: injected lost response (the write did land)", isTransient: true);
        return token;
    }

    public Task<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, CancellationToken ct)
    {
        ThrowIfDown("ReadRelationships");
        return inner.ReadAsync(filter, ct);
    }

    public Task<string> DeleteAsync(RelationshipFilter filter, CancellationToken ct)
    {
        ThrowIfDown("DeleteRelationships");
        MaybeFlap("DeleteRelationships");
        lock (_gate)
        {
            _deleteCalls++;
            if (FailDeleteCallNumber == _deleteCalls)
            {
                FailDeleteCallNumber = null; // once
                throw new AuthorizationUnavailableException("DeleteRelationships", "Unavailable: injected failure part way through a plan", isTransient: true);
            }
        }

        return inner.DeleteAsync(filter, ct);
    }

    private void ThrowIfDown(string operation)
    {
        if (Down) throw new AuthorizationUnavailableException(operation, "Unavailable: injected outage", isTransient: true);
    }

    private void MaybeFlap(string operation)
    {
        if (FailureRate <= 0) return;
        bool fail;
        lock (_gate) fail = _random.NextDouble() < FailureRate;
        if (fail) throw new AuthorizationUnavailableException(operation, "Unavailable: injected flapping", isTransient: true);
    }
}
