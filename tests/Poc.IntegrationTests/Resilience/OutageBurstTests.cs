using System.Net;
using Poc.IntegrationTests.Api;

namespace Poc.IntegrationTests.Resilience;

/// <summary>Many people acting at once while authorisation cannot land: nothing is lost, nothing is applied twice.</summary>
public sealed class OutageBurstTests : ApiTestBase
{
    [Fact]
    public async Task A_burst_of_concurrent_creates_during_an_outage_all_become_visible_exactly_once_on_recovery()
    {
        var alice = await AsAsync("alice");
        Host.Faults.Down = true; // checks still work; the tuple writes after them fail

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            using var response = await alice.PostJsonAsync("/api/domains/acme-ie/wallets", new { name = $"Burst {i}" });
            return (response.StatusCode, Body: await response.JsonAsync());
        }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.All(responses, r => Assert.True(r.Body.GetProperty("authorisationPending").GetBoolean()));
        var ids = responses.Select(r => r.Body.GetProperty("id").GetString()!).ToList();
        Assert.Equal(20, ids.Distinct().Count());
        await (await alice.GetAsync($"/api/wallets/{ids[0]}")).ExpectProblemAsync(HttpStatusCode.NotFound); // saved but invisible

        Host.Faults.Down = false;
        await EventuallyAsync(async () =>
        {
            foreach (var id in ids)
            {
                using var response = await alice.GetAsync($"/api/wallets/{id}");
                if (response.StatusCode != HttpStatusCode.OK) return false;
            }

            return true;
        }, "not every wallet became visible once the engine returned");

        var children = await (await alice.GetAsync("/api/domain/acme-ie/children")).ExpectAsync(HttpStatusCode.OK);
        var burst = children.EnumerateArray().Select(c => c.GetProperty("name").GetString()!).Where(n => n.StartsWith("Burst ", StringComparison.Ordinal)).ToList();
        Assert.Equal(20, burst.Count);
        Assert.Equal(20, burst.Distinct().Count()); // none doubled
    }

    [Fact]
    public async Task Spenders_creating_subscriptions_during_an_outage_each_get_their_operator_grant_once_it_ends()
    {
        var carol = await AsAsync("carol");
        Host.Faults.Down = true;

        var created = await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
        {
            using var response = await carol.PostJsonAsync("/api/wallets/uk-ops/subscriptions", new { productName = $"Tool {i}", quantity = 1 });
            return (await response.ExpectAsync(HttpStatusCode.Accepted)).GetProperty("id").GetString()!;
        }));

        Host.Faults.Down = false;
        await EventuallyAsync(async () =>
        {
            foreach (var id in created)
            {
                using var response = await carol.PostJsonAsync($"/api/subscriptions/{id}/quantity", new { quantity = 2 });
                if (response.StatusCode != HttpStatusCode.OK) return false;
            }

            return true;
        }, "Carol never became operator of every subscription she created");

        var audit = await (await (await AsAsync("alice")).GetAsync("/api/access/audit?type=subscription&id=" + created[0])).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(1, audit.GetArrayLength()); // exactly one default grant, not one per flush attempt
    }
}
