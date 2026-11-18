using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;

namespace Poc.Application.Access;

/// <summary>
/// What the deployed schema says, in the terms the access layer needs, derived from the engine rather than written by hand
/// (DECISIONS.md, "Access model read from the engine"):
/// <list type="bullet">
/// <item>A <b>resource type</b> is <c>platform</c>, or a definition with a <c>parent</c> relation and the <c>view</c> and
/// <c>manage_access</c> permissions (the endpoint filter checks <c>view</c> on every resource route). <c>group</c> and
/// <c>user</c> are not: groups are managed by their own commands.</item>
/// <item>A <b>grantable relation</b> is a relation of a resource type whose every allowed subject is a person: <c>user</c> or
/// <c>group#member</c>, with no wildcard and no caveat. A relation that points at an object (<c>parent</c>) is structural.</item>
/// <item>The <b>permissions</b> and <b>relations</b> of every definition, in schema order.</item>
/// </list>
/// Immutable; <see cref="AccessModelSource"/> holds the current one.
/// </summary>
public sealed class AccessModel
{
    public const string ParentRelation = CustomerSchema.Relations.Parent;
    public const string ManageAccessPermission = CustomerSchema.Permissions.ManageAccess;
    public const string ViewPermission = CustomerSchema.Permissions.View;

    private readonly Dictionary<string, string[]> _grantableByType;
    private readonly Dictionary<string, ReflectedDefinition> _definitions;

    private AccessModel(IReadOnlyList<ReflectedDefinition> definitions)
    {
        _definitions = definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
        _grantableByType = definitions.Where(IsResourceDefinition)
            .ToDictionary(d => d.Name, d => d.Relations.Where(IsRole).Select(r => r.Name).ToArray(), StringComparer.Ordinal);
        ResourceTypes = _grantableByType.Keys.Order(StringComparer.Ordinal).ToArray();
        TypesWithRelations = definitions.Where(d => d.Relations.Count > 0).Select(d => d.Name).Order(StringComparer.Ordinal).ToArray();
        TypesGrantingGroups = definitions
            .Where(d => d.Relations.Any(r => r.AllowedSubjects.Any(s => s is { Type: GroupSchema.Type, Relation: GroupSchema.Relations.Member })))
            .Select(d => d.Name).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The model before any schema is loaded: no type resolves and nothing is grantable, so everything fails closed.</summary>
    public static AccessModel Empty { get; } = new([]);

    /// <summary>Whether this model came from a schema; <see cref="Empty"/> did not.</summary>
    public bool IsLoaded => _definitions.Count > 0;

    /// <summary>The object types a request may name, in ordinal order.</summary>
    public IReadOnlyList<string> ResourceTypes { get; }

    /// <summary>Every definition that has at least one relation, so can hold tuples as a resource. Ordinal order.</summary>
    public IReadOnlyList<string> TypesWithRelations { get; }

    /// <summary>Every definition with a relation that accepts <c>group#member</c>: where a deleted group's grants can be.</summary>
    public IReadOnlyList<string> TypesGrantingGroups { get; }

    public static AccessModel FromSchema(IEnumerable<ReflectedDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return new AccessModel(definitions.ToList());
    }

    public bool IsResourceType(string type) => _grantableByType.ContainsKey(type);

    /// <summary>The grantable relations of a type, in schema order; empty for a type that is not a resource type.</summary>
    public IReadOnlyList<string> GrantableRelations(string type) =>
        _grantableByType.TryGetValue(type, out var relations) ? relations : [];

    public bool IsGrantable(string type, string relation) => GrantableRelations(type).Contains(relation);

    /// <summary>Every permission of the type, in schema order; empty for an unknown type.</summary>
    public IReadOnlyList<string> PermissionsOf(string type) =>
        _definitions.TryGetValue(type, out var definition) ? definition.Permissions : [];

    public bool HasPermission(string type, string permission) => PermissionsOf(type).Contains(permission);

    public bool HasRelation(string type, string relation) =>
        _definitions.TryGetValue(type, out var definition) && definition.Relations.Any(r => r.Name == relation);

    private static bool IsResourceDefinition(ReflectedDefinition definition) =>
        definition.Name == PlatformSchema.Type
        || (definition.Relations.Any(r => r.Name == ParentRelation)
            && definition.Permissions.Contains(ManageAccessPermission)
            && definition.Permissions.Contains(ViewPermission));

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
