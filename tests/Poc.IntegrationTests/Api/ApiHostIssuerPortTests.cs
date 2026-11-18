using System.Net;
using System.Net.Sockets;
using Poc.IntegrationTests.Fixtures;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// The stub issuer is started on a port that was free when picked and released before the issuer binds it, so another
/// process can take it in between. This test takes it on purpose.
/// </summary>
public sealed class ApiHostIssuerPortTests
{
    [Fact]
    public async Task A_picked_issuer_port_that_is_taken_before_the_issuer_binds_it_is_retried_on_another()
    {
        var thief = new TcpListener(IPAddress.Loopback, 0);
        thief.Start();
        try
        {
            var taken = ((IPEndPoint)thief.LocalEndpoint).Port;
            var picks = new Queue<int>([taken, NativeSpiceDbHost.FreePort()]);

            var (issuer, _, url) = await ApiHost.StartIssuerAsync(null, picks.Dequeue);
            await using (issuer)
            {
                Assert.Empty(picks);
                Assert.NotEqual($"http://127.0.0.1:{taken}", url);
                using var client = new HttpClient();
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{url}/.well-known/openid-configuration")).StatusCode);
            }
        }
        finally
        {
            thief.Stop();
        }
    }
}
