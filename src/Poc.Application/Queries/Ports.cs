using Poc.Application.Authorization;
using Poc.Domain.Directory;

namespace Poc.Application.Queries;

/// <summary>Read side of Commerce. Implemented over the application database; never consults the engine.</summary>
public interface ICommerceQueries
{
    /// <summary>
    /// Whether the object still has children that block its deletion: domains under a customer (and groups it owns, so
    /// no group's owner edge can dangle), wallets under a domain, subscriptions under a wallet.
    /// </summary>
    Task<bool> HasChildrenAsync(ResourceRef resource, CancellationToken ct);

    /// <summary>Names and parents for the given objects. <c>platform:main</c> is included as "Platform". Unknown ids are omitted.</summary>
    Task<IReadOnlyList<ObjectSummary>> GetSummariesAsync(IEnumerable<ResourceRef> resources, CancellationToken ct);

    /// <summary>Children of <paramref name="parent"/> whose ids are in <paramref name="allowedIds"/>.</summary>
    Task<IReadOnlyList<ObjectSummary>> ChildrenAsync(ResourceRef parent, IReadOnlyCollection<string> allowedIds, CancellationToken ct);

    /// <summary>Ancestors of the object, root first, ending with its direct parent. Empty for the platform.</summary>
    Task<IReadOnlyList<ObjectSummary>> AncestorsAsync(ResourceRef resource, CancellationToken ct);
}

/// <summary>Read side of Identity and group directories.</summary>
public interface IDirectoryQueries
{
    Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string? query, int limit, CancellationToken ct);

    Task<IReadOnlyDictionary<string, DirectoryUser>> GetUsersAsync(IEnumerable<string> ids, CancellationToken ct);

    Task<IReadOnlyDictionary<string, GroupSummary>> GetGroupsAsync(IEnumerable<string> ids, CancellationToken ct);

    /// <summary>Groups owned by the customer (or by the platform), limited to <paramref name="allowedIds"/>.</summary>
    Task<IReadOnlyList<GroupSummary>> GroupsOwnedByAsync(GroupOwnerType ownerType, string ownerId, IReadOnlyCollection<string> allowedIds, CancellationToken ct);
}
