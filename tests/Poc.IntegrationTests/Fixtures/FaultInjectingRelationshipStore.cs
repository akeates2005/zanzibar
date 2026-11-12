using Poc.Application.Authorization;

namespace Poc.IntegrationTests.Fixtures;

/// <summary>
/// Wraps the real store to simulate an engine outage or a poisoned write, so resilience paths run through the real
/// projector and commands. Every other call passes straight through to the real SpiceDB.
/// </summary>
public sealed class FaultInjectingRelationshipStore(IRelationshipStore inner) : IRelationshipStore
{
    /// <summary>While true every call fails as if the engine were unreachable (transient).</summary>
    public bool Down { get; set; }

    /// <summary>When set, a write for which this returns true is refused as if the engine rejected it (not transient).</summary>
    public Func<IReadOnlyCollection<RelationshipUpdate>, bool>? PoisonWrite { get; set; }

    public int WriteCalls { get; private set; }

    public Task<string> WriteAsync(IReadOnlyCollection<RelationshipUpdate> updates, CancellationToken ct)
    {
        WriteCalls++;
        ThrowIfDown("WriteRelationships");
        if (PoisonWrite?.Invoke(updates) == true)
            throw new AuthorizationUnavailableException("WriteRelationships", "InvalidArgument: injected poison", isTransient: false);
        return inner.WriteAsync(updates, ct);
    }

    public Task<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, CancellationToken ct)
    {
        ThrowIfDown("ReadRelationships");
        return inner.ReadAsync(filter, ct);
    }

    public Task<string> DeleteAsync(RelationshipFilter filter, CancellationToken ct)
    {
        ThrowIfDown("DeleteRelationships");
        return inner.DeleteAsync(filter, ct);
    }

    private void ThrowIfDown(string operation)
    {
        if (Down) throw new AuthorizationUnavailableException(operation, "Unavailable: injected outage", isTransient: true);
    }
}
