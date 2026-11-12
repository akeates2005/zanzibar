using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Domain.Commerce;
using Poc.Domain.Common;
using Poc.Domain.Directory;

namespace Poc.Application.Projection;

/// <summary>
/// What the engine must do for one domain event. <see cref="Updates"/> go in a single write request;
/// <see cref="Deletes"/> are filter deletes run afterwards. <see cref="Target"/> is the object the event is about: it
/// receives the ZedToken, and later messages for the same target wait if this one fails.
/// </summary>
public sealed record ProjectionPlan(
    ResourceRef Target,
    IReadOnlyList<RelationshipUpdate> Updates,
    IReadOnlyList<RelationshipFilter> Deletes);

/// <summary>Maps domain events to structural tuples (PRD: Tuple lifecycle). Pure, so it is unit-tested without an engine.</summary>
public static class EventTupleMapper
{
    private static readonly string[] AllResourceTypes =
    [
        PlatformSchema.Type, GroupSchema.Type, CustomerSchema.Type, DomainSchema.Type, WalletSchema.Type, SubscriptionSchema.Type,
    ];

    public static ProjectionPlan Map(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return domainEvent switch
        {
            CustomerCreated e => Create(Res(CustomerSchema.Type, e.CustomerId), CustomerSchema.Relations.Parent, PlatformSchema.Main),
            DomainCreated e => Create(Res(DomainSchema.Type, e.DomainId), DomainSchema.Relations.Parent, Res(CustomerSchema.Type, e.CustomerId)),
            WalletCreated e => Create(Res(WalletSchema.Type, e.WalletId), WalletSchema.Relations.Parent, Res(DomainSchema.Type, e.DomainId)),
            SubscriptionCreated e => Create(Res(SubscriptionSchema.Type, e.SubscriptionId), SubscriptionSchema.Relations.Parent, Res(WalletSchema.Type, e.WalletId)),
            GroupCreated e => Create(
                Res(GroupSchema.Type, e.GroupId),
                GroupSchema.Relations.Owner,
                e.OwnerType == GroupOwnerType.Platform ? PlatformSchema.Main : Res(CustomerSchema.Type, e.OwnerId)),
            WalletMoved e => Move(e),
            CustomerDeleted e => DeleteAllFor(Res(CustomerSchema.Type, e.CustomerId)),
            DomainDeleted e => DeleteAllFor(Res(DomainSchema.Type, e.DomainId)),
            WalletDeleted e => DeleteAllFor(Res(WalletSchema.Type, e.WalletId)),
            SubscriptionDeleted e => DeleteAllFor(Res(SubscriptionSchema.Type, e.SubscriptionId)),
            GroupDeleted e => DeleteGroup(e),
            _ => throw new NotSupportedException($"No tuple mapping exists for domain event {domainEvent.GetType().Name}."),
        };
    }

    private static ResourceRef Res(string type, string id) => new(type, id);

    private static ProjectionPlan Create(ResourceRef resource, string relation, ResourceRef parent) => new(
        resource,
        [RelationshipUpdate.Touch(new Relationship(resource, relation, new SubjectRef(parent.Type, parent.Id)))],
        []);

    private static ProjectionPlan Move(WalletMoved e)
    {
        var wallet = Res(WalletSchema.Type, e.WalletId);
        var from = new Relationship(wallet, WalletSchema.Relations.Parent, new SubjectRef(DomainSchema.Type, e.FromDomainId));
        var to = new Relationship(wallet, WalletSchema.Relations.Parent, new SubjectRef(DomainSchema.Type, e.ToDomainId));
        return new ProjectionPlan(wallet, [RelationshipUpdate.Delete(from), RelationshipUpdate.Touch(to)], []);
    }

    /// <summary>Every relationship where the object is the resource. Deletion with children is refused earlier (409).</summary>
    private static ProjectionPlan DeleteAllFor(ResourceRef resource) =>
        new(resource, [], [new RelationshipFilter(resource.Type, resource.Id)]);

    /// <summary>
    /// The group's own relationships, then every relationship whose subject is <c>group:{id}#member</c>, one delete per
    /// resource type. Replaying is harmless because deletes are idempotent.
    /// </summary>
    private static ProjectionPlan DeleteGroup(GroupDeleted e)
    {
        var group = Res(GroupSchema.Type, e.GroupId);
        var deletes = new List<RelationshipFilter> { new(GroupSchema.Type, group.Id) };
        deletes.AddRange(AllResourceTypes.Select(type => new RelationshipFilter(
            type, Subject: new SubjectFilter(GroupSchema.Type, e.GroupId, GroupSchema.Relations.Member))));
        return new ProjectionPlan(group, [], deletes);
    }
}
