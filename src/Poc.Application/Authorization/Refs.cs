namespace Poc.Application.Authorization;

/// <summary>An object in the authorisation graph, e.g. <c>wallet:uk-ops</c>.</summary>
public readonly record struct ResourceRef(string Type, string Id)
{
    public override string ToString() => $"{Type}:{Id}";
}

/// <summary>
/// A subject: an object, optionally with a relation, e.g. <c>user:alice</c> or <c>group:uk-it#member</c>.
/// </summary>
public readonly record struct SubjectRef(string Type, string Id, string? Relation = null)
{
    public static SubjectRef User(string id) => new(Schema.UserSchema.Type, id);

    public static SubjectRef GroupMembers(string groupId) =>
        new(Schema.GroupSchema.Type, groupId, Schema.GroupSchema.Relations.Member);

    public override string ToString() => Relation is null ? $"{Type}:{Id}" : $"{Type}:{Id}#{Relation}";
}
