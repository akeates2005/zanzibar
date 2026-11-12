using Poc.Domain.Common;

namespace Poc.Domain.Commerce;

public enum SubscriptionStatus
{
    Active,
    Cancelled,
}

public sealed class Subscription : AggregateRoot
{
    private Subscription()
    {
    }

    public string WalletId { get; private set; } = "";

    public string ProductName { get; private set; } = "";

    public int Quantity { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    public static Subscription Create(
        string walletId, string productName, int quantity, string createdBy, DateTimeOffset now, string? id = null)
    {
        var subscription = new Subscription
        {
            WalletId = EntityId.Validate(walletId, "wallet id"),
            ProductName = RequireName(productName, "product name"),
            Quantity = RequireQuantity(quantity),
            Status = SubscriptionStatus.Active,
        };
        subscription.Initialise(id is null ? EntityId.New() : EntityId.Validate(id, "subscription id"), createdBy, now);
        subscription.Raise(new SubscriptionCreated(subscription.Id, subscription.WalletId, subscription.CreatedBy));
        return subscription;
    }

    public void ChangeQuantity(int quantity)
    {
        if (Status == SubscriptionStatus.Cancelled) throw new DomainRuleException("A cancelled subscription cannot change quantity.");
        Quantity = RequireQuantity(quantity);
    }

    public void Cancel()
    {
        if (Status == SubscriptionStatus.Cancelled) throw new DomainRuleException("The subscription is already cancelled.");
        Status = SubscriptionStatus.Cancelled;
    }

    public void Delete() => Raise(new SubscriptionDeleted(Id));

    private static int RequireQuantity(int quantity) =>
        quantity >= 1 ? quantity : throw new DomainRuleException("Quantity must be 1 or more.");
}
