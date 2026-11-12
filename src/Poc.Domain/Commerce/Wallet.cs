using Poc.Domain.Common;

namespace Poc.Domain.Commerce;

public sealed class Wallet : AggregateRoot
{
    private Wallet()
    {
    }

    public string DomainId { get; private set; } = "";

    public string Name { get; private set; } = "";

    /// <summary>Changes only through <see cref="TopUp"/>.</summary>
    public decimal Balance { get; private set; }

    public static Wallet Create(string domainId, string name, string createdBy, DateTimeOffset now, string? id = null)
    {
        var wallet = new Wallet
        {
            DomainId = EntityId.Validate(domainId, "domain id"),
            Name = RequireName(name),
        };
        wallet.Initialise(id is null ? EntityId.New() : EntityId.Validate(id, "wallet id"), createdBy, now);
        wallet.Raise(new WalletCreated(wallet.Id, wallet.DomainId));
        return wallet;
    }

    public void Rename(string name) => Name = RequireName(name);

    public void TopUp(decimal amount)
    {
        if (amount <= 0) throw new DomainRuleException("A top-up amount must be greater than zero.");
        Balance += amount;
    }

    /// <summary>
    /// Re-parents the wallet. That the target belongs to the same customer is checked by the application, which can
    /// see both domains; the aggregate only knows its own state.
    /// </summary>
    public void MoveTo(string targetDomainId)
    {
        EntityId.Validate(targetDomainId, "target domain id");
        if (targetDomainId == DomainId) throw new DomainRuleException("The wallet is already in that domain.");
        var from = DomainId;
        DomainId = targetDomainId;
        Raise(new WalletMoved(Id, from, targetDomainId));
    }

    public void Delete() => Raise(new WalletDeleted(Id));
}
