using Poc.Application.Authorization;
using Poc.Application.Common;

namespace Poc.Api.Security;

public enum AccessDecision
{
    Allowed,

    /// <summary>The caller cannot view the object, so it must look as if it does not exist (404).</summary>
    NotVisible,

    /// <summary>The caller can view the object but lacks the permission (403 naming it).</summary>
    MissingPermission,
}

/// <summary>
/// The one place that turns engine answers into "allowed / 404 / 403". The enforcement filter and any handler that must
/// check a second object (a move's target, an explain request's resource) both use it, so the rule cannot diverge.
/// </summary>
public sealed class ResourceAccess(IAuthorizer authorizer)
{
    public const string View = "view";

    public async Task<AccessDecision> EvaluateAsync(Actor actor, string permission, ResourceRef resource, CancellationToken ct)
    {
        if (permission == View)
        {
            return await authorizer.CheckAsync(actor.Subject, View, resource, ct).ConfigureAwait(false)
                ? AccessDecision.Allowed : AccessDecision.NotVisible;
        }

        // One bulk check for view and the required permission together (PRD rule 3).
        var held = await authorizer.CheckManyAsync(actor.Subject, resource, [View, permission], ct).ConfigureAwait(false);
        if (!held[View]) return AccessDecision.NotVisible;
        return held[permission] ? AccessDecision.Allowed : AccessDecision.MissingPermission;
    }

    /// <summary>Throws <see cref="NotFoundException"/> or <see cref="PermissionDeniedException"/> unless allowed.</summary>
    public async Task RequireAsync(Actor actor, string permission, ResourceRef resource, CancellationToken ct)
    {
        switch (await EvaluateAsync(actor, permission, resource, ct).ConfigureAwait(false))
        {
            case AccessDecision.NotVisible:
                throw new NotFoundException(resource.ToString());
            case AccessDecision.MissingPermission:
                throw new PermissionDeniedException(permission, resource);
        }
    }
}
