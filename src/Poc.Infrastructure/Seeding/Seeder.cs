using Poc.Application.Access;
using Poc.Application.Common;
using Poc.Application.Persistence;
using Poc.Domain.Commerce;
using Poc.Domain.Directory;

namespace Poc.Infrastructure.Seeding;

/// <summary>
/// Seeds the application database, then issues the seed grants and memberships through the real commands with the
/// system actor, so the audit is complete from the first row.
/// </summary>
public sealed class Seeder(
    IRepository<User> users,
    IRepository<Customer> customers,
    IRepository<CustomerDomain> domains,
    IRepository<Wallet> wallets,
    IRepository<Subscription> subscriptions,
    IRepository<Group> groups,
    IUnitOfWork unitOfWork,
    AccessService access,
    TimeProvider time)
{
    /// <summary>One transaction: every row and its outbox messages commit together.</summary>
    public async Task SeedDatabaseAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        const string by = Actor.SystemUserId;

        foreach (var (id, name) in SeedData.Users) users.Add(User.Create(id, name, SeedUsers.EmailFor(id)));
        foreach (var (id, name) in SeedData.Customers) customers.Add(Customer.Create(name, by, now, id));
        foreach (var (id, customerId, name) in SeedData.Domains) domains.Add(CustomerDomain.Create(customerId, name, by, now, id));
        foreach (var (id, domainId, name, balance) in SeedData.Wallets)
        {
            var wallet = Wallet.Create(domainId, name, by, now, id);
            wallet.TopUp(balance);
            wallets.Add(wallet);
        }

        foreach (var (id, walletId, product, quantity) in SeedData.Subscriptions)
            subscriptions.Add(Subscription.Create(walletId, product, quantity, by, now, id));
        foreach (var (id, platform, ownerId, name) in SeedData.Groups)
            groups.Add(Group.Create(platform ? GroupOwnerType.Platform : GroupOwnerType.Customer, ownerId, name, by, now, id));

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Must run after the outbox has been flushed, because the rules look up objects and groups in the engine.</summary>
    public async Task SeedGrantsAsync(CancellationToken ct)
    {
        // Order follows the PRD's table: platform, then each user's path. Memberships come before the grants that name their groups.
        foreach (var (groupId, member) in SeedData.Memberships.Where(m => m.GroupId == "support-team"))
            await access.AddGroupMemberAsync(Actor.System, groupId, member, ct).ConfigureAwait(false);
        foreach (var grant in SeedData.Grants.Take(2))
            await access.GrantAsync(Actor.System, new GrantTarget(grant.Resource, grant.Relation, grant.Subject), ct).ConfigureAwait(false);

        foreach (var (groupId, member) in SeedData.Memberships.Where(m => m.GroupId != "support-team"))
            await access.AddGroupMemberAsync(Actor.System, groupId, member, ct).ConfigureAwait(false);
        foreach (var grant in SeedData.Grants.Skip(2))
            await access.GrantAsync(Actor.System, new GrantTarget(grant.Resource, grant.Relation, grant.Subject), ct).ConfigureAwait(false);
    }
}
