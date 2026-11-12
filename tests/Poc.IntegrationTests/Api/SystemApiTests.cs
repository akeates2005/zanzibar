using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Authorization;
using Poc.Infrastructure.Startup;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Api;

/// <summary>Outbox, reconciliation, reset, readiness and health.</summary>
public sealed class SystemApiTests : ApiTestBase
{
    // ---- Who may use the System endpoints --------------------------------------------------------------------------

    [Fact]
    public async Task System_endpoints_are_for_platform_admins_only()
    {
        foreach (var (method, url) in new[] { ("GET", "/api/system/outbox"), ("POST", "/api/system/reconcile"), ("POST", "/api/system/reset") })
        {
            foreach (var (user, status) in new[] { ("sam", HttpStatusCode.Forbidden), ("alice", HttpStatusCode.NotFound), ("frank", HttpStatusCode.NotFound) })
            {
                var client = await AsAsync(user);
                using var response = method == "GET" ? await client.GetAsync(url) : await client.PostJsonAsync(url);
                await response.ExpectProblemAsync(status, permission: status == HttpStatusCode.Forbidden ? "admin" : null);
            }
        }

        Assert.Equal(1, Host.Services.GetRequiredService<RuntimeState>().StartupCount); // none of the refused resets ran
    }

    // ---- Outbox and reconciliation ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_outbox_screen_reports_a_clean_system()
    {
        var status = await (await (await AsAsync("priya")).GetAsync("/api/system/outbox")).ExpectAsync(HttpStatusCode.OK);

        Assert.True(status.GetProperty("ready").GetBoolean());
        Assert.Equal(1, status.GetProperty("startupCount").GetInt32());
        Assert.Equal(0, status.GetProperty("outbox").GetProperty("pending").GetInt32());
        Assert.Equal(0, status.GetProperty("outbox").GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task A_dead_outbox_message_shows_on_the_system_screen_with_its_error()
    {
        ExpectDrift = true; // this test leaves a deliberately dead message behind
        Host.Faults.PoisonWrite = u => u.Any(x => x.Relationship.Resource.Id.Length > 0 && x.Relationship.Resource.Type == "wallet");
        var alice = await AsAsync("alice");
        await (await alice.PostJsonAsync("/api/domains/acme-uk/wallets", new { name = "Poisoned" })).ExpectAsync(HttpStatusCode.Accepted);

        var priya = await AsAsync("priya");
        JsonElement outbox = default;
        for (var i = 0; i < 100; i++)
        {
            outbox = (await (await priya.GetAsync("/api/system/outbox")).JsonAsync()).GetProperty("outbox");
            if (outbox.GetProperty("failed").GetInt32() == 1) break;
            await Task.Delay(100);
        }

        Assert.Equal(1, outbox.GetProperty("failed").GetInt32());
        var failure = Assert.Single(outbox.GetProperty("failures").EnumerateArray());
        Assert.Equal("WalletCreated", failure.GetProperty("type").GetString());
        Assert.Equal(5, failure.GetProperty("attempts").GetInt32());
        Assert.Contains("injected poison", failure.GetProperty("lastError").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconciliation_on_demand_reports_drift_and_repairs_only_when_asked()
    {
        var priya = await AsAsync("priya");
        var clean = await (await priya.PostJsonAsync("/api/system/reconcile")).ExpectAsync(HttpStatusCode.OK);
        Assert.True(clean.GetProperty("inSync").GetBoolean());

        var store = Host.Services.GetRequiredService<IRelationshipStore>();
        await store.DeleteAsync(new RelationshipFilter("wallet", "uk-ops", "parent"), Ct);
        await store.WriteAsync([RelationshipUpdate.Touch(ValidationYaml.ParseTuple("wallet:ie-ops#parent@domain:globex-main"))], Ct);

        var drift = await (await priya.PostJsonAsync("/api/system/reconcile")).ExpectAsync(HttpStatusCode.OK);
        Assert.False(drift.GetProperty("inSync").GetBoolean());
        Assert.Equal(["wallet:uk-ops#parent@domain:acme-uk"], drift.GetProperty("missing").EnumerateArray().Select(m => m.GetString()!).ToArray());
        Assert.Equal(["wallet:ie-ops#parent@domain:globex-main"], drift.GetProperty("unexpected").EnumerateArray().Select(m => m.GetString()!).ToArray());
        Assert.False((await (await priya.PostJsonAsync("/api/system/reconcile")).JsonAsync()).GetProperty("inSync").GetBoolean()); // detecting twice fixes nothing

        var repaired = await (await priya.PostJsonAsync("/api/system/reconcile?repair=true")).ExpectAsync(HttpStatusCode.OK);
        Assert.True(repaired.GetProperty("inSync").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await (await AsAsync("alice")).GetAsync("/api/wallets/uk-ops")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await AsAsync("grace")).GetAsync("/api/wallets/ie-ops")).StatusCode);

        var latest = await (await priya.GetAsync("/api/system/outbox")).JsonAsync();
        Assert.True(latest.GetProperty("reconciliation").GetProperty("inSync").GetBoolean());
    }

    // ---- Reset and readiness ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Reset_rebuilds_both_stores_to_the_seed_scenario()
    {
        var alice = await AsAsync("alice");
        await (await alice.PostJsonAsync("/api/customers/acme/groups", new { name = "Scratch" })).ExpectAsync(HttpStatusCode.Created);
        await (await alice.PutJsonAsync("/api/access/customer/acme/grants", new { relation = "viewer", subject = new { type = "user", id = "frank" } })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await (await AsAsync("frank")).GetAsync("/api/customers/acme")).StatusCode);

        var reset = await (await (await AsAsync("priya")).PostJsonAsync("/api/system/reset")).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal(2, reset.GetProperty("startupCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await (await AsAsync("frank")).GetAsync("/api/customers/acme")).StatusCode);
        Assert.Equal(["finance-ap", "uk-contractors", "uk-it"],
            (await (await alice.GetAsync("/api/customers/acme/groups")).JsonAsync()).EnumerateArray().Select(g => g.GetProperty("id").GetString()!).Order().ToArray());
        var (seed, _) = ValidationYaml.Load();
        var store = Host.Services.GetRequiredService<IRelationshipStore>();
        var actual = new List<Relationship>();
        foreach (var type in new[] { "platform", "group", "customer", "domain", "wallet", "subscription" })
            actual.AddRange(await store.ReadAsync(new RelationshipFilter(type), Ct));
        Assert.Equal(seed.Select(t => t.ToString()).Order(), actual.Select(t => t.ToString()).Order());
    }

    [Fact]
    public async Task While_the_system_is_rebuilding_api_requests_get_503_with_retry_after_and_health_says_not_ready()
    {
        var state = Host.Services.GetRequiredService<RuntimeState>();
        var alice = await AsAsync("alice");
        state.MarkNotReady();
        try
        {
            using var response = await alice.GetAsync("/api/customers/acme");
            await response.ExpectProblemAsync(HttpStatusCode.ServiceUnavailable);
            Assert.Equal("2", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var anonymous = Host.ClientWithToken(null);
            var health = await (await anonymous.GetAsync("/health")).ExpectAsync(HttpStatusCode.ServiceUnavailable);
            Assert.False(health.GetProperty("ready").GetBoolean());
        }
        finally
        {
            state.MarkReady();
        }

        await (await alice.GetAsync("/api/customers/acme")).ExpectAsync(HttpStatusCode.OK);
    }

    // ---- Health ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Health_reports_the_engine_outbox_and_readiness()
    {
        using var anonymous = Host.ClientWithToken(null);
        var health = await (await anonymous.GetAsync("/health")).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal("healthy", health.GetProperty("status").GetString());
        Assert.True(health.GetProperty("ready").GetBoolean());
        Assert.Equal("ok", health.GetProperty("engine").GetString());
        Assert.Equal(0, health.GetProperty("outboxPending").GetInt32());
        Assert.Equal(JsonValueKind.Null, health.GetProperty("outboxLagSeconds").ValueKind);
    }

    [Fact]
    public async Task Health_notices_a_frozen_engine_quickly_and_recovers_when_it_thaws()
    {
        using var anonymous = Host.ClientWithToken(null);
        Host.Engine.Pause();
        var clock = Stopwatch.StartNew();

        var health = await (await anonymous.GetAsync("/health")).ExpectAsync(HttpStatusCode.ServiceUnavailable);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the probe took {clock.Elapsed}");
        Assert.Equal("unhealthy", health.GetProperty("status").GetString());
        Assert.Equal("unreachable", health.GetProperty("engine").GetString());

        Host.Engine.Resume();
        await Host.WaitUntilReadyAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Health_shows_outbox_lag_while_authorisation_is_pending()
    {
        Host.Faults.Down = true;
        await (await (await AsAsync("alice")).PostJsonAsync("/api/domains/acme-uk/wallets", new { name = "Lagging" })).ExpectAsync(HttpStatusCode.Accepted);

        using var anonymous = Host.ClientWithToken(null);
        var health = await (await anonymous.GetAsync("/health")).JsonAsync();

        Assert.Equal(1, health.GetProperty("outboxPending").GetInt32());
        Assert.True(health.GetProperty("outboxLagSeconds").GetDouble() >= 0);
    }

    // ---- The API description --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_openapi_document_describes_every_endpoint_with_unique_operation_ids_and_bearer_security()
    {
        using var anonymous = Host.ClientWithToken(null);
        var document = await (await anonymous.GetAsync("/openapi/v1.json")).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal("bearer", document.GetProperty("components").GetProperty("securitySchemes").EnumerateObject().Single().Name);
        var operations = document.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Select(m => (Path: p.Name, Method: m.Name, Op: m.Value))).ToList();
        // Every route except the description itself must be described, no more and no fewer.
        var routes = Host.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>().SelectMany(s => s.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => $"{m} {e.RoutePattern.RawText}"))
            .Where(r => !r.Contains("openapi", StringComparison.Ordinal)).Order().ToList();
        var described = operations.Select(o => $"{o.Method.ToUpperInvariant()} {o.Path}").Order().ToList();
        Assert.Equal(routes, described);

        var ids = operations.Select(o => o.Op.GetProperty("operationId").GetString()).ToList();
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var expected in new[] { "CreateCustomer", "GetWallet", "MoveWallet", "Grant", "Revoke", "ExplainAccess", "Reconcile", "GetTree" })
            Assert.Contains(expected, ids);
        Assert.Contains(operations, o => o.Path == "/api/customers/{id}" && o.Method == "get");
    }
}
