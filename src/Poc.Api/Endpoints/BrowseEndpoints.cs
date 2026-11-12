using Poc.Api.Contracts;
using Poc.Api.Security;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Queries;

namespace Poc.Api.Endpoints;

/// <summary>Who am I, what can I see, and who can I pick. List endpoints return only what the caller can view.</summary>
public static class BrowseEndpoints
{
    private const int UserSearchLimit = 20;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/me", (CurrentUser user) => Results.Ok(new MeResponse(user.UserId, user.Name, user.Emails)))
            .WithName("GetMe").WithTags("Identity")
            .Requires(new AccessRequirement.Authenticated("A caller may always ask who they are."))
            .Produces<MeResponse>();

        api.MapGet("/tree", async (ObjectReadService read, CurrentUser user, CancellationToken ct) =>
            Results.Ok((await read.TreeAsync(user.Actor, ct)).Select(n =>
                new TreeNodeDto(n.Type, n.Id, n.Name, n.Ancestors.Select(a => new BreadcrumbDto(a.Type, a.Id, a.Name, a.Viewable)).ToList())).ToList()))
            .WithName("GetTree").WithTags("Browse")
            .Requires(new AccessRequirement.Authenticated("Returns only objects the caller can view; ancestors they cannot view are labels."))
            .Produces<List<TreeNodeDto>>();

        // Children of a platform, customer, domain or wallet: view on the parent, then only children the caller can view.
        api.MapGet("/{parentType}/{id}/children", async (string parentType, string id, ObjectReadService read, CurrentUser user, CancellationToken ct) =>
            Results.Ok((await read.ChildrenAsync(user.Actor, new ResourceRef(parentType, id), ct)).Select(c => new ObjectSummaryDto(c.Type, c.Id, c.Name)).ToList()))
            .WithName("GetChildren").WithTags("Browse")
            .RequiresOn(PlatformSchema.Permissions.View, ResourceLocator.RouteTyped("parentType"))
            .Produces<List<ObjectSummaryDto>>();

        api.MapGet("/users", async (string? query, IDirectoryQueries directory, CancellationToken ct) =>
            Results.Ok(await directory.SearchUsersAsync(query, UserSearchLimit, ct)))
            .WithName("SearchUsers").WithTags("Directory")
            .Requires(new AccessRequirement.Authenticated("Directory search for pickers: any signed-in user (PRD open question 7)."))
            .Produces<List<DirectoryUser>>();
    }
}
