using Microsoft.EntityFrameworkCore;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Queries;
using Poc.Domain.Directory;

namespace Poc.Infrastructure.Persistence;

/// <summary>Read side of Commerce over the application database.</summary>
public sealed class EfCommerceQueries(IDbContextFactory<PocDbContext> factory) : ICommerceQueries
{
    public async Task<bool> HasChildrenAsync(ResourceRef resource, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var id = resource.Id;
        return resource.Type switch
        {
            CustomerSchema.Type => await db.Domains.AnyAsync(d => d.CustomerId == id, ct).ConfigureAwait(false)
                || await db.Groups.AnyAsync(g => g.OwnerType == GroupOwnerType.Customer && g.OwnerId == id, ct).ConfigureAwait(false),
            DomainSchema.Type => await db.Wallets.AnyAsync(w => w.DomainId == id, ct).ConfigureAwait(false),
            WalletSchema.Type => await db.Subscriptions.AnyAsync(s => s.WalletId == id, ct).ConfigureAwait(false),
            _ => false,
        };
    }

    public async Task<IReadOnlyList<ObjectSummary>> GetSummariesAsync(IEnumerable<ResourceRef> resources, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var byType = resources.GroupBy(r => r.Type, r => r.Id).ToDictionary(g => g.Key, g => g.Distinct().ToList());
        var result = new List<ObjectSummary>();

        if (byType.TryGetValue(PlatformSchema.Type, out var platform) && platform.Contains(PlatformSchema.MainId))
            result.Add(new ObjectSummary(PlatformSchema.Type, PlatformSchema.MainId, "Platform", null, null));
        if (byType.TryGetValue(CustomerSchema.Type, out var customers))
        {
            result.AddRange((await db.Customers.AsNoTracking().Where(c => customers.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToListAsync(ct).ConfigureAwait(false))
                .Select(c => new ObjectSummary(CustomerSchema.Type, c.Id, c.Name, PlatformSchema.Type, PlatformSchema.MainId)));
        }

        if (byType.TryGetValue(DomainSchema.Type, out var domains))
        {
            result.AddRange((await db.Domains.AsNoTracking().Where(d => domains.Contains(d.Id)).Select(d => new { d.Id, d.Name, d.CustomerId }).ToListAsync(ct).ConfigureAwait(false))
                .Select(d => new ObjectSummary(DomainSchema.Type, d.Id, d.Name, CustomerSchema.Type, d.CustomerId)));
        }

        if (byType.TryGetValue(WalletSchema.Type, out var wallets))
        {
            result.AddRange((await db.Wallets.AsNoTracking().Where(w => wallets.Contains(w.Id)).Select(w => new { w.Id, w.Name, w.DomainId }).ToListAsync(ct).ConfigureAwait(false))
                .Select(w => new ObjectSummary(WalletSchema.Type, w.Id, w.Name, DomainSchema.Type, w.DomainId)));
        }

        if (byType.TryGetValue(SubscriptionSchema.Type, out var subscriptions))
        {
            result.AddRange((await db.Subscriptions.AsNoTracking().Where(s => subscriptions.Contains(s.Id)).Select(s => new { s.Id, s.ProductName, s.WalletId }).ToListAsync(ct).ConfigureAwait(false))
                .Select(s => new ObjectSummary(SubscriptionSchema.Type, s.Id, s.ProductName, WalletSchema.Type, s.WalletId)));
        }

        return result;
    }

    public async Task<IReadOnlyList<ObjectSummary>> ChildrenAsync(ResourceRef parent, IReadOnlyCollection<string> allowedIds, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var id = parent.Id;
        switch (parent.Type)
        {
            case PlatformSchema.Type:
                return (await db.Customers.AsNoTracking().Where(c => allowedIds.Contains(c.Id)).OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).ToListAsync(ct).ConfigureAwait(false))
                    .Select(c => new ObjectSummary(CustomerSchema.Type, c.Id, c.Name, PlatformSchema.Type, PlatformSchema.MainId)).ToList();
            case CustomerSchema.Type:
                return (await db.Domains.AsNoTracking().Where(d => d.CustomerId == id && allowedIds.Contains(d.Id)).OrderBy(d => d.Name).Select(d => new { d.Id, d.Name }).ToListAsync(ct).ConfigureAwait(false))
                    .Select(d => new ObjectSummary(DomainSchema.Type, d.Id, d.Name, CustomerSchema.Type, id)).ToList();
            case DomainSchema.Type:
                return (await db.Wallets.AsNoTracking().Where(w => w.DomainId == id && allowedIds.Contains(w.Id)).OrderBy(w => w.Name).Select(w => new { w.Id, w.Name }).ToListAsync(ct).ConfigureAwait(false))
                    .Select(w => new ObjectSummary(WalletSchema.Type, w.Id, w.Name, DomainSchema.Type, id)).ToList();
            case WalletSchema.Type:
                return (await db.Subscriptions.AsNoTracking().Where(s => s.WalletId == id && allowedIds.Contains(s.Id)).OrderBy(s => s.ProductName).Select(s => new { s.Id, s.ProductName }).ToListAsync(ct).ConfigureAwait(false))
                    .Select(s => new ObjectSummary(SubscriptionSchema.Type, s.Id, s.ProductName, WalletSchema.Type, id)).ToList();
            default:
                return [];
        }
    }

