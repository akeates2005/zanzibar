using System.Net;

namespace Poc.IntegrationTests.Api;

public sealed class SmokeTests : ApiTestBase
{
    [Fact]
    public async Task The_api_starts_against_a_real_issuer_and_engine_and_serves_a_user()
    {
        var alice = await AsAsync("alice");

        var me = await (await alice.GetAsync("/api/me")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("alice", me.GetProperty("userId").GetString());

        var customer = await (await alice.GetAsync("/api/customers/acme")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Acme Ltd", customer.GetProperty("name").GetString());
        Assert.True(customer.Permission("admin"));
    }
}
