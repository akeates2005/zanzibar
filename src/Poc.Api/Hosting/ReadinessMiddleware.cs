using Poc.Infrastructure.Startup;

namespace Poc.Api.Hosting;

/// <summary>
/// While start-up (or a System reset) is rebuilding both stores, every <c>/api</c> request is refused with 503 instead of
/// reading a half-built database. <c>/health</c> and the API description stay reachable.
/// </summary>
public sealed class ReadinessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RuntimeState state, IProblemDetailsService problems)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!state.IsReady && context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "2";
            await problems.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Starting",
                    Detail = "The system is rebuilding its data and is not ready yet.",
                },
            }).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
