using Poc.Api.Contracts;
using Poc.Api.Security;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Projection;
using Poc.Infrastructure;
using Poc.Infrastructure.Startup;

namespace Poc.Api.Endpoints;

/// <summary>Outbox, reconciliation and reset (platform administrators), plus the open health probe.</summary>
public static class SystemEndpoints
{
    public static void MapHealth(WebApplication app)
    {
        app.MapGet("/health", async (RuntimeState state, SchemaRecovery recovery, IOutboxInspector outbox, CancellationToken ct) =>
        {
            // The probe reads the schema each time (PRD): a memory-datastore restart loses it, and the probe both reports that
            // and starts the rebuild.
            var engine = (await recovery.ProbeAsync(ct)) switch
            {
                EngineStatus.Ok => "ok",
                EngineStatus.SchemaMissing => "schema-missing",
                _ => "unreachable",
            };

            OutboxStatus? status = null;
            try
            {
                status = state.IsReady ? await outbox.GetStatusAsync(ct) : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the application database is part of readiness; an error here shows as unhealthy below
            }

            var healthy = state.IsReady && engine == "ok" && status is not null;
            var lag = status?.OldestPendingAt is { } oldest ? (DateTimeOffset.UtcNow - oldest).TotalSeconds : (double?)null;
            return Results.Json(
                new HealthResponse(healthy ? "healthy" : "unhealthy", state.IsReady, engine, status?.Pending ?? 0, status?.Failed ?? 0, lag),
                statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).WithName("Health").WithTags("System").AllowAnonymous()
          .Requires(new AccessRequirement.Anonymous("Liveness, engine reachability and outbox lag; no data."))
          .Produces<HealthResponse>().Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    public static void Map(RouteGroupBuilder api)
    {
        var platform = ResourceLocator.Platform;

        api.MapGet("/system/outbox", async (IOutboxInspector outbox, ReconciliationRunner reconciliation, RuntimeState state, CancellationToken ct) =>
            Results.Ok(new OutboxDto(await outbox.GetStatusAsync(ct), reconciliation.Latest is { } r ? ToDto(r) : null, state.IsReady, state.StartupCount)))
            .WithName("GetOutbox").WithTags("System")
            .RequiresOn(PlatformSchema.Permissions.Admin, platform).Produces<OutboxDto>();

        api.MapPost("/system/reconcile", async (bool? repair, ReconciliationRunner reconciliation, CancellationToken ct) =>
            Results.Ok(ToDto(await reconciliation.RunAsync(repair ?? false, ct))))
            .WithName("Reconcile").WithTags("System")
            .RequiresOn(PlatformSchema.Permissions.Admin, platform).Produces<ReconciliationDto>();

        api.MapPost("/system/reset", async (StartupSequence startup, RuntimeState state, CancellationToken ct) =>
        {
            await startup.RunAsync(ct);
            return Results.Ok(new { startupCount = state.StartupCount });
        }).WithName("Reset").WithTags("System").RequiresOn(PlatformSchema.Permissions.Admin, platform);
    }

    private static ReconciliationDto ToDto(Poc.Application.Reconciliation.ReconciliationReport r) => new(
        r.At, r.InSync, r.Missing.Select(t => t.ToString()).ToList(), r.Unexpected.Select(t => t.ToString()).ToList(), r.SkippedPending);
}
