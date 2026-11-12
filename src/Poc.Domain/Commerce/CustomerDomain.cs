using Poc.Domain.Common;

namespace Poc.Domain.Commerce;

/// <summary>
/// The PRD's "domain" aggregate. Named <c>CustomerDomain</c> because a type called <c>Domain</c> inside the
/// <c>Poc.Domain</c> namespace is ambiguous with the namespace itself (DECISIONS.md).
/// </summary>
public sealed class CustomerDomain : AggregateRoot
{
    private CustomerDomain()
    {
    }

    public string CustomerId { get; private set; } = "";

    public string Name { get; private set; } = "";

    public static CustomerDomain Create(string customerId, string name, string createdBy, DateTimeOffset now, string? id = null)
    {
        var domain = new CustomerDomain
        {
            CustomerId = EntityId.Validate(customerId, "customer id"),
            Name = RequireName(name),
        };
        domain.Initialise(id is null ? EntityId.New() : EntityId.Validate(id, "domain id"), createdBy, now);
        domain.Raise(new DomainCreated(domain.Id, domain.CustomerId));
        return domain;
    }

    public void Rename(string name) => Name = RequireName(name);

    public void Delete() => Raise(new DomainDeleted(Id));
}
