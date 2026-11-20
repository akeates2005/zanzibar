using Microsoft.Extensions.Logging;
using Poc.Application.Authorization;
using Poc.Application.Common;
using Poc.Application.Persistence;
using Poc.Domain.Access;

namespace Poc.Infrastructure;

/// <summary>Grant and revoke at information level. Logs user ids and objects only: never a token or an email address.</summary>
/// <remarks>
/// The tuple and the resource are passed as objects, so the generated logger formats them only when the line is written
/// (CA1873). The text is the same: it is their <c>ToString()</c>.
/// </remarks>
public sealed partial class AccessLog(ILogger<AccessLog> logger) : IAccessLog
{
    public void Applied(Actor actor, GrantAction action, Relationship relationship) =>
        LogApplied(action, relationship, actor.UserId);

    public void Failed(Actor actor, GrantAction action, Relationship relationship, Exception exception) =>
        LogFailed(exception, action, relationship, actor.UserId);

    public void TokenStampFailed(ResourceRef resource, Exception exception) => LogStampFailed(exception, resource);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "{Action} applied: {Relationship} by {Actor}")]
    private partial void LogApplied(GrantAction action, Relationship relationship, string actor);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "{Action} failed: {Relationship} by {Actor}")]
    private partial void LogFailed(Exception exception, GrantAction action, Relationship relationship, string actor);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Could not record the ZedToken on {Resource}; the tuple was written")]
    private partial void LogStampFailed(Exception exception, ResourceRef resource);
}
