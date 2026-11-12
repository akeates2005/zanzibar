using Poc.Api.Contracts;
using Poc.Api.Security;
using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;
using Poc.Application.Queries;

namespace Poc.Api.Endpoints;

/// <summary>Grants, effective access, the permission explorer and the grant audit.</summary>
public static class AccessEndpoints
{
    private static readonly ResourceLocator Target = ResourceLocator.RouteTyped("type");

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/access/{type}/{id}/grants", async (string type, string id, AccessQueryService access, CancellationToken ct) =>
            Results.Ok(await access.GrantsAsync(new ResourceRef(type, id), ct)))
            .WithName("GetGrants").WithTags("Access")
            .RequiresOn(PlatformSchema.Permissions.ManageAccess, Target).Produces<GrantsView>();

        api.MapPut("/access/{type}/{id}/grants", async (string type, string id, GrantRequest body, AccessService access, CurrentUser user, CancellationToken ct) =>
        {
            var result = await access.GrantAsync(user.Actor, ToTarget(type, id, body.Relation, body.Subject), ct);
            return Results.Ok(new GrantResponse(result.AuditId));
        }).WithName("Grant").WithTags("Access").RequiresOn(PlatformSchema.Permissions.ManageAccess, Target).Produces<GrantResponse>();

        // Revoke names its grant in the query string so no client or proxy has to send a body on DELETE.
        api.MapDelete("/access/{type}/{id}/grants", async (string type, string id, string? relation, string? subjectType, string? subjectId, AccessService access, CurrentUser user, CancellationToken ct) =>
        {
            var result = await access.RevokeAsync(user.Actor, ToTarget(type, id, relation, new SubjectRequest(subjectType, subjectId)), ct);
            return Results.Ok(new GrantResponse(result.AuditId));
        }).WithName("Revoke").WithTags("Access").RequiresOn(PlatformSchema.Permissions.ManageAccess, Target).Produces<GrantResponse>();

        api.MapGet("/access/{type}/{id}/grantable-groups", async (string type, string id, AccessQueryService access, CurrentUser user, CancellationToken ct) =>
            Results.Ok(await access.GrantableGroupsAsync(user.Actor, new ResourceRef(type, id), ct)))
            .WithName("GetGrantableGroups").WithTags("Access")
            .RequiresOn(PlatformSchema.Permissions.ManageAccess, Target).Produces<List<GroupSummary>>();

        api.MapGet("/access/{type}/{id}/effective", async (string type, string id, string? permission, AccessQueryService access, CancellationToken ct) =>
            Results.Ok(await access.EffectiveAsync(new ResourceRef(type, id), Results2.Require(permission, "permission"), ct)))
            .WithName("GetEffectiveAccess").WithTags("Access")
            .RequiresOn(PlatformSchema.Permissions.ManageAccess, Target).Produces<List<SubjectView>>();

        // The object is named in the body, so the handler (not the filter) checks manage_access on it.
        api.MapPost("/access/explain", async (ExplainRequest body, AccessQueryService access, ResourceAccess guard, CurrentUser user, CancellationToken ct) =>
        {
            var type = Results2.Require(body.ResourceType, "resourceType");
            var id = Results2.Require(body.ResourceId, "resourceId");
            if (!ResourceLocator.NamedTypes.Contains(type) || !IdOk(id)) throw new NotFoundException($"{type}:{id}");

            var resource = new ResourceRef(type, id);
            await guard.RequireAsync(user.Actor, PlatformSchema.Permissions.ManageAccess, resource, ct);
            var explanation = await access.ExplainAsync(
                Results2.Require(body.UserId, "userId"), Results2.Require(body.Permission, "permission"), resource, ct);
            return Results.Ok(new ExplainResponse(explanation.Allowed, explanation.Trace is null ? [] : Flatten(explanation.Trace, 0).ToList()));
        }).WithName("ExplainAccess").WithTags("Access")
          .Requires(new AccessRequirement.HandlerChecked(PlatformSchema.Permissions.ManageAccess, "The resource is named in the body."))
          .Produces<ExplainResponse>();

        api.MapGet("/access/audit", async (string? type, string? id, AccessQueryService access, CancellationToken ct) =>
            Results.Ok(await access.AuditAsync(new ResourceRef(type!, id!), ct)))
            .WithName("GetGrantAudit").WithTags("Access")
            .RequiresOn(PlatformSchema.Permissions.ManageAccess, ResourceLocator.QueryTyped("type", "id")).Produces<List<GrantAuditView>>();
    }

    /// <summary>A group grantee is named by type alone in the API; its members are the subject.</summary>
    private static GrantTarget ToTarget(string type, string id, string? relation, SubjectRequest? subject)
    {
        var subjectType = Results2.Require(subject?.Type, "subject.type");
        var subjectId = Results2.Require(subject?.Id, "subject.id");
        var subjectRef = subjectType switch
        {
            UserSchema.Type => SubjectRef.User(subjectId),
            GroupSchema.Type => SubjectRef.GroupMembers(subjectId),
            _ => throw new RuleViolationException(GrantRules.Codes.InvalidSubject, "The subject must be a user or a group."),
        };
        return new GrantTarget(new ResourceRef(type, id), Results2.Require(relation, "relation"), subjectRef);
    }

    private static bool IdOk(string id)
    {
        try
        {
            Poc.Domain.Common.EntityId.Validate(id);
            return true;
        }
        catch (Poc.Domain.Common.DomainRuleException)
        {
            return false;
        }
    }

    /// <summary>Pre-order, so a client can render the tree by indenting each step by its depth.</summary>
    private static IEnumerable<ExplanationStepDto> Flatten(ExplanationStep step, int depth)
    {
        yield return new ExplanationStepDto(
            depth, step.Resource.Type, step.Resource.Id, step.Permission, step.Subject?.Type, step.Subject?.Id, step.Subject?.Relation,
            step.IsRelation, step.Allowed);
        foreach (var child in step.Children)
            foreach (var inner in Flatten(child, depth + 1)) yield return inner;
    }
}
