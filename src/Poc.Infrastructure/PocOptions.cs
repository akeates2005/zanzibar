using Poc.Infrastructure.SpiceDb;

namespace Poc.Infrastructure;

public sealed class PocOptions
{
    /// <summary>
    /// Name of the shared in-memory SQLite database. The default matches the PRD's
    /// <c>Data Source=poc;Mode=Memory;Cache=Shared</c>; tests use unique names so hosts in one process never collide.
    /// </summary>
    public string SqliteDatabaseName { get; set; } = "poc";

    public SpiceDbOptions SpiceDb { get; set; } = new();

    /// <summary>Path to <c>authz/schema.zed</c>, written to the engine at start-up.</summary>
    public string SchemaPath { get; set; } = Path.Combine("authz", "schema.zed");

    public TimeSpan OutboxFlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the watchdog checks that SpiceDB still holds its schema.</summary>
    public TimeSpan SchemaCheckInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait before retrying a failed rebuild after schema loss.</summary>
    public TimeSpan RecoveryRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A message that fails this many times (for reasons a retry will not fix) is left unprocessed with its error.</summary>
    public int MaxOutboxAttempts { get; set; } = 5;

    /// <summary>How long start-up waits for SpiceDB before giving up.</summary>
    public TimeSpan EngineStartupTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
