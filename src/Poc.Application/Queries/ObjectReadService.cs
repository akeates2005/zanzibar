using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;

namespace Poc.Application.Queries;

/// <summary>
/// Read-side views that combine the database (names, parents) with the engine (what the caller may see). Lists use
/// LookupResources intersected with the database query (PRD rule 5), so nothing is ever returned that the caller
/// could not view.
/// </summary>
public sealed class ObjectReadService(IAuthorizer authorizer, ICommerceQueries queries)
{
    private static readonly string[] ViewableObjectTypes =
        [PlatformSchema.Type, CustomerSchema.Type, DomainSchema.Type, WalletSchema.Type, SubscriptionSchema.Type];

    /// <summary>The caller's answer for every permission of the object's type, in one bulk check. The client uses it to enable controls; the server never trusts it.</summary>
    public Task<IReadOnlyDictionary<string, bool>> PermissionsAsync(Actor actor, ResourceRef resource, CancellationToken ct) =>
        authorizer.CheckManyAsync(actor.Subject, resource, SchemaCatalog.PermissionsOf(resource.Type), ct);

    /// <summary>Ancestors root-first. Ones the caller cannot view appear by name only, never as something to open.</summary>
    public async Task<IReadOnlyList<Breadcrumb>> BreadcrumbsAsync(Actor actor, ResourceRef resource, CancellationToken ct)
    {
        var ancestors = await queries.AncestorsAsync(resource, ct).ConfigureAwait(false);
        var crumbs = new List<Breadcrumb>(ancestors.Count);
        foreach (var ancestor in ancestors)
        {
            var viewable = await authorizer.CheckAsync(
                actor.Subject, PermissionFor(ancestor.Type), new ResourceRef(ancestor.Type, ancestor.Id), ct).ConfigureAwait(false);
            crumbs.Add(new Breadcrumb(ancestor.Type, viewable ? ancestor.Id : null, ancestor.Name, viewable));
        }

        return crumbs;
    }

    /// <summary>Every object the caller can view, each with its ancestors (viewable ones openable, the rest labels).</summary>
    public async Task<IReadOnlyList<TreeNode>> TreeAsync(Actor actor, CancellationToken ct)
    {
        var viewableIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var type in ViewableObjectTypes)
        {
            var ids = await authorizer.LookupResourcesAsync(actor.Subject, type, PermissionFor(type), ct).ConfigureAwait(false);
            viewableIds[type] = [.. ids];
        }

        var wanted = viewableIds.SelectMany(kv => kv.Value.Select(id => new ResourceRef(kv.Key, id))).ToList();
        var summaries = (await queries.GetSummariesAsync(wanted, ct).ConfigureAwait(false)).ToDictionary(s => new ResourceRef(s.Type, s.Id));

        // Pull in ancestors the caller cannot view so they can be shown as labels.
        while (true)
        {
            var missing = summaries.Values
                .Where(s => s.ParentType is not null)
                .Select(s => new ResourceRef(s.ParentType!, s.ParentId!))
                .Where(p => !summaries.ContainsKey(p))
                .Distinct().ToList();
            if (missing.Count == 0) break;
            var fetched = await queries.GetSummariesAsync(missing, ct).ConfigureAwait(false);
            if (fetched.Count == 0) break;
            foreach (var s in fetched) summaries[new ResourceRef(s.Type, s.Id)] = s;
        }

        var nodes = new List<TreeNode>();
        foreach (var summary in summaries.Values.Where(s => s.Type != PlatformSchema.Type && viewableIds[s.Type].Contains(s.Id)))
        {
            var chain = new List<Breadcrumb>();
            var cursor = summary;
            while (cursor.ParentType is not null && summaries.TryGetValue(new ResourceRef(cursor.ParentType, cursor.ParentId!), out var parent))
            {
                var viewable = viewableIds[parent.Type].Contains(parent.Id);
                chain.Add(new Breadcrumb(parent.Type, viewable ? parent.Id : null, parent.Name, viewable));
                cursor = parent;
            }

            chain.Reverse();
            nodes.Add(new TreeNode(summary.Type, summary.Id, summary.Name, chain));
        }

        return nodes.OrderBy(n => n.Ancestors.Count).ThenBy(n => n.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The children of <paramref name="parent"/> that the caller can view.</summary>
    public async Task<IReadOnlyList<ObjectSummary>> ChildrenAsync(Actor actor, ResourceRef parent, CancellationToken ct)
    {
        var childType = ChildTypeOf(parent.Type) ?? throw new NotFoundException($"{parent.Type} children");
        var visible = await authorizer.LookupResourcesAsync(actor.Subject, childType, PermissionFor(childType), ct).ConfigureAwait(false);
        return await queries.ChildrenAsync(parent, visible, ct).ConfigureAwait(false);
    }

    public static string? ChildTypeOf(string parentType) => parentType switch
    {
        PlatformSchema.Type => CustomerSchema.Type,
        CustomerSchema.Type => DomainSchema.Type,
        DomainSchema.Type => WalletSchema.Type,
        WalletSchema.Type => SubscriptionSchema.Type,
        _ => null,
    };

    /// <summary>The permission that means "the caller may see this object" for each type.</summary>
    private static string PermissionFor(string type) => type switch
    {
        PlatformSchema.Type => PlatformSchema.Permissions.View,
        CustomerSchema.Type => CustomerSchema.Permissions.View,
        DomainSchema.Type => DomainSchema.Permissions.View,
        WalletSchema.Type => WalletSchema.Permissions.View,
        SubscriptionSchema.Type => SubscriptionSchema.Permissions.View,
        GroupSchema.Type => GroupSchema.Permissions.View,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a viewable object type."),
    };
}
