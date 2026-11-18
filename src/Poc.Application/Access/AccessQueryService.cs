using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;
using Poc.Application.Persistence;
using Poc.Application.Queries;
using Poc.Domain.Access;
using Poc.Domain.Directory;

namespace Poc.Application.Access;

/// <summary>
/// The read side of Access Management: who holds what on an object, why, and the audit of how it got there. It reads
/// tuples (never writes them) because grant listings are exactly where relations, not permissions, are the subject.
/// </summary>
public sealed class AccessQueryService(
    IRelationshipReader tuples,
    IGrantAuditStore audit,
    IDirectoryQueries directory,
    ICommerceQueries commerce,
    IAuthorizer authorizer,
    AccessModelSource accessModel,
    ObjectHierarchy hierarchy)
{
    private const string AdminEffect = "admin";
    private const string ViewEffect = "view";

    /// <summary>Direct grants on the object, plus each ancestor's administrators and the platform's support subjects.</summary>
    public async Task<GrantsView> GrantsAsync(ResourceRef resource, CancellationToken ct)
    {
        var model = accessModel.Current;
        var direct = new List<Relationship>();
        foreach (var tuple in await tuples.ReadAsync(new RelationshipFilter(resource.Type, resource.Id), ct).ConfigureAwait(false))
        {
            if (model.IsGrantable(resource.Type, tuple.Relation)) direct.Add(tuple);
        }

        var inheritedSources = new List<(ObjectSummary Source, Relationship Tuple, string Effect)>();
        foreach (var ancestor in await NamedAncestorsAsync(resource, ct).ConfigureAwait(false))
        {
            foreach (var (relation, effect) in InheritedRelations(model, ancestor.Type))
            {
                foreach (var tuple in await tuples.ReadAsync(new RelationshipFilter(ancestor.Type, ancestor.Id, relation), ct).ConfigureAwait(false))
                    inheritedSources.Add((ancestor, tuple, effect));
            }
        }

        var subjects = await ResolveAsync(direct.Select(d => d.Subject).Concat(inheritedSources.Select(i => i.Tuple.Subject)), ct).ConfigureAwait(false);

        var history = await audit.ForResourceAsync(resource, ct).ConfigureAwait(false);
        var origin = new Dictionary<(string, string, string, string?), GrantAuditEntry>();
        foreach (var entry in history.Where(h => h.Status == GrantStatus.Applied))
        {
            var key = (entry.Relation, entry.SubjectType, entry.SubjectId, entry.SubjectRelation);
            if (entry.Action == GrantAction.Grant) origin[key] = entry;
            else origin.Remove(key);
        }

        var directViews = direct
            .Select(d =>
            {
                var s = d.Subject;
                origin.TryGetValue((d.Relation, s.Type, s.Id, s.Relation), out var by);
                return new DirectGrantView(d.Relation, subjects[s], by?.ActorUserId, by?.At);
            })
            .OrderBy(g => g.Relation, StringComparer.Ordinal).ThenBy(g => g.Subject.DisplayName, StringComparer.Ordinal).ToList();

        var inheritedViews = inheritedSources
            .Select(i => new InheritedGrantView(i.Tuple.Relation, i.Effect, subjects[i.Tuple.Subject], i.Source.Type, i.Source.Id, i.Source.Name))
            .OrderBy(g => g.SourceType, StringComparer.Ordinal).ThenBy(g => g.Relation, StringComparer.Ordinal).ThenBy(g => g.Subject.DisplayName, StringComparer.Ordinal).ToList();

        return new GrantsView(directViews, inheritedViews, model.GrantableRelations(resource.Type));
    }

    /// <summary>Every user who holds the permission, however they got it: directly, through groups, or by inheritance.</summary>
    public async Task<IReadOnlyList<SubjectView>> EffectiveAsync(ResourceRef resource, string permission, CancellationToken ct)
    {
        RequirePermission(resource.Type, permission);
        var holders = await authorizer.LookupSubjectsAsync(resource, permission, UserSchema.Type, ct).ConfigureAwait(false);
        var views = await ResolveAsync(holders, ct).ConfigureAwait(false);
        return holders.Select(h => views[h]).OrderBy(v => v.DisplayName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The groups that may be granted on <paramref name="resource"/> under the service rules: the groups of the customer the
    /// object sits under, plus platform groups when the caller is a platform administrator (only they may grant those).
    /// A domain administrator cannot view the customer's group list, which is why the picker needs this instead.
    /// </summary>
    public async Task<IReadOnlyList<GroupSummary>> GrantableGroupsAsync(Actor actor, ResourceRef resource, CancellationToken ct)
    {
        // The same engine walk the grant command uses, so the picker and the command cannot disagree on the root customer.
        var rootCustomerId = (await hierarchy.PlaceAsync(resource, ct).ConfigureAwait(false))?.RootCustomerId;
        var isPlatformAdministrator = await PlatformAdministrator.IsAsync(authorizer, actor, ct).ConfigureAwait(false);
        var candidates = new List<GroupSummary>();
        if (rootCustomerId is not null)
            candidates.AddRange(await directory.AllGroupsOwnedByAsync(GroupOwnerType.Customer, rootCustomerId, ct).ConfigureAwait(false));
        candidates.AddRange(await directory.AllGroupsOwnedByAsync(GroupOwnerType.Platform, PlatformSchema.MainId, ct).ConfigureAwait(false));

        // The candidates are a superset; the rule decides, so the picker and the grant command cannot disagree.
        return candidates
            .Where(g => GrantRules.IsGroupGrantableBy(g.OwnerType, g.OwnerId, rootCustomerId, isPlatformAdministrator))
            .OrderBy(g => g.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Allowed or denied for any user, permission and resource, with the engine's trace where it returns one.</summary>
    public async Task<CheckExplanation> ExplainAsync(string userId, string permission, ResourceRef resource, CancellationToken ct)
    {
        RequirePermission(resource.Type, permission);
        if (!(await directory.GetUsersAsync([userId], ct).ConfigureAwait(false)).ContainsKey(userId))
            throw new RuleViolationException(GrantRules.Codes.UnknownSubject, $"User '{userId}' does not exist.");
        return await authorizer.ExplainAsync(SubjectRef.User(userId), permission, resource, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GrantAuditView>> AuditAsync(ResourceRef resource, CancellationToken ct) =>
        (await audit.ForResourceAsync(resource, ct).ConfigureAwait(false))
        .Select(a => new GrantAuditView(a.Id, a.Action.ToString(), a.Relation, a.SubjectType, a.SubjectId, a.SubjectRelation, a.ActorUserId, a.At, a.Status.ToString()))
        .ToList();

    /// <summary>A group with its members and every place it has been granted or nested.</summary>
    public async Task<GroupDetailView> GroupDetailAsync(string groupId, CancellationToken ct)
    {
        var group = (await directory.GetGroupsAsync([groupId], ct).ConfigureAwait(false)).GetValueOrDefault(groupId)
            ?? throw new NotFoundException($"group:{groupId}");

        var memberTuples = await tuples.ReadAsync(
            new RelationshipFilter(GroupSchema.Type, groupId, GroupSchema.Relations.Member), ct).ConfigureAwait(false);
        var memberViews = await ResolveAsync(memberTuples.Select(t => t.Subject), ct).ConfigureAwait(false);
        var members = memberTuples.Select(t => new GroupMemberView(memberViews[t.Subject])).OrderBy(m => m.Subject.DisplayName, StringComparer.Ordinal).ToList();

        var where = new List<Relationship>();
        var asSubject = new SubjectFilter(GroupSchema.Type, groupId, GroupSchema.Relations.Member);
        foreach (var type in accessModel.Current.ResourceTypes.Append(GroupSchema.Type))
            where.AddRange(await tuples.ReadAsync(new RelationshipFilter(type, Subject: asSubject), ct).ConfigureAwait(false));

        var names = (await commerce.GetSummariesAsync(where.Where(w => w.Resource.Type != GroupSchema.Type).Select(w => w.Resource), ct).ConfigureAwait(false))
            .ToDictionary(s => new ResourceRef(s.Type, s.Id), s => s.Name);
        var groupNames = await directory.GetGroupsAsync(where.Where(w => w.Resource.Type == GroupSchema.Type).Select(w => w.Resource.Id), ct).ConfigureAwait(false);
        var grantedOn = where
            .Select(w => new GroupGrantView(
                w.Resource.Type,
                w.Resource.Id,
                w.Resource.Type == GroupSchema.Type ? groupNames.GetValueOrDefault(w.Resource.Id)?.Name ?? w.Resource.Id : names.GetValueOrDefault(w.Resource, w.Resource.Id),
                w.Relation))
            .OrderBy(g => g.ResourceType, StringComparer.Ordinal).ThenBy(g => g.ResourceName, StringComparer.Ordinal).ToList();

        return new GroupDetailView(new GroupSummary(group.Id, group.Name, group.OwnerType, group.OwnerId), members, grantedOn);
    }

    /// <summary>
    /// PRD (Access tab): each ancestor's administrators and the platform's support subjects. An ancestor type with no
    /// <c>administrator</c> relation contributes nothing, rather than an engine error.
    /// </summary>
    private static IEnumerable<(string Relation, string Effect)> InheritedRelations(AccessModel model, string ancestorType)
    {
        if (model.HasRelation(ancestorType, CustomerSchema.Relations.Administrator)) yield return (CustomerSchema.Relations.Administrator, AdminEffect);
        if (ancestorType == PlatformSchema.Type) yield return (PlatformSchema.Relations.Support, ViewEffect);
    }

    /// <summary>Ancestors from the engine, root-first, named from this application's database where it knows them, else by id.</summary>
    private async Task<IReadOnlyList<ObjectSummary>> NamedAncestorsAsync(ResourceRef resource, CancellationToken ct)
    {
        var ancestors = (await hierarchy.PlaceAsync(resource, ct).ConfigureAwait(false))?.Ancestors ?? [];
        var names = (await commerce.GetSummariesAsync(ancestors, ct).ConfigureAwait(false))
            .ToDictionary(s => new ResourceRef(s.Type, s.Id), s => s.Name);
        return ancestors.Select(a => new ObjectSummary(a.Type, a.Id, names.GetValueOrDefault(a, a.Id), null, null)).ToList();
    }

    /// <summary>
    /// What the Access page needs for any resource type, including ones this application has no table for: the caller's
    /// answer for every permission of the type, and the ancestors (viewable ones openable, the rest labels).
    /// </summary>
    public async Task<AccessObjectView> AccessObjectAsync(Actor actor, ResourceRef resource, CancellationToken ct)
    {
        var permissions = await authorizer.CheckManyAsync(actor.Subject, resource, accessModel.Current.PermissionsOf(resource.Type), ct).ConfigureAwait(false);
        var crumbs = new List<Breadcrumb>();
        foreach (var ancestor in await NamedAncestorsAsync(resource, ct).ConfigureAwait(false))
        {
            var viewable = await authorizer.CheckAsync(actor.Subject, AccessModel.ViewPermission, new ResourceRef(ancestor.Type, ancestor.Id), ct).ConfigureAwait(false);
            crumbs.Add(new Breadcrumb(ancestor.Type, viewable ? ancestor.Id : null, ancestor.Name, viewable));
        }

        var name = await commerce.GetSummariesAsync([resource], ct).ConfigureAwait(false) is { Count: > 0 } known ? known[0].Name : resource.Id;
        return new AccessObjectView(resource.Type, resource.Id, name, permissions, crumbs);
    }

    private void RequirePermission(string type, string permission)
    {
        if (!accessModel.Current.HasPermission(type, permission))
            throw new RuleViolationException("unknown_permission", $"'{permission}' is not a permission of {type}.");
    }

    private async Task<Dictionary<SubjectRef, SubjectView>> ResolveAsync(IEnumerable<SubjectRef> subjects, CancellationToken ct)
    {
        var distinct = subjects.Distinct().ToList();
        var users = await directory.GetUsersAsync(distinct.Where(s => s.Type == UserSchema.Type).Select(s => s.Id), ct).ConfigureAwait(false);
        var groups = await directory.GetGroupsAsync(distinct.Where(s => s.Type == GroupSchema.Type).Select(s => s.Id), ct).ConfigureAwait(false);
        return distinct.ToDictionary(s => s, s => new SubjectView(
            s.Type,
            s.Id,
            s.Relation,
            s.Type == UserSchema.Type ? users.GetValueOrDefault(s.Id)?.DisplayName ?? s.Id : groups.GetValueOrDefault(s.Id)?.Name ?? s.Id));
    }
}
