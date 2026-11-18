using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;

namespace Poc.Application.Access;

/// <summary>
/// Which object types a request may name and which relations an administrator may assign on each, derived from the deployed
/// schema rather than written by hand (DECISIONS.md, "Access model read from the engine"):
/// <list type="bullet">
/// <item>A <b>resource type</b> is <c>platform</c>, or a definition with a <c>parent</c> relation and a <c>manage_access</c>
/// permission. <c>group</c> and <c>user</c> are not: groups are managed by their own commands.</item>
/// <item>A <b>grantable relation</b> is a relation of a resource type whose every allowed subject is a person: <c>user</c> or
/// <c>group#member</c>, with no wildcard and no caveat. A relation that points at an object (<c>parent</c>) is structural.</item>
/// </list>
/// Immutable; <see cref="AccessModelSource"/> holds the current one.
/// </summary>
public sealed class AccessModel
{
    private const string ParentRelation = CustomerSchema.Relations.Parent;
    private const string ManageAccessPermission = CustomerSchema.Permissions.ManageAccess;

    private readonly Dictionary<string, string[]> _grantableByType;

    private AccessModel(Dictionary<string, string[]> grantableByType)
    {
        _grantableByType = grantableByType;
        ResourceTypes = grantableByType.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The model before any schema is loaded: no type resolves and nothing is grantable, so everything fails closed.</summary>
    public static AccessModel Empty { get; } = new(new Dictionary<string, string[]>(StringComparer.Ordinal));

    /// <summary>The object types a request may name, in ordinal order.</summary>
    public IReadOnlyList<string> ResourceTypes { get; }

    public static AccessModel FromSchema(IEnumerable<ReflectedDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var byType = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var definition in definitions.Where(IsResourceDefinition))
            byType[definition.Name] = definition.Relations.Where(IsRole).Select(r => r.Name).ToArray();
        return new AccessModel(byType);
    }

    public bool IsResourceType(string type) => _grantableByType.ContainsKey(type);

    /// <summary>The grantable relations of a type, in schema order; empty for a type that is not a resource type.</summary>
    public IReadOnlyList<string> GrantableRelations(string type) =>
        _grantableByType.TryGetValue(type, out var relations) ? relations : [];

    public bool IsGrantable(string type, string relation) => GrantableRelations(type).Contains(relation);

    private static bool IsResourceDefinition(ReflectedDefinition definition) =>
        definition.Name == PlatformSchema.Type
        || (definition.Relations.Any(r => r.Name == ParentRelation) && definition.Permissions.Contains(ManageAccessPermission));

    private static bool IsRole(ReflectedRelation relation) =>
        relation.AllowedSubjects.Count > 0 && relation.AllowedSubjects.All(IsPerson);

    private static bool IsPerson(AllowedSubject subject) =>
        subject is { IsWildcard: false, Caveat: null }
        && (subject is { Type: UserSchema.Type, Relation: null } || subject is { Type: GroupSchema.Type, Relation: GroupSchema.Relations.Member });
}

/// <summary>
/// The current <see cref="AccessModel"/>. Start-up loads it from the engine right after writing the schema, before the API
/// reports ready; until then it is <see cref="AccessModel.Empty"/>.
/// </summary>
public sealed class AccessModelSource(ISchemaStore schema)
{
    private AccessModel _current = AccessModel.Empty;

    public AccessModel Current => Volatile.Read(ref _current);

    public async Task<AccessModel> LoadAsync(CancellationToken ct)
    {
        var model = AccessModel.FromSchema(await schema.ReflectAsync(ct).ConfigureAwait(false));
        Volatile.Write(ref _current, model);
        return model;
    }
}
