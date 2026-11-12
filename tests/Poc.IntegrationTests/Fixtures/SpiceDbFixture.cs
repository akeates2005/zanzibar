using Microsoft.Extensions.Logging.Abstractions;
using Poc.Application.Authorization;
using Poc.Infrastructure.SpiceDb;

namespace Poc.IntegrationTests.Fixtures;

/// <summary>One SpiceDB with the real schema loaded, shared by every test in the collection.</summary>
public sealed class SpiceDbFixture : IAsyncLifetime
{
    private NativeSpiceDbHost? _host;

    public SpiceDbConnection Connection { get; private set; } = null!;

    public IAuthorizer Authorizer { get; private set; } = null!;

    public IRelationshipStore Relationships { get; private set; } = null!;

    public ISchemaStore Schema { get; private set; } = null!;

    public ISpiceDbHost Host => _host ?? throw new InvalidOperationException("Fixture not initialised.");

    public static string SchemaText => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "authz", "schema.zed"));

    public async Task InitializeAsync()
    {
        _host = await NativeSpiceDbHost.StartAsync();
        Connection = NewConnection(_host);
        Authorizer = new SpiceDbAuthorizer(Connection, NullLogger<SpiceDbAuthorizer>.Instance);
        Relationships = new SpiceDbRelationshipStore(Connection);
        Schema = new SpiceDbSchemaStore(Connection);
        await Schema.WriteAsync(SchemaText, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        Connection?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    public static SpiceDbConnection NewConnection(ISpiceDbHost host, Action<SpiceDbOptions>? configure = null)
    {
        var options = new SpiceDbOptions
        {
            Endpoint = host.Endpoint,
            PresharedKey = host.PresharedKey,
            AllowInsecureTransport = true,
        };
        configure?.Invoke(options);
        return new SpiceDbConnection(options);
    }

    /// <summary>A short unique suffix so tests sharing one engine never see each other's objects.</summary>
    public static string Unique() => Guid.NewGuid().ToString("N")[..10];
}

[CollectionDefinition(Name)]
public sealed class SpiceDbCollectionDefinition : ICollectionFixture<SpiceDbFixture>
{
    public const string Name = "SpiceDB";
}