    public async Task<IReadOnlyList<ObjectSummary>> AncestorsAsync(ResourceRef resource, CancellationToken ct)
    {
        var chain = new List<ObjectSummary>();
        var current = (await GetSummariesAsync([resource], ct).ConfigureAwait(false)) is { Count: > 0 } found ? found[0] : null;
        while (current?.ParentType is not null)
        {
            var parent = (await GetSummariesAsync([new ResourceRef(current.ParentType, current.ParentId!)], ct).ConfigureAwait(false)) is { Count: > 0 } up ? up[0] : null;
            if (parent is null) break;
            chain.Add(parent);
            current = parent;
        }

        chain.Reverse();
        return chain;
    }
}

/// <summary>Read side of the user and group directories.</summary>
public sealed class EfDirectoryQueries(IDbContextFactory<PocDbContext> factory) : IDirectoryQueries
{
    public async Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string? query, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var users = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            // LIKE with an escape character so a search for "%" or "_" matches the literal text, not everything.
            var pattern = "%" + query.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            users = users.Where(u => EF.Functions.Like(u.DisplayName, pattern, "\\") || EF.Functions.Like(u.Email, pattern, "\\") || EF.Functions.Like(u.Id, pattern, "\\"));
        }

        return await users.OrderBy(u => u.DisplayName).Take(limit)
            .Select(u => new DirectoryUser(u.Id, u.DisplayName, u.Email)).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, DirectoryUser>> GetUsersAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<string, DirectoryUser>();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id))
            .Select(u => new DirectoryUser(u.Id, u.DisplayName, u.Email)).ToDictionaryAsync(u => u.Id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, GroupSummary>> GetGroupsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<string, GroupSummary>();
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Groups.AsNoTracking().Where(g => wanted.Contains(g.Id))
            .Select(g => new GroupSummary(g.Id, g.Name, g.OwnerType, g.OwnerId)).ToDictionaryAsync(g => g.Id, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GroupSummary>> GroupsOwnedByAsync(
        GroupOwnerType ownerType, string ownerId, IReadOnlyCollection<string> allowedIds, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Groups.AsNoTracking()
            .Where(g => g.OwnerType == ownerType && g.OwnerId == ownerId && allowedIds.Contains(g.Id))
            .OrderBy(g => g.Name).Select(g => new GroupSummary(g.Id, g.Name, g.OwnerType, g.OwnerId)).ToListAsync(ct).ConfigureAwait(false);
    }
}
