using Poc.Domain.Common;

namespace Poc.Domain.Directory;

public enum GroupOwnerType
{
    Customer,
    Platform,
}

public sealed record GroupCreated(string GroupId, GroupOwnerType OwnerType, string OwnerId) : IDomainEvent;

public sealed record GroupDeleted(string GroupId) : IDomainEvent;

/// <summary>
/// Owned by one customer or by the platform. Membership lives in SpiceDB, not here. Because the owner never changes,
/// one customer's group can never be granted on another customer's objects (see the service rules).
/// </summary>
public sealed class Group : AggregateRoot
{
    /// <summary>The id of the single platform object.</summary>
    public const string PlatformOwnerId = "main";

    private Group()
    {
    }

    public GroupOwnerType OwnerType { get; private set; }

    public string OwnerId { get; private set; } = "";

    public string Name { get; private set; } = "";

    public static Group Create(
        GroupOwnerType ownerType, string ownerId, string name, string createdBy, DateTimeOffset now, string? id = null)
    {
        if (ownerType == GroupOwnerType.Platform && ownerId != PlatformOwnerId)
            throw new DomainRuleException($"A platform group's owner id must be '{PlatformOwnerId}'.");
        var group = new Group
        {
            OwnerType = ownerType,
            OwnerId = EntityId.Validate(ownerId, "owner id"),
            Name = RequireName(name),
        };
        group.Initialise(id is null ? EntityId.New() : EntityId.Validate(id, "group id"), createdBy, now);
        group.Raise(new GroupCreated(group.Id, ownerType, ownerId));
        return group;
    }

    public void Rename(string name) => Name = RequireName(name);

    public void Delete() => Raise(new GroupDeleted(Id));
}
