using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;
using Poc.Domain.Directory;

namespace Poc.Application.Access;

/// <summary>
/// The rules a Zanzibar schema cannot express (PRD: Rules the schema cannot express), as pure functions. The cycle
/// rule needs the engine and lives in <see cref="AccessService"/>. Every violation carries a stable code.
/// </summary>
public static class GrantRules
{
    public static class Codes
    {
        public const string UnknownRelation = "unknown_relation";
        public const string InvalidSubject = "invalid_subject";
        public const string GroupOwnerMismatch = "group_owner_mismatch";
        public const string GroupSelfNesting = "group_self_nesting";
        public const string GroupCycle = "group_cycle";
        public const string UnknownSubject = "unknown_subject";
    }

    /// <summary>A grant must name a relation that exists and is assignable on that object type.</summary>
    public static void RequireGrantableRelation(string resourceType, string relation)
    {
        if (!GrantableRelations.IsGrantable(resourceType, relation))
            throw new RuleViolationException(Codes.UnknownRelation,
                $"'{relation}' is not a grantable relation on {resourceType}. Allowed: {string.Join(", ", GrantableRelations.For(resourceType))}.");
    }

    /// <summary>The relations accept a user or a group's members, and nothing else.</summary>
    public static void RequireValidSubjectShape(SubjectRef subject)
    {
        var isUser = subject is { Type: UserSchema.Type, Relation: null };
        var isGroupMembers = subject is { Type: GroupSchema.Type, Relation: GroupSchema.Relations.Member };
        if (!isUser && !isGroupMembers)
            throw new RuleViolationException(Codes.InvalidSubject,
                "The subject must be a user, or the members of a group (group#member).");
    }

    /// <summary>
    /// Rule 1: a customer's group may be granted only on objects under that customer. Platform groups pass here; the
    /// caller separately requires a platform administrator for them.
    /// </summary>
    public static void RequireGroupInScope(Group group, string? objectRootCustomerId)
    {
        if (group.OwnerType == GroupOwnerType.Platform) return;
        if (objectRootCustomerId is null || objectRootCustomerId != group.OwnerId)
            throw new RuleViolationException(Codes.GroupOwnerMismatch,
                $"Group '{group.Id}' belongs to customer '{group.OwnerId}' and cannot be granted on an object outside that customer.");
    }

    /// <summary>Rule 2 (same owner), plus the trivial self-nesting case that is also a cycle.</summary>
    public static void RequireNestable(Group container, Group child)
    {
        if (container.Id == child.Id)
            throw new RuleViolationException(Codes.GroupSelfNesting, "A group cannot be nested inside itself.");
        if (container.OwnerType != child.OwnerType || container.OwnerId != child.OwnerId)
            throw new RuleViolationException(Codes.GroupOwnerMismatch,
                "A group can be nested only inside a group with the same owner.");
    }
}
