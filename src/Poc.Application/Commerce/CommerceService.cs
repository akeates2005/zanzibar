using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;
using Poc.Application.Persistence;
using Poc.Application.Projection;
using Poc.Application.Queries;
using Poc.Domain.Commerce;

namespace Poc.Application.Commerce;

/// <param name="AuthorisationPending">
/// The change is saved but the engine could not be told yet; the object stays invisible until the background worker
/// catches up. The API reports this as 202.
/// </param>
public sealed record CommandResult<T>(T Value, bool AuthorisationPending);

/// <summary>
/// Customers, domains, wallets and subscriptions. Commerce never writes tuples and never asks the engine anything: the
/// endpoint filter has already checked the caller's permission, and structural changes reach the engine only through
/// the outbox (PRD rule 6). Each structural command saves, then flushes the outbox, and reports whether it all landed.
/// </summary>
public sealed class CommerceService(
    IRepository<Customer> customers,
    IRepository<CustomerDomain> domains,
    IRepository<Wallet> wallets,
    IRepository<Subscription> subscriptions,
    IUnitOfWork unitOfWork,
    IAuthorizationProjector projector,
    ICommerceQueries queries,
    TimeProvider time)
{
    public const string CrossCustomerMove = "cross_customer_move";

    // ---- Customers ----------------------------------------------------------------------------------------------

    public Task<CommandResult<Customer>> CreateCustomerAsync(Actor actor, string name, CancellationToken ct) =>
        AddAsync(customers, Customer.Create(name, actor.UserId, time.GetUtcNow()), ct);

    public Task<Customer> RenameCustomerAsync(string id, string name, CancellationToken ct) =>
        ChangeAsync(customers, CustomerSchema.Type, id, c => c.Rename(name), ct);

    public Task<bool> DeleteCustomerAsync(string id, CancellationToken ct) =>
        DeleteAsync(customers, new ResourceRef(CustomerSchema.Type, id), id, c => c.Delete(), ct);

    // ---- Domains ------------------------------------------------------------------------------------------------

    public async Task<CommandResult<CustomerDomain>> CreateDomainAsync(Actor actor, string customerId, string name, CancellationToken ct)
    {
        _ = await customers.FindAsync(customerId, ct).ConfigureAwait(false) ?? throw NotFound(CustomerSchema.Type, customerId);
        return await AddAsync(domains, CustomerDomain.Create(customerId, name, actor.UserId, time.GetUtcNow()), ct).ConfigureAwait(false);
    }

    public Task<CustomerDomain> RenameDomainAsync(string id, string name, CancellationToken ct) =>
        ChangeAsync(domains, DomainSchema.Type, id, d => d.Rename(name), ct);

    public Task<bool> DeleteDomainAsync(string id, CancellationToken ct) =>
        DeleteAsync(domains, new ResourceRef(DomainSchema.Type, id), id, d => d.Delete(), ct);

    // ---- Wallets ------------------------------------------------------------------------------------------------

    public async Task<CommandResult<Wallet>> CreateWalletAsync(Actor actor, string domainId, string name, CancellationToken ct)
    {
        _ = await domains.FindAsync(domainId, ct).ConfigureAwait(false) ?? throw NotFound(DomainSchema.Type, domainId);
        return await AddAsync(wallets, Wallet.Create(domainId, name, actor.UserId, time.GetUtcNow()), ct).ConfigureAwait(false);
    }

    public Task<Wallet> RenameWalletAsync(string id, string name, CancellationToken ct) =>
        ChangeAsync(wallets, WalletSchema.Type, id, w => w.Rename(name), ct);

    public Task<Wallet> TopUpWalletAsync(string id, decimal amount, CancellationToken ct) =>
        ChangeAsync(wallets, WalletSchema.Type, id, w => w.TopUp(amount), ct);

    /// <summary>
    /// Moves a wallet to another domain under the same customer. Moving across customers is refused (422) because the
    /// wallet's group grants would then point at another customer's groups. The caller must already hold
    /// <c>create_wallet</c> on the target; the endpoint checks that.
    /// </summary>
    public async Task<CommandResult<Wallet>> MoveWalletAsync(string id, string targetDomainId, CancellationToken ct)
    {
        var wallet = await wallets.FindAsync(id, ct).ConfigureAwait(false) ?? throw NotFound(WalletSchema.Type, id);
        var current = await domains.FindAsync(wallet.DomainId, ct).ConfigureAwait(false) ?? throw NotFound(DomainSchema.Type, wallet.DomainId);
        var target = await domains.FindAsync(targetDomainId, ct).ConfigureAwait(false) ?? throw NotFound(DomainSchema.Type, targetDomainId);
        if (current.CustomerId != target.CustomerId)
            throw new RuleViolationException(CrossCustomerMove, "A wallet can move only to a domain under the same customer.");

        wallet.MoveTo(targetDomainId);
        var pending = await SaveAndFlushAsync(ct).ConfigureAwait(false);
        return new CommandResult<Wallet>(wallet, pending);
    }

    public Task<bool> DeleteWalletAsync(string id, CancellationToken ct) =>
        DeleteAsync(wallets, new ResourceRef(WalletSchema.Type, id), id, w => w.Delete(), ct);

    // ---- Subscriptions ------------------------------------------------------------------------------------------

    public async Task<CommandResult<Subscription>> CreateSubscriptionAsync(
        Actor actor, string walletId, string productName, int quantity, CancellationToken ct)
    {
        _ = await wallets.FindAsync(walletId, ct).ConfigureAwait(false) ?? throw NotFound(WalletSchema.Type, walletId);
        return await AddAsync(subscriptions, Subscription.Create(walletId, productName, quantity, actor.UserId, time.GetUtcNow()), ct).ConfigureAwait(false);
    }

    public Task<Subscription> ChangeSubscriptionQuantityAsync(string id, int quantity, CancellationToken ct) =>
        ChangeAsync(subscriptions, SubscriptionSchema.Type, id, s => s.ChangeQuantity(quantity), ct);

    public Task<Subscription> CancelSubscriptionAsync(string id, CancellationToken ct) =>
        ChangeAsync(subscriptions, SubscriptionSchema.Type, id, s => s.Cancel(), ct);

    public Task<bool> DeleteSubscriptionAsync(string id, CancellationToken ct) =>
        DeleteAsync(subscriptions, new ResourceRef(SubscriptionSchema.Type, id), id, s => s.Delete(), ct);

    // ---- Shared -------------------------------------------------------------------------------------------------

    private static NotFoundException NotFound(string type, string id) => new($"{type}:{id}");

    private async Task<CommandResult<T>> AddAsync<T>(IRepository<T> repository, T entity, CancellationToken ct)
        where T : class
    {
        repository.Add(entity);
        var pending = await SaveAndFlushAsync(ct).ConfigureAwait(false);
        return new CommandResult<T>(entity, pending);
    }

    /// <summary>For changes that raise no authorisation event (renames, top-ups, quantity, cancellation): save only.</summary>
    private async Task<T> ChangeAsync<T>(IRepository<T> repository, string type, string id, Action<T> change, CancellationToken ct)
        where T : class
    {
        var entity = await repository.FindAsync(id, ct).ConfigureAwait(false) ?? throw NotFound(type, id);
        change(entity);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity;
    }

    private async Task<bool> DeleteAsync<T>(IRepository<T> repository, ResourceRef resource, string id, Action<T> raiseDeleted, CancellationToken ct)
        where T : class
    {
        var entity = await repository.FindAsync(id, ct).ConfigureAwait(false) ?? throw NotFound(resource.Type, id);
        // Deleting an object that still has children is refused, so a child can never outlive the parent its inherited
        // admins come from (PRD: Aggregates and events).
        if (await queries.HasChildrenAsync(resource, ct).ConfigureAwait(false))
            throw new ConflictException($"{resource.Type} '{id}' still has children and cannot be deleted.");
        raiseDeleted(entity);
        repository.Remove(entity);
        return await SaveAndFlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Saves the aggregate and its outbox rows, then flushes. Returns true when authorisation is still pending.</summary>
    private async Task<bool> SaveAndFlushAsync(CancellationToken ct)
    {
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        var outcome = await projector.FlushAsync(ct).ConfigureAwait(false);
        return !outcome.FullyApplied;
    }
}
