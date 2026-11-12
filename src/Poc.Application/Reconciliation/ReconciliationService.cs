using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Persistence;

namespace Poc.Application.Reconciliation;

/// <param name="Missing">Tuples the database implies that the engine lacks.</param>
/// <param name="Unexpected">Parent or owner tuples in the engine that no database row implies.</param>
/// <param name="SkippedPending">Objects left out because their outbox messages are still in flight.</param>
public sealed record ReconciliationReport(
    DateTimeOffset At,
    IReadOnlyList<Relationship> Missing,
    IReadOnlyList<Relationship> Unexpected,
    int SkippedPending)
{
    public bool InSync => Missing.Count == 0 && Unexpected.Count == 0;
}

/// <summary>
/// Compares every parent and owner reference in the application database with the matching tuples in the engine, in
/// both directions. It reports and never repairs by itself; <see cref="RepairAsync"/> is an explicit act.
/// </summary>
public sealed class ReconciliationService(IStructuralReferenceSource source, IRelationshipStore tuples, TimeProvider time)
{
    private const int RepairBatchSize = 500;

    /// <summary>The (resource type, relation) pairs that are structural. Grants are not reconciled here.</summary>
    private static readonly (string Type, string Relation)[] StructuralRelations =
    [
        (CustomerSchema.Type, CustomerSchema.Relations.Parent),
        (DomainSchema.Type, DomainSchema.Relations.Parent),
        (WalletSchema.Type, WalletSchema.Relations.Parent),
        (SubscriptionSchema.Type, SubscriptionSchema.Relations.Parent),
        (GroupSchema.Type, GroupSchema.Relations.Owner),
    ];

    public async Task<ReconciliationReport> RunAsync(CancellationToken ct)
    {
        // Read the pending set before the expected set so an object created in between is skipped, not misreported.
        var pending = await source.PendingObjectsAsync(ct).ConfigureAwait(false);
        var expected = (await source.ExpectedAsync(ct).ConfigureAwait(false)).ToHashSet();

        var actual = new HashSet<Relationship>();
        foreach (var (type, relation) in StructuralRelations)
        {
            foreach (var found in await tuples.ReadAsync(new RelationshipFilter(type, Relation: relation), ct).ConfigureAwait(false))
                actual.Add(found);
        }

        var missing = expected.Where(r => !actual.Contains(r) && !pending.Contains(r.Resource)).OrderBy(r => r.ToString(), StringComparer.Ordinal).ToList();
        var unexpected = actual.Where(r => !expected.Contains(r) && !pending.Contains(r.Resource)).OrderBy(r => r.ToString(), StringComparer.Ordinal).ToList();
        var skipped = pending.Count;
        return new ReconciliationReport(time.GetUtcNow(), missing, unexpected, skipped);
    }

    /// <summary>Writes the missing tuples and deletes the unexpected ones. Idempotent, so a partial repair can be re-run.</summary>
    public async Task RepairAsync(ReconciliationReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        var updates = report.Missing.Select(RelationshipUpdate.Touch)
            .Concat(report.Unexpected.Select(RelationshipUpdate.Delete))
            .ToList();
        foreach (var batch in updates.Chunk(RepairBatchSize))
            await tuples.WriteAsync(batch, ct).ConfigureAwait(false);
    }
}
