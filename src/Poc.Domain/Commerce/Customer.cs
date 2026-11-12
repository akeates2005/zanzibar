using Poc.Domain.Common;

namespace Poc.Domain.Commerce;

/// <summary>Root aggregate. Its parent is always <c>platform:main</c>.</summary>
public sealed class Customer : AggregateRoot
{
    private Customer()
    {
    }

    public string Name { get; private set; } = "";

    public static Customer Create(string name, string createdBy, DateTimeOffset now, string? id = null)
    {
        var customer = new Customer { Name = RequireName(name) };
        customer.Initialise(id is null ? EntityId.New() : EntityId.Validate(id, "customer id"), createdBy, now);
        customer.Raise(new CustomerCreated(customer.Id));
        return customer;
    }

    public void Rename(string name) => Name = RequireName(name);

    public void Delete() => Raise(new CustomerDeleted(Id));
}
