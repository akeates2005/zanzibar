using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Infrastructure.SpiceDb;
using Poc.IntegrationTests.Fixtures;

namespace Poc.IntegrationTests.Adapter;

/// <summary>
/// PRD rule 5: there is no code path that allows access when the engine is unavailable or errors.
/// Every operation must throw <see cref="AuthorizationUnavailableException"/> in every failure mode.
/// </summary>
[Collection(SpiceDbCollectionDefinition.Name)]
public sealed class FailureTests(SpiceDbFixture fx)
{
    private static CancellationToken Ct => CancellationToken.None;
    private static readonly SubjectRef Alice = SubjectRef.User("alice");
    private static readonly ResourceRef Wallet = new(WalletSchema.Type, "uk-ops");

    /// <summary>A connection to a port nothing is listening on.</summary>
    public static SpiceDbConnection DeadConnection() => new(new SpiceDbOptions
    {
        Endpoint = $"http://127.0.0.1:{NativeSpiceDbHost.FreePort()}",
        PresharedKey = "irrelevant",
        AllowInsecureTransport = true,
        Deadline = TimeSpan.FromSeconds(3),
    });

    public static TheoryData<string> Operations =>
        new() { "check", "checkMany", "lookupResources", "lookupSubjects", "write", "read", "delete", "schemaWrite", "schemaRead" };

    private static Task Run(string operation, SpiceDbConnection c)
    {
        var authorizer = new SpiceDbAuthorizer(c, NullLogger<SpiceDbAuthorizer>.Instance);
        var store = new SpiceDbRelationshipStore(c);
        var schema = new SpiceDbSchemaStore(c);
        var tuple = new Relationship(Wallet, WalletSchema.Relations.Viewer, Alice);
        return operation switch
        {
            "check" => authorizer.CheckAsync(Alice, WalletSchema.Permissions.View, Wallet, Ct),
            "checkMany" => authorizer.CheckManyAsync(Alice, Wallet, [WalletSchema.Permissions.View], Ct),
            "lookupResources" => authorizer.LookupResourcesAsync(Alice, WalletSchema.Type, WalletSchema.Permissions.View, Ct),
            "lookupSubjects" => authorizer.LookupSubjectsAsync(Wallet, WalletSchema.Permissions.View, UserSchema.Type, Ct),
            "write" => store.WriteAsync([RelationshipUpdate.Touch(tuple)], Ct),
            "read" => store.ReadAsync(new RelationshipFilter(WalletSchema.Type), Ct),
            "delete" => store.DeleteAsync(new RelationshipFilter(WalletSchema.Type, Wallet.Id), Ct),
            "schemaWrite" => schema.WriteAsync("definition user {}", Ct),
            "schemaRead" => schema.ReadAsync(Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task An_unreachable_engine_throws_a_typed_exception(string operation)
    {
        using var dead = DeadConnection();
        var ex = await Assert.ThrowsAsync<AuthorizationUnavailableException>(() => Run(operation, dead));
        Assert.IsType<RpcException>(ex.InnerException);
        Assert.True(ex.IsTransient, "an unreachable engine is worth retrying");
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task A_wrong_preshared_key_throws_a_typed_exception(string operation)
    {
        using var wrongKey = SpiceDbFixture.NewConnection(fx.Host, o => o.PresharedKey = "not-the-key");
        var ex = await Assert.ThrowsAsync<AuthorizationUnavailableException>(() => Run(operation, wrongKey));
        // SpiceDB answers a bad preshared key with PermissionDenied (observed on v1.57.1), not Unauthenticated.
        Assert.Equal(StatusCode.PermissionDenied, ((RpcException)ex.InnerException!).StatusCode);
        Assert.False(ex.IsTransient, "a rejected key will not be fixed by retrying");
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task An_engine_that_never_answers_hits_the_deadline_and_throws(string operation)
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var accepted = new List<TcpClient>();
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true) accepted.Add(await silent.AcceptTcpClientAsync());
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // listener stopped
            }
        });
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        using var connection = new SpiceDbConnection(new SpiceDbOptions
        {
            Endpoint = $"http://127.0.0.1:{port}",
            PresharedKey = "irrelevant",
            AllowInsecureTransport = true,
            Deadline = TimeSpan.FromMilliseconds(400),
        });

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AuthorizationUnavailableException>(() => Run(operation, connection));
        clock.Stop();

        Assert.Equal(StatusCode.DeadlineExceeded, ((RpcException)ex.InnerException!).StatusCode);
        Assert.True(ex.IsTransient, "a timeout is worth retrying");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"took {clock.Elapsed}");
        silent.Stop();
        await acceptLoop;
        foreach (var c in accepted) c.Dispose();
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task A_cancelled_caller_gets_a_cancellation_not_an_authorisation_failure(string operation)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var authorizer = new SpiceDbAuthorizer(fx.Connection, NullLogger<SpiceDbAuthorizer>.Instance);
        var store = new SpiceDbRelationshipStore(fx.Connection);
        var schema = new SpiceDbSchemaStore(fx.Connection);
        Task call = operation switch
        {
            "check" => authorizer.CheckAsync(Alice, WalletSchema.Permissions.View, Wallet, cts.Token),
            "checkMany" => authorizer.CheckManyAsync(Alice, Wallet, [WalletSchema.Permissions.View], cts.Token),
            "lookupResources" => authorizer.LookupResourcesAsync(Alice, WalletSchema.Type, WalletSchema.Permissions.View, cts.Token),
            "lookupSubjects" => authorizer.LookupSubjectsAsync(Wallet, WalletSchema.Permissions.View, UserSchema.Type, cts.Token),
            "write" => store.WriteAsync([RelationshipUpdate.Touch(new Relationship(Wallet, WalletSchema.Relations.Viewer, Alice))], cts.Token),
            "read" => store.ReadAsync(new RelationshipFilter(WalletSchema.Type), cts.Token),
            "delete" => store.DeleteAsync(new RelationshipFilter(WalletSchema.Type, Wallet.Id), cts.Token),
            "schemaWrite" => schema.WriteAsync("definition user {}", cts.Token),
            _ => schema.ReadAsync(cts.Token),
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public void A_plain_http_endpoint_is_refused_unless_explicitly_allowed()
    {
        Assert.Throws<ArgumentException>(() => new SpiceDbConnection(
            new SpiceDbOptions { Endpoint = "http://localhost:50051", PresharedKey = "k" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_preshared_key_is_refused(string key)
    {
        Assert.Throws<ArgumentException>(() => new SpiceDbConnection(
            new SpiceDbOptions { Endpoint = "https://localhost:50051", PresharedKey = key }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void A_lookup_page_size_outside_the_engine_cap_is_refused(int pageSize)
    {
        Assert.Throws<ArgumentException>(() => new SpiceDbConnection(new SpiceDbOptions
        {
            Endpoint = "https://localhost:50051",
            PresharedKey = "k",
            LookupPageSize = pageSize,
        }));
    }
}
