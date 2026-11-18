namespace Poc.Application.Authorization;

/// <summary>
/// A definition as the engine reports it for the deployed schema (SpiceDB <c>SchemaService/ReflectSchema</c>): its relations
/// with the subjects each allows, and its permission names, both in declaration order.
/// </summary>
public sealed record ReflectedDefinition(string Name, IReadOnlyList<ReflectedRelation> Relations, IReadOnlyList<string> Permissions);

public sealed record ReflectedRelation(string Name, IReadOnlyList<AllowedSubject> AllowedSubjects);

/// <summary>
/// One subject a relation allows: <c>user</c>, <c>group#member</c> (<paramref name="Relation"/> set) or <c>user:*</c>
/// (<paramref name="IsWildcard"/>), optionally only under a caveat.
/// </summary>
public sealed record AllowedSubject(string Type, string? Relation = null, bool IsWildcard = false, string? Caveat = null);
