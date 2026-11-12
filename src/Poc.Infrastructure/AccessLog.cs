using Microsoft.Extensions.Logging;
using Poc.Application.Authorization;
using Poc.Application.Common;
using Poc.Application.Persistence;
using Poc.Domain.Access;

namespace Poc.Infrastructure;

/// <summary>Grant and revoke at information level. Logs user ids and objects only: never a token or an email address.</summary>
public sealed partial class AccessLog(ILogger<AccessLog> logger) : IAccessLog
{
    public void Applied(Actor actor, GrantAction action, Relationship relationship) =>
        LogApplied(action, relationship.ToString(), actor.UserId);

    public void Failed(Actor actor, GrantAction action, Relationship relationship, Exception exception) =>
        LogFailed(exception, action, relationship.ToString(), actor.UserId);

    public void TokenStampFailed(ResourceRef resource, Exception exception) => LogStampFailed(exception, resource.ToString());

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "{Action} applied: {Relationship} by {Actor}")]
    private partial void LogApplied(GrantAction action, string relationship, string actor);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "{Action} failed: {Relationship} by {Actor}")]
    private partial void LogFailed(Exception exception, GrantAction action, string relationship, string actor);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Could not record the ZedToken on {Resource}; the tuple was written")]
    private partial void LogStampFailed(Exception exception, string resource);
}
