using System.Net;
using System.Net.Sockets;

namespace Poc.IntegrationTests.Fixtures;

/// <summary>
/// The harness that starts a real engine for every test. A free port is found, released, then bound by the engine, so
/// another process can take it in between; these tests take it on purpose.
/// </summary>
public sealed class NativeSpiceDbHostTests
{
    [Fact]
    public async Task A_chosen_port_that_is_taken_before_the_engine_binds_it_is_retried_on_another()
    {
        var thief = new TcpListener(IPAddress.Loopback, 0);
        thief.Start();
        try
        {
            var taken = ((IPEndPoint)thief.LocalEndpoint).Port;
            var picks = new Queue<int>([taken, NativeSpiceDbHost.FreePort()]);

            await using var host = await NativeSpiceDbHost.StartAsync(null, null, picks.Dequeue);

            Assert.Empty(picks);
            Assert.NotEqual($"http://127.0.0.1:{taken}", host.Endpoint);
        }
        finally
        {
            thief.Stop();
        }
    }

    [Fact]
    public async Task A_fixed_port_that_is_taken_fails_rather_than_moving_or_trusting_whoever_holds_it()
    {
        // The thief accepts connections, so a readiness check that only connects would wrongly report this engine ready.
        var thief = new TcpListener(IPAddress.Loopback, 0);
        thief.Start();
        try
        {
            var taken = ((IPEndPoint)thief.LocalEndpoint).Port;

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeSpiceDbHost.StartAsync(taken));

            Assert.Contains("address already in use", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            thief.Stop();
        }
    }
}
