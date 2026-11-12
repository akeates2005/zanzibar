namespace Poc.Application.Authorization;

/// <summary>A stored tuple: <c>resource#relation@subject</c>.</summary>
public sealed record Relationship(ResourceRef Resource, string Relation, SubjectRef Subject)
{
    public override string ToString() => $"{Resource}#{Relation}@{Subject}";
}

/// <summary>Touch is the engine's upsert, so replaying it is harmless. Delete is idempotent.</summary>
public enum RelationshipOperation
{
    Touch,
    Delete,
}

public sealed record RelationshipUpdate(RelationshipOperation Operation, Relationship Relationship)
{
    public static RelationshipUpdate Touch(Relationship relationship) => new(RelationshipOperation.Touch, relationship);

    public static RelationshipUpdate Delete(Relationship relationship) => new(RelationshipOperation.Delete, relationship);
}

/// <summary>Matches relationships whose subject has the given type and optionally id and relation.</summary>
public sealed record SubjectFilter(string SubjectType, string? SubjectId = null, string? SubjectRelation = null);

/// <summary>
/// Selects relationships to read or delete. <see cref="ResourceType"/> is mandatory so that an unconstrained
/// filter can never be built by accident.
/// </summary>
public sealed record RelationshipFilter(
    string ResourceType,
    string? ResourceId = null,
    string? Relation = null,
    SubjectFilter? Subject = null)
{
    public string ResourceType { get; } = string.IsNullOrWhiteSpace(ResourceType)
        ? throw new ArgumentException("A relationship filter must name a resource type.", nameof(ResourceType))
        : ResourceType;
}
