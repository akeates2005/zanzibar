using Poc.Domain.Directory;

namespace Poc.Application.Queries;

/// <summary>A business object as the read side sees it: its name and where it hangs in the tree.</summary>
public sealed record ObjectSummary(string Type, string Id, string Name, string? ParentType, string? ParentId);

/// <summary>An ancestor on the way to the root. <see cref="Id"/> is null when the caller cannot view it, so a client can only label it.</summary>
public sealed record Breadcrumb(string Type, string? Id, string Name, bool Viewable);

public sealed record TreeNode(string Type, string Id, string Name, IReadOnlyList<Breadcrumb> Ancestors);

public sealed record DirectoryUser(string Id, string DisplayName, string Email);

public sealed record GroupSummary(string Id, string Name, GroupOwnerType OwnerType, string OwnerId);

public sealed record SubjectView(string Type, string Id, string? Relation, string DisplayName);

public sealed record DirectGrantView(string Relation, SubjectView Subject, string? GrantedBy, DateTimeOffset? GrantedAt);

/// <summary>Access that reaches an object from an ancestor: that ancestor's administrators, and the platform's support subjects.</summary>
public sealed record InheritedGrantView(string Relation, string Effect, SubjectView Subject, string SourceType, string SourceId, string SourceName);

public sealed record GrantsView(
    IReadOnlyList<DirectGrantView> Direct,
    IReadOnlyList<InheritedGrantView> Inherited,
    IReadOnlyList<string> GrantableRelations);

public sealed record GrantAuditView(
    long Id, string Action, string Relation, string SubjectType, string SubjectId, string? SubjectRelation,
    string Actor, DateTimeOffset At, string Status);

public sealed record GroupMemberView(SubjectView Subject);

public sealed record GroupGrantView(string ResourceType, string ResourceId, string ResourceName, string Relation);

public sealed record GroupDetailView(
    GroupSummary Group, IReadOnlyList<GroupMemberView> Members, IReadOnlyList<GroupGrantView> GrantedOn);
