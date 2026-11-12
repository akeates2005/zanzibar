namespace Poc.Application.Authorization;

/// <summary>
/// Read access to tuples. Listings of grants and group membership use this and nothing more, so the compiler, not
/// convention, guarantees that a read-side service cannot write a tuple.
/// </summary>
public interface IRelationshipReader
{
    Task<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, CancellationToken ct);
}

/// <summary>
/// Reads and writes tuples. Only the projector and the grant commands may call the write methods; Commerce code
/// never does (PRD rule 6), and an architecture test lists every type allowed to depend on this interface.
/// </summary>
public interface IRelationshipStore : IRelationshipReader
{
    /// <summary>Applies all updates atomically in one request and returns the ZedToken for the write.</summary>
    Task<string> WriteAsync(IReadOnlyCollection<RelationshipUpdate> updates, CancellationToken ct);

    /// <summary>Deletes every match (looping over partial deletions) and returns the last ZedToken.</summary>
    Task<string> DeleteAsync(RelationshipFilter filter, CancellationToken ct);
}
