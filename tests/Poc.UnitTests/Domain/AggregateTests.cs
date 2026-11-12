using Poc.Domain.Commerce;
using Poc.Domain.Common;
using Poc.Domain.Directory;

namespace Poc.UnitTests.Domain;

public sealed class AggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Creating_each_aggregate_raises_exactly_one_creation_event_carrying_its_parent()
    {
        var customer = Customer.Create("Acme", "alice", Now, "acme");
        var domain = CustomerDomain.Create("acme", "UK", "alice", Now, "acme-uk");
        var wallet = Wallet.Create("acme-uk", "Ops", "alice", Now, "uk-ops");
        var subscription = Subscription.Create("uk-ops", "M365", 5, "carol", Now, "m365");
        var group = Group.Create(GroupOwnerType.Customer, "acme", "UK IT", "alice", Now, "uk-it");

        Assert.Equal(new CustomerCreated("acme"), Assert.Single(customer.DomainEvents));
        Assert.Equal(new DomainCreated("acme-uk", "acme"), Assert.Single(domain.DomainEvents));
        Assert.Equal(new WalletCreated("uk-ops", "acme-uk"), Assert.Single(wallet.DomainEvents));
        Assert.Equal(new SubscriptionCreated("m365", "uk-ops", "carol"), Assert.Single(subscription.DomainEvents));
        Assert.Equal(new GroupCreated("uk-it", GroupOwnerType.Customer, "acme"), Assert.Single(group.DomainEvents));
    }

    [Fact]
    public void Generated_ids_are_version_7_guids_in_lower_case_without_dashes()
    {
        var customer = Customer.Create("Acme", "alice", Now);

        Assert.Equal(32, customer.Id.Length);
        Assert.Matches("^[0-9a-f]{32}$", customer.Id);
        Assert.Equal('7', customer.Id[12]); // the version nibble of a version 7 GUID
        Assert.NotEqual(customer.Id, Customer.Create("Acme", "alice", Now).Id);
    }

    [Fact]
    public void Renames_top_ups_quantity_changes_and_cancellation_raise_no_authorisation_event()
    {
        var customer = Customer.Create("Acme", "alice", Now, "acme");
        var wallet = Wallet.Create("acme-uk", "Ops", "alice", Now, "uk-ops");
        var subscription = Subscription.Create("uk-ops", "M365", 5, "alice", Now, "m365");
        customer.ClearDomainEvents();
        wallet.ClearDomainEvents();
        subscription.ClearDomainEvents();

        customer.Rename("Acme Limited");
        wallet.Rename("UK Ops");
        wallet.TopUp(10m);
        subscription.ChangeQuantity(9);
        subscription.Cancel();

        Assert.Empty(customer.DomainEvents);
        Assert.Empty(wallet.DomainEvents);
        Assert.Empty(subscription.DomainEvents);
    }

    [Fact]
    public void Moving_a_wallet_raises_WalletMoved_with_both_domains_and_updates_its_parent()
    {
        var wallet = Wallet.Create("acme-uk", "Ops", "alice", Now, "uk-ops");
        wallet.ClearDomainEvents();

        wallet.MoveTo("acme-ie");

        Assert.Equal("acme-ie", wallet.DomainId);
        Assert.Equal(new WalletMoved("uk-ops", "acme-uk", "acme-ie"), Assert.Single(wallet.DomainEvents));
    }

    [Fact]
    public void Moving_a_wallet_to_its_current_domain_is_refused()
    {
        var wallet = Wallet.Create("acme-uk", "Ops", "alice", Now, "uk-ops");
        Assert.Throws<DomainRuleException>(() => wallet.MoveTo("acme-uk"));
    }

    [Fact]
    public void Deleting_raises_the_matching_deleted_event()
    {
        var customer = Customer.Create("Acme", "a", Now, "acme");
        var domain = CustomerDomain.Create("acme", "UK", "a", Now, "acme-uk");
        var wallet = Wallet.Create("acme-uk", "Ops", "a", Now, "uk-ops");
        var subscription = Subscription.Create("uk-ops", "M365", 1, "a", Now, "m365");
        var group = Group.Create(GroupOwnerType.Platform, "main", "Support", "a", Now, "support");
        foreach (var a in new AggregateRoot[] { customer, domain, wallet, subscription, group }) a.ClearDomainEvents();

        customer.Delete();
        domain.Delete();
        wallet.Delete();
        subscription.Delete();
        group.Delete();

        Assert.Equal(new CustomerDeleted("acme"), Assert.Single(customer.DomainEvents));
        Assert.Equal(new DomainDeleted("acme-uk"), Assert.Single(domain.DomainEvents));
        Assert.Equal(new WalletDeleted("uk-ops"), Assert.Single(wallet.DomainEvents));
        Assert.Equal(new SubscriptionDeleted("m365"), Assert.Single(subscription.DomainEvents));
        Assert.Equal(new GroupDeleted("support"), Assert.Single(group.DomainEvents));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_top_up_must_be_positive(int amount)
    {
        var wallet = Wallet.Create("acme-uk", "Ops", "a", Now, "uk-ops");
        Assert.Throws<DomainRuleException>(() => wallet.TopUp(amount));
        Assert.Equal(0m, wallet.Balance);
    }

    [Fact]
    public void Balance_changes_only_through_top_up()
    {
        var wallet = Wallet.Create("acme-uk", "Ops", "a", Now, "uk-ops");
        wallet.TopUp(25.50m);
        wallet.TopUp(4.50m);
        Assert.Equal(30m, wallet.Balance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Subscription_quantity_must_be_one_or_more(int quantity)
    {
        Assert.Throws<DomainRuleException>(() => Subscription.Create("uk-ops", "M365", quantity, "a", Now));
        var subscription = Subscription.Create("uk-ops", "M365", 1, "a", Now);
        Assert.Throws<DomainRuleException>(() => subscription.ChangeQuantity(quantity));
    }

    [Fact]
    public void A_cancelled_subscription_cannot_be_cancelled_again_or_change_quantity()
    {
        var subscription = Subscription.Create("uk-ops", "M365", 2, "a", Now);
        subscription.Cancel();

        Assert.Equal(SubscriptionStatus.Cancelled, subscription.Status);
        Assert.Throws<DomainRuleException>(() => subscription.Cancel());
        Assert.Throws<DomainRuleException>(() => subscription.ChangeQuantity(3));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Names_must_not_be_blank(string name)
    {
        Assert.Throws<DomainRuleException>(() => Customer.Create(name, "a", Now));
        Assert.Throws<DomainRuleException>(() => CustomerDomain.Create("acme", name, "a", Now));
        Assert.Throws<DomainRuleException>(() => Wallet.Create("acme-uk", name, "a", Now));
        Assert.Throws<DomainRuleException>(() => Subscription.Create("uk-ops", name, 1, "a", Now));
        Assert.Throws<DomainRuleException>(() => Group.Create(GroupOwnerType.Customer, "acme", name, "a", Now));
    }

    [Fact]
    public void A_creator_is_mandatory_so_every_row_records_who_made_it()
    {
        Assert.Throws<DomainRuleException>(() => Customer.Create("Acme", " ", Now));
    }

    [Fact]
    public void A_platform_group_must_be_owned_by_platform_main()
    {
        Assert.Throws<DomainRuleException>(() => Group.Create(GroupOwnerType.Platform, "acme", "Support", "a", Now));
        Assert.Equal("main", Group.Create(GroupOwnerType.Platform, "main", "Support", "a", Now).OwnerId);
    }

    [Fact]
    public void Row_version_starts_at_zero_and_increments_only_when_asked()
    {
        var customer = Customer.Create("Acme", "a", Now, "acme");
        Assert.Equal(0, customer.RowVersion);
        customer.IncrementRowVersion();
        customer.IncrementRowVersion();
        Assert.Equal(2, customer.RowVersion);
    }

    [Fact]
    public void Users_need_a_display_name_and_a_plausible_email()
    {
        Assert.Throws<DomainRuleException>(() => User.Create("bob", "", "bob@x.test"));
        Assert.Throws<DomainRuleException>(() => User.Create("bob", "Bob", "not-an-email"));
        Assert.Equal("bob", User.Create("bob", "Bob", "bob@x.test").Id);
    }
}
