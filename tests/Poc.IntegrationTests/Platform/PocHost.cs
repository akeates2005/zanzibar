using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Poc.Application.Authorization;
using Poc.Infrastructure;
using Poc.Infrastructure.Persistence;
using Poc.Infrastructure.SpiceDb;
using Poc.Infrastructure.Startup;
using Poc.IntegrationTests.Fixtures;

namespace Poc.IntegrationTests.Platform;

/// <summary>
/// The whole Phase 3 system in a test process: a real SpiceDB, the in-memory SQLite database, and the real container
/// (validated on build, so a singleton capturing a scoped service fails here). Background workers are registered but
/// not started unless a test starts them.
/// </summary>
public sealed class PocHost : IAsyncDisposable
{
    private readonly NativeSpiceDbHost _engine;

    private PocHost(NativeSpiceDbHost engine, ServiceProvider services, FaultInjectingRelationshipStore faults, PocOptions options)
    {
        _engine = engine;
        Services = services;
        Faults = faults;
        Options = options;
    }

    public ServiceProvider Services { get; }

    public FaultInjectingRelationshipStore Faults { get; }

    public PocOptions Options { get; }

    public static async Task<PocHost> CreateAsync(bool runStartup, Action<PocOptions>? configure = null)
    {
        var engine = await NativeSpiceDbHost.StartAsync();
        var options = new PocOptions
        {
            SqliteDatabaseName = $"poc-{Guid.NewGuid():N}",
            SchemaPath = Path.Combine(AppContext.BaseDirectory, "authz", "schema.zed"),
            OutboxFlushInterval = TimeSpan.FromMilliseconds(100),
            ReconciliationInterval = TimeSpan.FromMilliseconds(200),
            EngineStartupTimeout = TimeSpan.FromSeconds(10),
            SpiceDb = { Endpoint = engine.Endpoint, PresharedKey = engine.PresharedKey, AllowInsecureTransport = true },
        };
        configure?.Invoke(options);

        FaultInjectingRelationshipStore? faults = null;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPocInfrastructure(options);
        services.Replace(ServiceDescriptor.Singleton<IRelationshipStore>(sp =>
            faults = new FaultInjectingRelationshipStore(new SpiceDbRelationshipStore(sp.GetRequiredService<SpiceDbConnection>()))));

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var host = new PocHost(engine, provider, provider.GetRequiredService<IRelationshipStore>() as FaultInjectingRelationshipStore ?? faults!, options);
        if (runStartup) await host.ResetAsync();
        return host;
    }

    /// <summary>Creates the tables and opens the keep-alive connection without seeding, for persistence-only tests.</summary>
    public Task EnsureDatabaseAsync() =>
        Get<PocDatabase>().EnsureCreatedAsync(Get<IDbContextFactory<PocDbContext>>(), CancellationToken.None);

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public AsyncServiceScope Scope() => Services.CreateAsyncScope();

    /// <summary>Runs the full start-up sequence: a complete reset of both stores to the seed scenario.</summary>
    public Task ResetAsync() => Get<StartupSequence>().RunAsync(CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await _engine.DisposeAsync();
    }
}
