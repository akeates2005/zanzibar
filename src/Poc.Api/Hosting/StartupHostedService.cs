using Poc.Infrastructure.Startup;

namespace Poc.Api.Hosting;

/// <summary>
/// Runs the start-up sequence once the host is up, retrying until it succeeds, so the API can come up before SpiceDB
/// does. Until it completes <c>/health</c> reports not ready and <c>/api</c> answers 503.
/// </summary>
public sealed partial class StartupHostedService(StartupSequence startup, ILogger<StartupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await startup.RunAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogStartupFailed(ex);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(EventId = 70, Level = LogLevel.Error, Message = "Start-up sequence failed; will retry in 5 seconds")]
    private partial void LogStartupFailed(Exception exception);
}
