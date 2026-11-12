using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;
using Poc.Application.Persistence;
using Poc.Application.Projection;
using Poc.Domain.Access;
using Poc.Domain.Directory;

namespace Poc.Application.Access;

/// <summary>A relation granted (or revoked) on a resource to a subject.</summary>
public sealed record GrantTarget(ResourceRef Resource, string Relation, SubjectRef Subject)
{
    public Relationship ToRelationship() => new(Resource, Relation, Subject);
}

public sealed record GrantResult(long AuditId, string ZedToken);

public sealed record GroupCommandResult(string GroupId, bool AuthorisationPending);

/// <summary>
/// Access Management: grants, revokes, group membership and group lifecycle. This and the projector are the only
/// writers of tuples. Grant sequence (PRD): check permission, apply the service rules, write a Pending audit row, write
/// the tuple, mark the row Applied with the ZedToken. A failed tuple write marks it Failed and surfaces as a 503.
/// Grants use touch and revokes use delete, so repeating either is safe.
/// </summary>
public sealed class AccessService(
    IAuthorizer authorizer,
    IRelationshipStore tuples,
    IGrantAuditStore audit,
    IDirectoryStore directory,
    IAuthzTokenStore tokens,
    IRepository<Group> groups,
    IUnitOfWork unitOfWork,
    IAuthorizationProjector projector,
    IAccessLog log,
    TimeProvider time)
{
    // ---- Grants on business objects -----------------------------------------------------------------------------

    public Task<GrantResult> GrantAsync(Actor actor, GrantTarget grant, CancellationToken ct) =>
        ChangeGrantAsync(actor, GrantAction.Grant, grant, ct);

    public Task<GrantResult> RevokeAsync(Actor actor, GrantTarget grant, CancellationToken ct) =>
        ChangeGrantAsync(actor, GrantAction.Revoke, grant, ct);

    private async Task<GrantResult> ChangeGrantAsync(Actor actor, GrantAction action, GrantTarget grant, CancellationToken ct)
    {
        // Authorise first so an unauthorised caller learns nothing from validation messages.
        await RequireAsync(actor, PermissionFor(grant.Resource.Type), grant.Resource, ct).ConfigureAwait(false);

        GrantRules.RequireGrantableRelation(grant.Resource.Type, grant.Relation);
        GrantRules.RequireValidSubjectShape(grant.Subject);

        var info = await directory.DescribeAsync(grant.Resource, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(grant.Resource.ToString());

        if (grant.Subject.Type == GroupSchema.Type)
        {
            var group = await directory.FindGroupAsync(grant.Subject.Id, ct).ConfigureAwait(false)
                ?? throw new RuleViolationException(GrantRules.Codes.UnknownSubject, $"Group '{grant.Subject.Id}' does not exist.");
            GrantRules.RequireGroupInScope(group, info.RootCustomerId);
            // Platform groups may be granted anywhere, and only by a platform administrator (rule 1).
            if (group.OwnerType == GroupOwnerType.Platform)
                await RequireAsync(actor, PlatformSchema.Permissions.Admin, PlatformSchema.Main, ct).ConfigureAwait(false);
        }
        else if (action == GrantAction.Grant && !await directory.UserExistsAsync(grant.Subject.Id, ct).ConfigureAwait(false))
        {
            throw new RuleViolationException(GrantRules.Codes.UnknownSubject, $"User '{grant.Subject.Id}' does not exist.");
        }

        return await ApplyAuditedAsync(actor, action, grant, ct).ConfigureAwait(false);
    }

    // ---- Group membership ---------------------------------------------------------------------------------------

    public Task<GrantResult> AddGroupMemberAsync(Actor actor, string groupId, SubjectRef member, CancellationToken ct) =>
        ChangeMembershipAsync(actor, GrantAction.Grant, groupId, member, ct);

    public Task<GrantResult> RemoveGroupMemberAsync(Actor actor, string groupId, SubjectRef member, CancellationToken ct) =>
        ChangeMembershipAsync(actor, GrantAction.Revoke, groupId, member, ct);

    private async Task<GrantResult> ChangeMembershipAsync(
        Actor actor, GrantAction action, string groupId, SubjectRef member, CancellationToken ct)
    {
        var groupRef = new ResourceRef(GroupSchema.Type, groupId);
        await RequireAsync(actor, GroupSchema.Permissions.Manage, groupRef, ct).ConfigureAwait(false);
        GrantRules.RequireValidSubjectShape(member);

        var container = await directory.FindGroupAsync(groupId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(groupRef.ToString());

        if (member.Type == GroupSchema.Type)
        {
            var child = await directory.FindGroupAsync(member.Id, ct).ConfigureAwait(false)
                ?? throw new RuleViolationException(GrantRules.Codes.UnknownSubject, $"Group '{member.Id}' does not exist.");
            if (action == GrantAction.Grant)
            {
                GrantRules.RequireNestable(container, child);
                // Rule 3: nesting B inside A is refused if A is already a member of B, directly or indirectly. That is
                // the engine's answer to "group:B#member@group:A#member", asked before anything is written.
                var wouldCycle = await authorizer.CheckAsync(
                    SubjectRef.GroupMembers(container.Id), GroupSchema.Relations.Member, new ResourceRef(GroupSchema.Type, child.Id), ct)
                    .ConfigureAwait(false);
                if (wouldCycle)
                    throw new RuleViolationException(GrantRules.Codes.GroupCycle,
                        $"Group '{container.Id}' is already a member of '{child.Id}'; nesting '{child.Id}' inside it would create a cycle.");
            }
        }
        else if (action == GrantAction.Grant && !await directory.UserExistsAsync(member.Id, ct).ConfigureAwait(false))
        {
            throw new RuleViolationException(GrantRules.Codes.UnknownSubject, $"User '{member.Id}' does not exist.");
        }

        return await ApplyAuditedAsync(actor, action, new GrantTarget(groupRef, GroupSchema.Relations.Member, member), ct)
            .ConfigureAwait(false);
    }

    // ---- Group lifecycle ----------------------------------------------------------------------------------------

    /// <summary>
    /// Creates a group and flushes its owner tuple. The caller must already have checked <c>create_group</c> on the
    /// owner (the endpoint filter does, as for every create route).
    /// </summary>
    public async Task<GroupCommandResult> CreateGroupAsync(
        Actor actor, GroupOwnerType ownerType, string ownerId, string name, CancellationToken ct, string? id = null)
    {
        var owner = ownerType == GroupOwnerType.Platform
            ? PlatformSchema.Main
            : new ResourceRef(CustomerSchema.Type, ownerId);
        if (await directory.DescribeAsync(owner, ct).ConfigureAwait(false) is null) throw new NotFoundException(owner.ToString());

        var group = Group.Create(ownerType, ownerId, name, actor.UserId, time.GetUtcNow(), id);
        groups.Add(group);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        var outcome = await projector.FlushAsync(ct).ConfigureAwait(false);
        return new GroupCommandResult(group.Id, !outcome.FullyApplied);
    }

    /// <summary>Deletes a group. The projector then removes it, and every grant and membership it appears in, from the engine.</summary>
    public async Task<bool> DeleteGroupAsync(Actor actor, string groupId, CancellationToken ct)
    {
        _ = actor; // authorisation (`manage` on the group) is the endpoint filter's job, as for every delete route
        var group = await groups.FindAsync(groupId, ct).ConfigureAwait(false) ?? throw new NotFoundException($"group:{groupId}");
        group.Delete();
        groups.Remove(group);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        var outcome = await projector.FlushAsync(ct).ConfigureAwait(false);
        return !outcome.FullyApplied;
    }

    // ---- Shared -------------------------------------------------------------------------------------------------

    private static string PermissionFor(string resourceType) => resourceType switch
    {
        PlatformSchema.Type => PlatformSchema.Permissions.ManageAccess,
        CustomerSchema.Type => CustomerSchema.Permissions.ManageAccess,
        DomainSchema.Type => DomainSchema.Permissions.ManageAccess,
        WalletSchema.Type => WalletSchema.Permissions.ManageAccess,
        SubscriptionSchema.Type => SubscriptionSchema.Permissions.ManageAccess,
        _ => throw new RuleViolationException(GrantRules.Codes.UnknownRelation, $"{resourceType} takes no direct grants."),
    };

    private async Task RequireAsync(Actor actor, string permission, ResourceRef resource, CancellationToken ct)
    {
        if (actor.IsSystem) return;
        if (!await authorizer.CheckAsync(actor.Subject, permission, resource, ct).ConfigureAwait(false))
            throw new PermissionDeniedException(permission, resource);
    }

    private async Task<GrantResult> ApplyAuditedAsync(Actor actor, GrantAction action, GrantTarget grant, CancellationToken ct)
    {
        var relationship = grant.ToRelationship();
        var entry = GrantAuditEntry.Pending(
            action, grant.Resource.Type, grant.Resource.Id, grant.Relation,
            grant.Subject.Type, grant.Subject.Id, grant.Subject.Relation, actor.UserId, time.GetUtcNow());
        await audit.AddAsync(entry, ct).ConfigureAwait(false);

        string token;
        try
        {
            token = action == GrantAction.Grant
                ? await tuples.WriteAsync([RelationshipUpdate.Touch(relationship)], ct).ConfigureAwait(false)
                : await tuples.DeleteAsync(ExactFilter(grant), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            entry.MarkFailed();
            try
            {
                await audit.UpdateAsync(entry, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception secondary) when (secondary is not OperationCanceledException)
            {
                // The row stays Pending, which is itself the visible signal of a crash window. Keep the original error.
            }

            log.Failed(actor, action, relationship, ex);
            throw;
        }

        entry.MarkApplied(token);
        await audit.UpdateAsync(entry, ct).ConfigureAwait(false);
        log.Applied(actor, action, relationship);
        try
        {
            await tokens.StampAsync(grant.Resource, token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The tuple is already written; the stamp is informational. Do not fail a grant that succeeded.
            log.TokenStampFailed(grant.Resource, ex);
        }

        return new GrantResult(entry.Id, token);
    }

    private static RelationshipFilter ExactFilter(GrantTarget grant) => new(
        grant.Resource.Type,
        grant.Resource.Id,
        grant.Relation,
        new SubjectFilter(grant.Subject.Type, grant.Subject.Id, grant.Subject.Relation ?? string.Empty));
}
