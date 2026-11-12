namespace Poc.Domain.Common;

/// <summary>
/// Base for objects that appear in the authorisation graph or its audit. Carries the audit columns, the integer
/// concurrency token and the last ZedToken, and collects domain events until the repository writes them to the outbox.
/// </summary>
public abstract class AggregateRoot
{
    private readonly List<IDomainEvent> _events = [];

    public string Id { get; protected set; } = "";

    public DateTimeOffset CreatedAt { get; protected set; }

    public string CreatedBy { get; protected set; } = "";

    /// <summary>Integer concurrency token. SQLite has no native row version, so the repository increments it.</summary>
    public int RowVersion { get; private set; }

    /// <summary>The ZedToken from the last tuple write for this row, or <c>null</c> if none has landed yet.</summary>
    public string? AuthzToken { get; private set; }

    public IReadOnlyList<IDomainEvent> DomainEvents => _events;

    protected void Initialise(string id, string createdBy, DateTimeOffset now)
    {
        Id = id;
        CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? throw new DomainRuleException("CreatedBy is required.") : createdBy;
        CreatedAt = now;
    }

    protected void Raise(IDomainEvent domainEvent) => _events.Add(domainEvent);

    public void ClearDomainEvents() => _events.Clear();

    public void IncrementRowVersion() => RowVersion++;

    public void RecordAuthzToken(string token) => AuthzToken = token;

    protected static string RequireName(string? name, string what = "name")
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainRuleException($"The {what} must not be blank.");
        var trimmed = name.Trim();
        return trimmed.Length > 200 ? throw new DomainRuleException($"The {what} must be 200 characters or fewer.") : trimmed;
    }
}
