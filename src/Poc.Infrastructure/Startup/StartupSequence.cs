using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Infrastructure.Persistence;
using Poc.Infrastructure.Projection;
using Poc.Infrastructure.Seeding;

namespace Poc.Infrastructure.Startup;

/// <summary>
/// Both stores are in memory, so they are always rebuilt together (PRD: Start-up and reconciliation): wait for the
/// engine, write the schema, delete every relationship, empty and reseed the database, flush the outbox, then issue
/// the seed grants through the real commands. Running it again is a full reset.
/// </summary>
public sealed partial class StartupSequence(
    PocOptions options,
    PocDatabase database,
    IDbContextFactory<PocDbContext> factory,
    ISchemaStore schema,
    IRelationshipStore tuples,
    OutboxProjector projector,
    IServiceScopeFactory scopes,
    RuntimeState state,
    ILogger<StartupSequence> logger) : IDisposable
{
    private static readonly string[] ObjectTypes =
    [
        PlatformSchema.Type, GroupSchema.Type, CustomerSchema.Type, DomainSchema.Type, WalletSchema.Type, SubscriptionSchema.Type,
    ];

    private readonly SemaphoreSlim _running = new(1, 1);

    public void Dispose() => _running.Dispose();

    public async Task RunAsync(CancellationToken ct)
    {
        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            state.MarkNotReady();
            LogStarting();

            await WaitForEngineAsync(ct).ConfigureAwait(false);
            await database.EnsureCreatedAsync(factory, ct).ConfigureAwait(false);
            await schema.WriteAsync(await File.ReadAllTextAsync(options.SchemaPath, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

            await projector.RunExclusiveAsync(async () =>
            {
                foreach (var type in ObjectTypes)
                    await tuples.DeleteAsync(new RelationshipFilter(type), ct).ConfigureAwait(false);
                await PocDatabase.ClearAsync(factory, ct).ConfigureAwait(false);

                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<Seeder>().SeedDatabaseAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            var outcome = await projector.FlushAsync(ct).ConfigureAwait(false);
            if (!outcome.FullyApplied)
                throw new InvalidOperationException(
                    $"Start-up could not apply {outcome.Remaining} structural message(s) to the engine (engine unavailable: {outcome.EngineUnavailable}).");

            await using (var scope = scopes.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<Seeder>().SeedGrantsAsync(ct).ConfigureAwait(false);

            state.MarkReady();
            LogReady(state.StartupCount);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task WaitForEngineAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + options.EngineStartupTimeout;
        while (true)
        {
            try
            {
                await schema.ReadAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (AuthorizationUnavailableException ex) when (ex.IsTransient && DateTime.UtcNow < deadline)
            {
                LogWaitingForEngine(ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }
            catch (AuthorizationUnavailableException ex) when (ex.IsTransient)
            {
                throw new TimeoutException($"SpiceDB did not become available within {options.EngineStartupTimeout}.", ex);
            }
        }
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "Start-up sequence beginning: rebuilding SpiceDB and the application database")]
    private partial void LogStarting();

    [LoggerMessage(EventId = 31, Level = LogLevel.Information, Message = "Start-up sequence complete (run {Run}); the system is ready")]
    private partial void LogReady(int run);

    [LoggerMessage(EventId = 32, Level = LogLevel.Warning, Message = "Waiting for SpiceDB: {Reason}")]
    private partial void LogWaitingForEngine(string reason);
}
