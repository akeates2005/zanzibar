using Microsoft.Extensions.DependencyInjection;
using Poc.Infrastructure;
using Poc.Application.Projection;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// One whole system per test. When the test ends, outstanding authorisation work is flushed and reconciliation must
/// report no drift: one of the PRD's three permanent fitness functions, asserted after every HTTP test so a behaviour
/// that leaves the database and the engine disagreeing cannot pass.
/// </summary>
public abstract class ApiTestBase : IAsyncLifetime
{
    protected static CancellationToken Ct => CancellationToken.None;

    protected ApiHost Host { get; set; } = null!;

    /// <summary>Tests that deliberately create drift or dead messages set this so cleanup does not assert on them.</summary>
    protected bool ExpectDrift { get; set; }

    public virtual async Task InitializeAsync() => Host = await ApiHost.StartAsync();

    public virtual async Task DisposeAsync()
    {
        try
        {
            if (!ExpectDrift)
            {
                Host.Faults.Down = false;
                Host.Faults.PoisonWrite = null;
                Host.Engine.Resume(); // harmless if it was never paused
                await Host.Services.GetRequiredService<IAuthorizationProjector>().FlushAsync(Ct);
                var report = await Host.Services.GetRequiredService<ReconciliationRunner>().RunAsync(repair: false, Ct);
                Assert.True(report.InSync,
                    $"reconciliation found drift after the test: missing [{string.Join(", ", report.Missing)}] unexpected [{string.Join(", ", report.Unexpected)}]");
            }
        }
        finally
        {
            await Host.DisposeAsync();
        }
    }

    protected static async Task EventuallyAsync(Func<Task<bool>> condition, string failure, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        Assert.Fail(failure);
    }

    protected Task<HttpClient> AsAsync(string user) => Host.ClientAsync(user);
}
