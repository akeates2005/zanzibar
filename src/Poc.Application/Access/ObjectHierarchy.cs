using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;

namespace Poc.Application.Access;

/// <summary>Where an object sits: its ancestors root-first (nearest last), and the customer at the top, if any.</summary>
public sealed record ObjectPlacement(ResourceRef Resource, IReadOnlyList<ResourceRef> Ancestors)
{
    /// <summary>The customer the object sits under (itself, for a customer); <c>null</c> for the platform or a broken chain.</summary>
    public string? RootCustomerId => Resource.Type == CustomerSchema.Type
        ? Resource.Id
        : Ancestors.Where(a => a.Type == CustomerSchema.Type).Select(a => a.Id).FirstOrDefault();
}

/// <summary>
/// The structure of objects, read from the engine's <c>parent</c> tuples rather than from any application's database, so
/// it works for every team's types (NEXT-STEPS step 4). One walk serves both the grant command and the group picker.
/// <list type="bullet">
/// <item><c>platform:main</c> exists; any other resource type's object exists when it has a <c>parent</c> tuple. The
/// projector writes that tuple when the object is created and deletes it when the object is deleted.</item>
/// <item>The walk stops at an ancestor whose type has no <c>parent</c> relation (the platform), or one with no parent tuple.</item>
/// </list>
/// Reads are fully consistent, as every engine read in this application is.
/// </summary>
public sealed class ObjectHierarchy(IRelationshipReader tuples, AccessModelSource accessModel)
{
    /// <summary>Far deeper than any real hierarchy; a longer chain means a cycle in the data.</summary>
    public const int MaxDepth = 32;

    /// <summary>The object's placement, or <c>null</c> when it is not a resource type or does not exist in the engine.</summary>
    public async Task<ObjectPlacement?> PlaceAsync(ResourceRef resource, CancellationToken ct)
    {
        var model = accessModel.Current;
        if (resource.Type == PlatformSchema.Type)
            return resource.Id == PlatformSchema.MainId ? new ObjectPlacement(resource, []) : null;
        if (!model.IsResourceType(resource.Type)) return null;

        var ancestors = new List<ResourceRef>();
        var current = resource;
        while (model.HasRelation(current.Type, AccessModel.ParentRelation))
        {
            var parents = await tuples.ReadAsync(new RelationshipFilter(current.Type, current.Id, AccessModel.ParentRelation), ct).ConfigureAwait(false);
            if (parents.Count == 0)
            {
                if (ancestors.Count == 0) return null; // the object itself has no parent tuple: it does not exist
                break;
            }

            if (parents.Count > 1)
                throw new InvalidOperationException($"{current} has {parents.Count} parent tuples; the structure allows exactly one.");
            if (ancestors.Count == MaxDepth)
                throw new InvalidOperationException($"{resource} has more than {MaxDepth} ancestors; the parent tuples contain a cycle.");

            var parent = parents[0].Subject;
            current = new ResourceRef(parent.Type, parent.Id);
            ancestors.Add(current);
        }

        ancestors.Reverse();
        return new ObjectPlacement(resource, ancestors);
    }
}
