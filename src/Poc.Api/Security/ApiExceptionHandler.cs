using Microsoft.AspNetCore.Diagnostics;
using Poc.Application.Authorization;
using Poc.Application.Common;
using Poc.Domain.Common;

namespace Poc.Api.Security;

/// <summary>Turns application exceptions into RFC 9457 problem details. A failure of the engine is always 503, never a pass.</summary>
public sealed partial class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499; // the client went away; nothing to report
            return true;
        }

        var (status, title, detail, extensions) = exception switch
        {
            NotFoundException => (404, "Not found", "No such object, or you may not know that it exists.", null),
            PermissionDeniedException e => (403, "Forbidden", $"You lack the '{e.Permission}' permission.", new Dictionary<string, object?> { ["permission"] = e.Permission }),
            ConflictException e => (409, "Conflict", e.Message, null),
            RuleViolationException e => (422, "Request refused", e.Message, new Dictionary<string, object?> { ["code"] = e.Code }),
            DomainRuleException e => (422, "Request refused", e.Message, new Dictionary<string, object?> { ["code"] = "domain_rule" }),
            AuthorizationUnavailableException => (503, "Authorisation service unavailable", "The authorisation service could not answer, so the request was refused. Try again shortly.", null),
            UnauthorizedAccessException => (401, "Unauthorized", "A valid access token with a user id is required.", null),
            BadHttpRequestException e => (400, "Bad request", e.Message, null),
            _ => (500, "Unexpected error", "Something went wrong. Quote the trace id when reporting it.", null),
        };

        if (status == 503) LogUnavailable(exception);
        else if (status == 500) LogUnexpected(exception);

        httpContext.Response.StatusCode = status;
        if (status == 503) httpContext.Response.Headers.RetryAfter = "5";
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = null, // never echo exception details to the caller
            ProblemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = status, Title = title, Detail = detail, Extensions = extensions ?? [] },
        }).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 61, Level = LogLevel.Warning, Message = "Request refused because the authorisation service is unavailable")]
    private partial void LogUnavailable(Exception exception);

    [LoggerMessage(EventId = 62, Level = LogLevel.Error, Message = "Unhandled exception")]
    private partial void LogUnexpected(Exception exception);
}
