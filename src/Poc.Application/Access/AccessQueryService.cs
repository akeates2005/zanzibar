using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;
using Poc.Application.Persistence;
using Poc.Application.Queries;
using Poc.Domain.Access;

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
    IAuthorizer authorizer)
{
    private const string AdminEffect = "admin";
    private const string ViewEffect = "view";

    /// <summary>Direct grants on the object, plus each ancestor's administrators and the platform's support subjects.</summary>
    public async Task<GrantsView> GrantsAsync(ResourceRef resource, CancellationToken ct)
    {
        var direct = new List<Relationship>();
        foreach (var tuple in await tuples.ReadAsync(new RelationshipFilter(resource.Type, resource.Id), ct).ConfigureAwait(false))
        {
            if (GrantableRelations.IsGrantable(resource.Type, tuple.Relation)) direct.Add(tuple);
        }

        var inheritedSources = new List<(ObjectSummary Source, Relationship Tuple, string Effect)>();
        foreach (var ancestor in await commerce.AncestorsAsync(resource, ct).ConfigureAwait(false))
        {
            foreach (var (relation, effect) in InheritedRelations(ancestor.Type))
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

        return new GrantsView(directViews, inheritedViews, GrantableRelations.For(resource.Type));
    }

    /// <summary>Every user who holds the permission, however they got it: directly, through groups, or by inheritance.</summary>
    public async Task<IReadOnlyList<SubjectView>> EffectiveAsync(ResourceRef resource, string permission, CancellationToken ct)
    {
        RequirePermission(resource.Type, permission);
        var holders = await authorizer.LookupSubjectsAsync(resource, permission, UserSchema.Type, ct).ConfigureAwait(false);
        var views = await ResolveAsync(holders, ct).ConfigureAwait(false);
        return holders.Select(h => views[h]).OrderBy(v => v.DisplayName, StringComparer.Ordinal).ToList();
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
        foreach (var type in GrantableRelations.ResourceTypes.Append(GroupSchema.Type))
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

    private static IEnumerable<(string Relation, string Effect)> InheritedRelations(string ancestorType) => ancestorType == PlatformSchema.Type
        ? [(PlatformSchema.Relations.Administrator, AdminEffect), (PlatformSchema.Relations.Support, ViewEffect)]
        : [(CustomerSchema.Relations.Administrator, AdminEffect)]; // administrator exists under the same name on customer, domain and wallet

    private static void RequirePermission(string type, string permission)
    {
        if (!SchemaCatalog.HasPermission(type, permission))
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
