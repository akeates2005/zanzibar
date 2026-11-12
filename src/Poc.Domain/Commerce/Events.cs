using Poc.Domain.Common;

namespace Poc.Domain.Commerce;

// Every structural change raises an event. Renames, top-ups, quantity changes and cancellation change no
// relationship, so they raise none (PRD: Aggregates and events).
public sealed record CustomerCreated(string CustomerId) : IDomainEvent;

public sealed record CustomerDeleted(string CustomerId) : IDomainEvent;

public sealed record DomainCreated(string DomainId, string CustomerId) : IDomainEvent;

public sealed record DomainDeleted(string DomainId) : IDomainEvent;

public sealed record WalletCreated(string WalletId, string DomainId) : IDomainEvent;

public sealed record WalletMoved(string WalletId, string FromDomainId, string ToDomainId) : IDomainEvent;

public sealed record WalletDeleted(string WalletId) : IDomainEvent;

/// <summary><paramref name="CreatedBy"/> drives the default-grant policy for spenders who are not admins.</summary>
public sealed record SubscriptionCreated(string SubscriptionId, string WalletId, string CreatedBy) : IDomainEvent;

public sealed record SubscriptionDeleted(string SubscriptionId) : IDomainEvent;
