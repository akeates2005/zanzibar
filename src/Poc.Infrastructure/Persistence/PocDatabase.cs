using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Poc.Infrastructure.Persistence;

/// <summary>
/// Owns the in-memory SQLite database. It exists only while at least one connection is open, so one keep-alive
/// connection is opened at start-up and held for the life of the process; every <see cref="PocDbContext"/> opens its
/// own connection to the same named database.
/// </summary>
public sealed class PocDatabase(PocOptions options) : IDisposable
{
    private SqliteConnection? _keepAlive;

    public string ConnectionString { get; } = $"Data Source={options.SqliteDatabaseName};Mode=Memory;Cache=Shared;Foreign Keys=True";

    /// <summary>Opens the keep-alive connection (idempotent) and creates the schema if it does not exist.</summary>
    public async Task EnsureCreatedAsync(IDbContextFactory<PocDbContext> factory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_keepAlive is null)
        {
            _keepAlive = new SqliteConnection(ConnectionString);
            await _keepAlive.OpenAsync(ct).ConfigureAwait(false);
        }

        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await context.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
    }

    private static readonly string[] ClearStatements =
    [
        "DELETE FROM \"Subscriptions\"", "DELETE FROM \"Wallets\"", "DELETE FROM \"Domains\"", "DELETE FROM \"Customers\"",
        "DELETE FROM \"Groups\"", "DELETE FROM \"Users\"", "DELETE FROM \"OutboxMessages\"", "DELETE FROM \"GrantAudit\"",
        "DELETE FROM sqlite_sequence",
    ];

    /// <summary>Deletes every row in every table, children first, and resets identity counters.</summary>
    public static async Task ClearAsync(IDbContextFactory<PocDbContext> factory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(factory);
        await using var context = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var statement in ClearStatements)
            await context.Database.ExecuteSqlRawAsync(statement, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => _keepAlive?.Dispose();
}
