using Poc.Domain.Common;

namespace Poc.Domain.Access;

public enum GrantAction
{
    Grant,
    Revoke,
}

public enum GrantStatus
{
    Pending,
    Applied,
    Failed,
}

/// <summary>
/// One row per grant or revoke attempt. Written as Pending before the tuple write and finished as Applied or Failed,
/// so a crash between the two is visible rather than silent. The engine, not this table, is the source of truth.
/// </summary>
public sealed class GrantAuditEntry
{
    private GrantAuditEntry()
    {
    }

    public long Id { get; private set; }

    public GrantAction Action { get; private set; }

    public string ResourceType { get; private set; } = "";

    public string ResourceId { get; private set; } = "";

    public string Relation { get; private set; } = "";

    public string SubjectType { get; private set; } = "";

    public string SubjectId { get; private set; } = "";

    /// <summary><c>member</c> for a group subject (<c>group:x#member</c>), otherwise <c>null</c>.</summary>
    public string? SubjectRelation { get; private set; }

    public string ActorUserId { get; private set; } = "";

    public DateTimeOffset At { get; private set; }

    public GrantStatus Status { get; private set; }

    public string? ZedToken { get; private set; }

    public static GrantAuditEntry Pending(
        GrantAction action, string resourceType, string resourceId, string relation,
        string subjectType, string subjectId, string? subjectRelation, string actorUserId, DateTimeOffset at) => new()
        {
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Relation = relation,
            SubjectType = subjectType,
            SubjectId = subjectId,
            SubjectRelation = subjectRelation,
            ActorUserId = actorUserId,
            At = at,
            Status = GrantStatus.Pending,
        };

    public void MarkApplied(string zedToken)
    {
        Status = GrantStatus.Applied;
        ZedToken = zedToken;
    }

    public void MarkFailed() => Status = GrantStatus.Failed;
}
