using Poc.Application.Authorization;
using Poc.Infrastructure.SpiceDb;
using Poc.IntegrationTests.Fixtures;

namespace Poc.IntegrationTests.Adapter;

/// <summary>Uses its own SpiceDB so it can observe the "no schema yet" state that the shared fixture never has.</summary>
public sealed class SchemaStoreTests : IAsyncLifetime
{
    private static CancellationToken Ct => CancellationToken.None;
    private NativeSpiceDbHost _host = null!;
    private SpiceDbConnection _connection = null!;
    private SpiceDbSchemaStore _schema = null!;

    public async Task InitializeAsync()
    {
        _host = await NativeSpiceDbHost.StartAsync();
        _connection = SpiceDbFixture.NewConnection(_host);
        _schema = new SpiceDbSchemaStore(_connection);
    }

    public async Task DisposeAsync()
    {
        _connection.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Read_returns_null_when_the_engine_holds_no_schema()
    {
        Assert.Null(await _schema.ReadAsync(Ct));
    }

    [Fact]
    public async Task Write_then_read_round_trips_the_real_schema()
    {
        await _schema.WriteAsync(SpiceDbFixture.SchemaText, Ct);

        var text = await _schema.ReadAsync(Ct);

        Assert.NotNull(text);
        foreach (var type in new[] { "user", "platform", "group", "customer", "domain", "wallet", "subscription" })
            Assert.Contains($"definition {type}", text);
    }

    [Fact]
    public async Task Rewriting_the_schema_is_idempotent()
    {
        await _schema.WriteAsync(SpiceDbFixture.SchemaText, Ct);
        await _schema.WriteAsync(SpiceDbFixture.SchemaText, Ct);
        Assert.NotNull(await _schema.ReadAsync(Ct));
    }

    [Fact]
    public async Task An_invalid_schema_is_rejected_with_a_typed_exception()
    {
        await Assert.ThrowsAsync<AuthorizationUnavailableException>(() => _schema.WriteAsync("definition {{{ nonsense", Ct));
        Assert.Null(await _schema.ReadAsync(Ct));
    }

    [Fact]
    public async Task Writing_blank_schema_text_is_rejected_before_any_call()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _schema.WriteAsync("  ", Ct));
    }

    [Fact]
    public async Task The_engine_losing_its_schema_is_visible_to_the_health_probe()
    {
        // Memory datastore restart simulation: a brand-new instance has no schema even though the old one did.
        await _schema.WriteAsync(SpiceDbFixture.SchemaText, Ct);
        await using var fresh = await NativeSpiceDbHost.StartAsync();
        using var freshConnection = SpiceDbFixture.NewConnection(fresh);
        var freshSchema = new SpiceDbSchemaStore(freshConnection);

        Assert.NotNull(await _schema.ReadAsync(Ct));
        Assert.Null(await freshSchema.ReadAsync(Ct));
    }
}
