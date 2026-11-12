using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Poc.Application.Authorization;
using Poc.Infrastructure.Startup;
using Poc.IntegrationTests.Api;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Resilience;

/// <summary>
/// SpiceDB's memory datastore loses everything if its container restarts. The API must notice, refuse to serve in the
/// meantime (never a misleading answer), rebuild both stores, and say so in its log. Here the engine process is really
/// restarted on the same port.
/// </summary>
public sealed class SchemaLossRecoveryTests : ApiTestBase
{
    private async Task<HttpStatusCode> StatusAsync(string user, string url)
    {
        using var response = await (await AsAsync(user)).GetAsync(url);
        return response.StatusCode;
    }

    private RuntimeState State => Host.Services.GetRequiredService<RuntimeState>();

    [Fact]
    public async Task A_restarted_engine_is_noticed_and_both_stores_are_rebuilt_without_anyone_asking()
    {
        // A change made before the restart: it lives only in the stores that are about to be wiped.
        var alice = await AsAsync("alice");
        await (await alice.PostJsonAsync("/api/customers/acme/groups", new { name = "Scratch" })).ExpectAsync(HttpStatusCode.Created);

        await Host.RestartEngineAsync();
        await EventuallyAsync(async () => await StatusAsync("alice", "/api/customers/acme") == HttpStatusCode.OK, "the API never recovered by itself");

        Assert.Equal(2, State.StartupCount);
        Assert.True(State.IsReady);
        var groups = await (await alice.GetAsync("/api/customers/acme/groups")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["finance-ap", "uk-contractors", "uk-it"], groups.EnumerateArray().Select(g => g.GetProperty("id").GetString()!).Order().ToArray()); // Scratch is gone: the PRD accepts the loss

        var (seed, _) = ValidationYaml.Load();
        var store = Host.Services.GetRequiredService<IRelationshipStore>();
        var actual = new List<Relationship>();
        foreach (var type in new[] { "platform", "group", "customer", "domain", "wallet", "subscription" })
            actual.AddRange(await store.ReadAsync(new RelationshipFilter(type), Ct));
        Assert.Equal(seed.Select(t => t.ToString()).Order(), actual.Select(t => t.ToString()).Order());
    }

    [Fact]
    public async Task Until_it_has_recovered_every_answer_is_a_503_never_a_wrong_deny_or_a_stale_allow()
    {
        await Host.RestartEngineAsync();
        var seen = new HashSet<HttpStatusCode>();

        // Hammer the API through the outage and the recovery. Each of these is something Alice may see and Frank may not.
        var recovered = false;
        for (var i = 0; i < 600 && !recovered; i++)
        {
            foreach (var url in new[] { "/api/customers/acme", "/api/wallets/uk-ops", "/api/tree" })
            {
                var status = await StatusAsync("alice", url);
                seen.Add(status);
                if (status == HttpStatusCode.OK && url == "/api/customers/acme") recovered = true; // latch: later URLs must not unset it
            }
            var frank = await StatusAsync("frank", "/api/customers/acme");
            Assert.True(frank is HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotFound, $"Frank got {frank}"); // only ever refused: 503 while down, 404 after, never 200
            if (!recovered) await Task.Delay(25);
        }

        Assert.True(recovered, "the API never recovered");
        Assert.Subset(new HashSet<HttpStatusCode> { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable }, seen);
    }

    [Fact]
    public async Task The_reset_and_the_recovery_are_logged_so_an_operator_can_tell_what_happened()
    {
        await Host.RestartEngineAsync();
        await EventuallyAsync(async () => await StatusAsync("alice", "/api/customers/acme") == HttpStatusCode.OK, "the API never recovered");

        var logs = Host.Logs.Entries;
        Assert.Contains(logs, l => l.Level == LogLevel.Warning && l.Message.Contains("lost its schema", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Level == LogLevel.Information && l.Message.Contains("Recovered from schema loss", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deliberate_system_reset_is_not_mistaken_for_schema_loss()
    {
        await (await (await AsAsync("priya")).PostJsonAsync("/api/system/reset")).ExpectAsync(HttpStatusCode.OK);
        await Task.Delay(1500); // many watchdog ticks

        Assert.Equal(2, State.StartupCount); // one reset, no extra rebuild
        Assert.DoesNotContain(Host.Logs.Entries, l => l.Message.Contains("lost its schema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rebuild_that_fails_is_retried_until_it_succeeds()
    {
        Host.Faults.Down = true; // the rebuild's tuple wipe will fail
        await Host.RestartEngineAsync();

        await EventuallyAsync(
            () => Task.FromResult(Host.Logs.Entries.Count(l => l.Message.Contains("Recovery from schema loss failed", StringComparison.Ordinal)) >= 2),
            "the rebuild was not retried");
        using var anonymous = Host.ClientWithToken(null);
        var health = await (await anonymous.GetAsync("/health")).ExpectAsync(HttpStatusCode.ServiceUnavailable);
        Assert.False(health.GetProperty("ready").GetBoolean()); // and it says so while it is failing
        await (await (await AsAsync("alice")).GetAsync("/api/customers/acme")).ExpectProblemAsync(HttpStatusCode.ServiceUnavailable);

        Host.Faults.Down = false;
        await EventuallyAsync(async () => await StatusAsync("alice", "/api/customers/acme") == HttpStatusCode.OK, "the rebuild never succeeded once the fault cleared");
    }
}

/// <summary>With the watchdog effectively off, recovery is started by the health probe alone, as the PRD describes.</summary>
public sealed class SchemaLossHealthProbeTests : ApiTestBase
{
    public override async Task InitializeAsync() =>
        Host = await ApiHost.StartAsync(settings => settings["Poc:SchemaCheckInterval"] = "01:00:00");

    [Fact]
    public async Task The_health_probe_reports_a_lost_schema_and_starts_the_rebuild()
    {
        using var anonymous = Host.ClientWithToken(null);
        await Host.RestartEngineAsync();

        var health = await (await anonymous.GetAsync("/health")).ExpectAsync(HttpStatusCode.ServiceUnavailable);
        Assert.Equal("schema-missing", health.GetProperty("engine").GetString());
        Assert.Equal("unhealthy", health.GetProperty("status").GetString());

        await EventuallyAsync(async () => (await anonymous.GetAsync("/health")).StatusCode == HttpStatusCode.OK, "the probe-started rebuild never finished");
        Assert.Equal(2, Host.Services.GetRequiredService<RuntimeState>().StartupCount);
        await (await (await AsAsync("alice")).GetAsync("/api/customers/acme")).ExpectAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Nothing_is_recovered_until_something_probes_and_meanwhile_the_engines_own_errors_keep_everything_closed()
    {
        await Host.RestartEngineAsync();
        await Task.Delay(1000);

        Assert.Equal(1, Host.Services.GetRequiredService<RuntimeState>().StartupCount); // nobody has probed yet
        // The stale "ready" API still never answers from an engine that has no schema.
        await (await (await AsAsync("alice")).GetAsync("/api/customers/acme")).ExpectProblemAsync(HttpStatusCode.ServiceUnavailable);

        using var anonymous = Host.ClientWithToken(null);
        _ = await anonymous.GetAsync("/health"); // the probe is what starts recovery
        await EventuallyAsync(async () => (await anonymous.GetAsync("/health")).StatusCode == HttpStatusCode.OK, "never recovered");
    }
}
