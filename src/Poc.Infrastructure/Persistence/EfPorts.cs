using Microsoft.EntityFrameworkCore;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Persistence;
using Poc.Domain.Access;
using Poc.Domain.Directory;

namespace Poc.Infrastructure.Persistence;

public sealed class EfRepository<T>(PocDbContext db) : IRepository<T>
    where T : class
{
    public async Task<T?> FindAsync(string id, CancellationToken ct) =>
        await db.Set<T>().FindAsync([id], ct).ConfigureAwait(false);

    public void Add(T entity) => db.Set<T>().Add(entity);

    public void Remove(T entity) => db.Set<T>().Remove(entity);
}

public sealed class EfUnitOfWork(PocDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct) => await db.SaveChangesAsync(ct).ConfigureAwait(false);
}

/// <summary>Each call uses its own context, so an audit write can never commit another command's half-finished changes.</summary>
public sealed class EfGrantAuditStore(IDbContextFactory<PocDbContext> factory) : IGrantAuditStore
{
    public async Task AddAsync(GrantAuditEntry entry, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.GrantAudit.Add(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(GrantAuditEntry entry, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.GrantAudit.Update(entry);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GrantAuditEntry>> ForResourceAsync(ResourceRef resource, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.GrantAudit.AsNoTracking()
            .Where(a => a.ResourceType == resource.Type && a.ResourceId == resource.Id)
            .OrderBy(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}

public sealed class EfDirectoryStore(PocDbContext db) : IDirectoryStore
{
    public Task<bool> UserExistsAsync(string userId, CancellationToken ct) =>
        db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, ct);

    public Task<Group?> FindGroupAsync(string groupId, CancellationToken ct) =>
        db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);

    public async Task<ObjectInfo?> DescribeAsync(ResourceRef resource, CancellationToken ct)
    {
        var id = resource.Id;
        switch (resource.Type)
        {
            case PlatformSchema.Type:
                return id == PlatformSchema.MainId ? new ObjectInfo(null) : null;
            case CustomerSchema.Type:
                return await db.Customers.AsNoTracking().AnyAsync(c => c.Id == id, ct).ConfigureAwait(false) ? new ObjectInfo(id) : null;
            case DomainSchema.Type:
                var domain = await db.Domains.AsNoTracking().Where(d => d.Id == id).Select(d => d.CustomerId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                return domain is null ? null : new ObjectInfo(domain);
            case WalletSchema.Type:
                var wallet = await (from w in db.Wallets.AsNoTracking()
                                    join d in db.Domains.AsNoTracking() on w.DomainId equals d.Id
                                    where w.Id == id
                                    select d.CustomerId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                return wallet is null ? null : new ObjectInfo(wallet);
            case SubscriptionSchema.Type:
                var subscription = await (from s in db.Subscriptions.AsNoTracking()
                                          join w in db.Wallets.AsNoTracking() on s.WalletId equals w.Id
                                          join d in db.Domains.AsNoTracking() on w.DomainId equals d.Id
                                          where s.Id == id
                                          select d.CustomerId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                return subscription is null ? null : new ObjectInfo(subscription);
            default:
                return null;
        }
    }
}

/// <summary>Sets <c>AuthzToken</c> without touching the row version, so a stamp never conflicts with a user's edit.</summary>
public sealed class EfAuthzTokenStore(IDbContextFactory<PocDbContext> factory) : IAuthzTokenStore
{
    public async Task StampAsync(ResourceRef resource, string zedToken, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var id = resource.Id;
        var rows = resource.Type switch
        {
            CustomerSchema.Type => db.Customers.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthzToken, zedToken), ct),
            DomainSchema.Type => db.Domains.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthzToken, zedToken), ct),
            WalletSchema.Type => db.Wallets.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthzToken, zedToken), ct),
            SubscriptionSchema.Type => db.Subscriptions.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthzToken, zedToken), ct),
            GroupSchema.Type => db.Groups.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AuthzToken, zedToken), ct),
            _ => Task.FromResult(0), // platform:main has no row
        };
        await rows.ConfigureAwait(false); // 0 rows is fine: the object may already be deleted
    }
}
