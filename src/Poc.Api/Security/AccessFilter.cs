using Poc.Application.Common;

namespace Poc.Api.Security;

/// <summary>
/// Enforces every endpoint's declared <see cref="AccessRequirement"/>. An endpoint with no declaration is refused, never
/// allowed: failing to declare access must fail closed.
/// </summary>
public sealed partial class AccessFilter(ResourceAccess access, CurrentUser user, ILogger<AccessFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var ct = http.RequestAborted;

        switch (http.GetEndpoint()?.Metadata.GetMetadata<AccessRequirement>())
        {
            case null:
                LogUndeclared(http.GetEndpoint()?.DisplayName ?? http.Request.Path);
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Endpoint has no access declaration.",
                    detail: "Every endpoint must declare the permission it requires; this one does not, so it is refused.");

            case AccessRequirement.Anonymous:
            case AccessRequirement.Authenticated:
            case AccessRequirement.HandlerChecked:
                _ = user.Actor; // fail with 401 if the caller cannot be identified
                return await next(context).ConfigureAwait(false);

            case AccessRequirement.OnResource requirement:
                var resource = requirement.Locator.Resolve(http.Request);
                if (resource is null) return NotFound();
                switch (await access.EvaluateAsync(user.Actor, requirement.Permission, resource.Value, ct).ConfigureAwait(false))
                {
                    case AccessDecision.NotVisible:
                        return NotFound();
                    case AccessDecision.MissingPermission:
                        return Results.Problem(
                            statusCode: StatusCodes.Status403Forbidden,
                            title: "Forbidden",
                            detail: $"You can see this {resource.Value.Type} but you lack the '{requirement.Permission}' permission on it.",
                            extensions: new Dictionary<string, object?> { ["permission"] = requirement.Permission });
                    default:
                        return await next(context).ConfigureAwait(false);
                }

            default:
                throw new InvalidOperationException("Unknown access requirement.");
        }
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: "No such object, or you may not know that it exists.");

    [LoggerMessage(EventId = 60, Level = LogLevel.Error, Message = "Endpoint {Endpoint} has no access declaration and was refused")]
    private partial void LogUndeclared(string endpoint);
}
