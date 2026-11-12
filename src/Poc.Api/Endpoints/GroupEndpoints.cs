using Poc.Api.Contracts;
using Poc.Api.Security;
using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;
using Poc.Application.Queries;
using Poc.Domain.Directory;

namespace Poc.Api.Endpoints;

/// <summary>Customer and platform groups: create, list, inspect, change membership, delete.</summary>
public static class GroupEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/customers/{id}/groups", (string id, AuthContext ctx, CancellationToken ct) =>
            ListAsync(ctx, GroupOwnerType.Customer, id, ct))
            .WithName("ListCustomerGroups").WithTags("Groups")
            .RequiresOn(CustomerSchema.Permissions.View, ResourceLocator.Route(CustomerSchema.Type)).Produces<List<GroupSummary>>();

        api.MapPost("/customers/{id}/groups", (string id, NameRequest body, AccessService access, CurrentUser user, CancellationToken ct) =>
            CreateAsync(access, user, GroupOwnerType.Customer, id, body, ct))
            .WithName("CreateCustomerGroup").WithTags("Groups")
            .RequiresOn(CustomerSchema.Permissions.CreateGroup, ResourceLocator.Route(CustomerSchema.Type))
            .Produces<GroupCreatedDto>(StatusCodes.Status201Created).Produces<PendingResponse>(StatusCodes.Status202Accepted);

        api.MapGet("/platform/groups", (AuthContext ctx, CancellationToken ct) =>
            ListAsync(ctx, GroupOwnerType.Platform, PlatformSchema.MainId, ct))
            .WithName("ListPlatformGroups").WithTags("Groups")
            .RequiresOn(PlatformSchema.Permissions.View, ResourceLocator.Platform).Produces<List<GroupSummary>>();

        api.MapPost("/platform/groups", (NameRequest body, AccessService access, CurrentUser user, CancellationToken ct) =>
            CreateAsync(access, user, GroupOwnerType.Platform, PlatformSchema.MainId, body, ct))
            .WithName("CreatePlatformGroup").WithTags("Groups")
            .RequiresOn(PlatformSchema.Permissions.CreateGroup, ResourceLocator.Platform)
            .Produces<GroupCreatedDto>(StatusCodes.Status201Created).Produces<PendingResponse>(StatusCodes.Status202Accepted);

        api.MapGet("/groups/{id}", async (string id, AccessQueryService access, CancellationToken ct) =>
            Results.Ok(await access.GroupDetailAsync(id, ct)))
            .WithName("GetGroup").WithTags("Groups")
            .RequiresOn(GroupSchema.Permissions.View, ResourceLocator.Route(GroupSchema.Type)).Produces<GroupDetailView>();

        api.MapDelete("/groups/{id}", async (string id, AccessService access, CurrentUser user, CancellationToken ct) =>
            Results2.Deleted(id, await access.DeleteGroupAsync(user.Actor, id, ct)))
            .WithName("DeleteGroup").WithTags("Groups")
            .RequiresOn(GroupSchema.Permissions.Manage, ResourceLocator.Route(GroupSchema.Type))
            .Produces(StatusCodes.Status204NoContent).Produces<PendingResponse>(StatusCodes.Status202Accepted);

        api.MapPut("/groups/{id}/members", async (string id, SubjectRequest body, AccessService access, CurrentUser user, CancellationToken ct) =>
            Results.Ok(new GrantResponse((await access.AddGroupMemberAsync(user.Actor, id, ToSubject(body.Type, body.Id), ct)).AuditId)))
            .WithName("AddGroupMember").WithTags("Groups")
            .RequiresOn(GroupSchema.Permissions.Manage, ResourceLocator.Route(GroupSchema.Type)).Produces<GrantResponse>();

        api.MapDelete("/groups/{id}/members", async (string id, string? type, string? memberId, AccessService access, CurrentUser user, CancellationToken ct) =>
            Results.Ok(new GrantResponse((await access.RemoveGroupMemberAsync(user.Actor, id, ToSubject(type, memberId), ct)).AuditId)))
            .WithName("RemoveGroupMember").WithTags("Groups")
            .RequiresOn(GroupSchema.Permissions.Manage, ResourceLocator.Route(GroupSchema.Type)).Produces<GrantResponse>();
    }

    private static async Task<IResult> ListAsync(AuthContext ctx, GroupOwnerType ownerType, string ownerId, CancellationToken ct)
    {
        // Only groups the caller can view: LookupResources intersected with the database query (PRD rule 5).
        var visible = await ctx.Authorizer.LookupResourcesAsync(ctx.User.Actor.Subject, GroupSchema.Type, GroupSchema.Permissions.View, ct);
        return Results.Ok(await ctx.Directory.GroupsOwnedByAsync(ownerType, ownerId, visible, ct));
    }

    private static async Task<IResult> CreateAsync(AccessService access, CurrentUser user, GroupOwnerType ownerType, string ownerId, NameRequest body, CancellationToken ct)
    {
        var name = Results2.Require(body.Name, "name");
        var result = await access.CreateGroupAsync(user.Actor, ownerType, ownerId, name, ct);
        return result.AuthorisationPending
            ? Results.Json(new PendingResponse(result.GroupId, true), statusCode: StatusCodes.Status202Accepted)
            : Results.Created($"/api/groups/{result.GroupId}", new GroupCreatedDto(result.GroupId, name, false));
    }

    private static SubjectRef ToSubject(string? type, string? id)
    {
        var subjectId = Results2.Require(id, "id");
        return type switch
        {
            UserSchema.Type => SubjectRef.User(subjectId),
            GroupSchema.Type => SubjectRef.GroupMembers(subjectId),
            _ => throw new RuleViolationException(GrantRules.Codes.InvalidSubject, "The member must be a user or a group."),
        };
    }
}

/// <summary>The few services a list endpoint needs together, so each handler's signature stays readable.</summary>
public sealed class AuthContext(IAuthorizer authorizer, IDirectoryQueries directory, CurrentUser user)
{
    public IAuthorizer Authorizer { get; } = authorizer;

    public IDirectoryQueries Directory { get; } = directory;

    public CurrentUser User { get; } = user;
}
