using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Domain.Access;
using Poc.Infrastructure.Persistence;

namespace Poc.IntegrationTests.Platform;

/// <summary>One real SpiceDB and one fresh in-memory database per test, seeded by the real start-up sequence.</summary>
public abstract class PocTestBase : IAsyncLifetime
{
    protected static CancellationToken Ct => CancellationToken.None;

    protected PocHost Host { get; private set; } = null!;

    /// <summary>Whether to run the start-up sequence (seed scenario). Persistence-only tests override this.</summary>
    protected virtual bool SeedOnStartup => true;

    public async Task InitializeAsync()
    {
        Host = await PocHost.CreateAsync(SeedOnStartup);
        if (!SeedOnStartup) await Host.EnsureDatabaseAsync();
    }

    public async Task DisposeAsync() => await Host.DisposeAsync();

    protected static ResourceRef Res(string type, string id) => new(type, id);

    protected Task<bool> CanAsync(string user, string permission, ResourceRef resource) =>
        Host.Get<IAuthorizer>().CheckAsync(SubjectRef.User(user), permission, resource, Ct);

    protected async Task<List<Relationship>> AllTuplesAsync()
    {
        var store = Host.Get<IRelationshipStore>();
        var all = new List<Relationship>();
        foreach (var type in new[] { PlatformSchema.Type, GroupSchema.Type, CustomerSchema.Type, DomainSchema.Type, WalletSchema.Type, SubscriptionSchema.Type })
            all.AddRange(await store.ReadAsync(new RelationshipFilter(type), Ct));
        return all;
    }

    protected async Task<bool> HasTupleAsync(string tuple)
    {
        var wanted = ValidationYaml.ParseTuple(tuple);
        var found = await Host.Get<IRelationshipStore>().ReadAsync(new RelationshipFilter(wanted.Resource.Type, wanted.Resource.Id, wanted.Relation), Ct);
        return found.Contains(wanted);
    }

    /// <summary>Runs work in its own DI scope, which is how each request will see the container.</summary>
    protected async Task InScopeAsync(Func<IServiceProvider, Task> work)
    {
        await using var scope = Host.Scope();
        await work(scope.ServiceProvider);
    }

    protected async Task<T> QueryAsync<T>(Func<PocDbContext, Task<T>> query)
    {
        await using var db = await Host.Get<IDbContextFactory<PocDbContext>>().CreateDbContextAsync(Ct);
        return await query(db);
    }

    /// <summary>Saves whatever the callback changed through the repositories, in one unit of work.</summary>
    protected Task CommitAsync(Action<IServiceProvider> change) => InScopeAsync(async sp =>
    {
        change(sp);
        await sp.GetRequiredService<Poc.Application.Persistence.IUnitOfWork>().SaveChangesAsync(Ct);
    });

    protected Task<Poc.Application.Projection.FlushOutcome> FlushAsync() =>
        Host.Get<Poc.Application.Projection.IAuthorizationProjector>().FlushAsync(Ct);

    protected Task<List<GrantAuditEntry>> AuditAsync() => QueryAsync(db => db.GrantAudit.AsNoTracking().OrderBy(a => a.Id).ToListAsync(Ct));
}
