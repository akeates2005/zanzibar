using Poc.Application.Authorization;
using Poc.Application.Common;
using Poc.Domain.Access;
using Poc.Domain.Common;
using Poc.Domain.Directory;

namespace Poc.Application.Persistence;

public interface IRepository<T>
    where T : class
{
    Task<T?> FindAsync(string id, CancellationToken ct);

    void Add(T entity);

    void Remove(T entity);
}

/// <summary>
/// Saves every tracked change and the outbox rows for their domain events in one transaction. A lost concurrency
/// race throws <see cref="Common.ConflictException"/>. Never hold one open across a SpiceDB call: SQLite allows one writer.
/// </summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Grant audit. Uses its own short transaction so it can never commit someone else's half-finished work.</summary>
public interface IGrantAuditStore
{
    Task AddAsync(GrantAuditEntry entry, CancellationToken ct);

    Task UpdateAsync(GrantAuditEntry entry, CancellationToken ct);

    Task<IReadOnlyList<GrantAuditEntry>> ForResourceAsync(ResourceRef resource, CancellationToken ct);
}

/// <summary>What the application database knows about an object, for the service rules.</summary>
public sealed record ObjectInfo(string? RootCustomerId);

public interface IDirectoryStore
{
    Task<bool> UserExistsAsync(string userId, CancellationToken ct);

    Task<Group?> FindGroupAsync(string groupId, CancellationToken ct);

    /// <summary>
    /// <c>null</c> if no such object exists. The root customer is the customer the object sits under (itself for a
    /// customer); it is <c>null</c> for <c>platform:main</c>, which has none.
    /// </summary>
    Task<ObjectInfo?> DescribeAsync(ResourceRef resource, CancellationToken ct);
}

/// <summary>Records the ZedToken of the last tuple write on the row that owns it.</summary>
public interface IAuthzTokenStore
{
    Task StampAsync(ResourceRef resource, string zedToken, CancellationToken ct);
}

/// <summary>What reconciliation compares the engine against.</summary>
public interface IStructuralReferenceSource
{
    /// <summary>Every parent and owner reference in the application database, as the tuple it should have.</summary>
    Task<IReadOnlyList<Relationship>> ExpectedAsync(CancellationToken ct);

    /// <summary>Objects with unprocessed outbox messages. Their tuples are in flight, so they are not drift.</summary>
    Task<IReadOnlySet<ResourceRef>> PendingObjectsAsync(CancellationToken ct);
}

/// <summary>Information-level log of grants and revokes, kept as a port so Application has no logging dependency.</summary>
public interface IAccessLog
{
    void Applied(Actor actor, GrantAction action, Relationship relationship);

    void Failed(Actor actor, GrantAction action, Relationship relationship, Exception exception);

    void TokenStampFailed(ResourceRef resource, Exception exception);
}
