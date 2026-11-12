using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Poc.Application.Access;
using Poc.Application.Persistence;
using Poc.Application.Projection;
using Poc.Application.Reconciliation;
using Poc.Infrastructure.Persistence;
using Poc.Infrastructure.Projection;
using Poc.Infrastructure.Seeding;
using Poc.Infrastructure.SpiceDb;
using Poc.Infrastructure.Startup;

namespace Poc.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers persistence, the outbox projector, access management, reconciliation, seeding, start-up and the two
    /// background workers. Call <see cref="StartupSequence.RunAsync"/> once the host is up to build both stores.
    /// </summary>
    public static IServiceCollection AddPocInfrastructure(this IServiceCollection services, PocOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSpiceDbAuthorization(options.SpiceDb);

        services.AddSingleton<PocDatabase>();
        services.AddDbContextFactory<PocDbContext>((sp, builder) => builder.UseSqlite(sp.GetRequiredService<PocDatabase>().ConnectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PocDbContext>>().CreateDbContext());
        services.AddScoped(typeof(IRepository<>), typeof(EfRepository<>));
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IDirectoryStore, EfDirectoryStore>();
        services.AddSingleton<IGrantAuditStore, EfGrantAuditStore>();
        services.AddSingleton<IAuthzTokenStore, EfAuthzTokenStore>();
        services.AddSingleton<IStructuralReferenceSource, EfReconciliationSource>();
        services.AddSingleton<IAccessLog, AccessLog>();

        services.AddSingleton<OutboxProjector>();
        services.AddSingleton<IAuthorizationProjector>(sp => sp.GetRequiredService<OutboxProjector>());
        services.AddSingleton<IOutboxInspector>(sp => sp.GetRequiredService<OutboxProjector>());
        services.AddScoped<AccessService>();
        services.AddScoped<DefaultGrantPolicy>();

        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<ReconciliationRunner>();
        services.AddSingleton<RuntimeState>();
        services.AddScoped<Seeder>();
        services.AddSingleton<StartupSequence>();

        services.AddHostedService<OutboxFlushWorker>();
        services.AddHostedService<ReconciliationWorker>();
        return services;
    }
}
