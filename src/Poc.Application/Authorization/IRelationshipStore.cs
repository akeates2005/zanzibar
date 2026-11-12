namespace Poc.Application.Authorization;

/// <summary>
/// Reads and writes tuples. Only the projector and the grant commands may call the write methods; Commerce code
/// never does (PRD rule 6).
/// </summary>
public interface IRelationshipStore
{
    /// <summary>Applies all updates atomically in one request and returns the ZedToken for the write.</summary>
    Task<string> WriteAsync(IReadOnlyCollection<RelationshipUpdate> updates, CancellationToken ct);

    Task<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, CancellationToken ct);

    /// <summary>Deletes every match (looping over partial deletions) and returns the last ZedToken.</summary>
    Task<string> DeleteAsync(RelationshipFilter filter, CancellationToken ct);
}
