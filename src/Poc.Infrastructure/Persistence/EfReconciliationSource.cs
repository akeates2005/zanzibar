using Microsoft.EntityFrameworkCore;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Persistence;
using Poc.Application.Projection;
using Poc.Domain.Directory;

namespace Poc.Infrastructure.Persistence;

public sealed class EfReconciliationSource(IDbContextFactory<PocDbContext> factory) : IStructuralReferenceSource
{
    public async Task<IReadOnlyList<Relationship>> ExpectedAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var expected = new List<Relationship>();

        foreach (var id in await db.Customers.AsNoTracking().Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false))
            expected.Add(Edge(CustomerSchema.Type, id, CustomerSchema.Relations.Parent, PlatformSchema.Type, PlatformSchema.MainId));
        foreach (var d in await db.Domains.AsNoTracking().Select(d => new { d.Id, d.CustomerId }).ToListAsync(ct).ConfigureAwait(false))
            expected.Add(Edge(DomainSchema.Type, d.Id, DomainSchema.Relations.Parent, CustomerSchema.Type, d.CustomerId));
        foreach (var w in await db.Wallets.AsNoTracking().Select(w => new { w.Id, w.DomainId }).ToListAsync(ct).ConfigureAwait(false))
            expected.Add(Edge(WalletSchema.Type, w.Id, WalletSchema.Relations.Parent, DomainSchema.Type, w.DomainId));
        foreach (var s in await db.Subscriptions.AsNoTracking().Select(s => new { s.Id, s.WalletId }).ToListAsync(ct).ConfigureAwait(false))
            expected.Add(Edge(SubscriptionSchema.Type, s.Id, SubscriptionSchema.Relations.Parent, WalletSchema.Type, s.WalletId));
        foreach (var g in await db.Groups.AsNoTracking().Select(g => new { g.Id, g.OwnerType, g.OwnerId }).ToListAsync(ct).ConfigureAwait(false))
        {
            expected.Add(g.OwnerType == GroupOwnerType.Platform
                ? Edge(GroupSchema.Type, g.Id, GroupSchema.Relations.Owner, PlatformSchema.Type, PlatformSchema.MainId)
                : Edge(GroupSchema.Type, g.Id, GroupSchema.Relations.Owner, CustomerSchema.Type, g.OwnerId));
        }

        return expected;
    }

    public async Task<IReadOnlySet<ResourceRef>> PendingObjectsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var messages = await db.OutboxMessages.AsNoTracking().Where(m => m.ProcessedAt == null).ToListAsync(ct).ConfigureAwait(false);
        var pending = new HashSet<ResourceRef>();
        foreach (var message in messages)
        {
            try
            {
                pending.Add(EventTupleMapper.Map(OutboxEventSerializer.Deserialize(message.Type, message.Payload)).Target);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
            {
                // Uninterpretable messages cannot be attributed to an object; the System screen shows them as failures.
            }
        }

        return pending;
    }

    private static Relationship Edge(string type, string id, string relation, string parentType, string parentId) =>
        new(new ResourceRef(type, id), relation, new SubjectRef(parentType, parentId));
}
