using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Poc.Application.Common;
using Poc.Domain.Access;
using Poc.Domain.Commerce;
using Poc.Domain.Common;
using Poc.Domain.Directory;

namespace Poc.Infrastructure.Persistence;

public sealed class PocDbContext(DbContextOptions<PocDbContext> options, TimeProvider time) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<CustomerDomain> Domains => Set<CustomerDomain>();

    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GrantAuditEntry> GrantAudit => Set<GrantAuditEntry>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigureAggregate<Customer>(modelBuilder, "Customers", b => b.Property(x => x.Name).IsRequired());

        ConfigureAggregate<CustomerDomain>(modelBuilder, "Domains", b =>
        {
            b.Property(x => x.Name).IsRequired();
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.CustomerId, x.Name }).IsUnique(); // name unique within the customer
        });

        ConfigureAggregate<Wallet>(modelBuilder, "Wallets", b =>
        {
            b.Property(x => x.Name).IsRequired();
            b.HasOne<CustomerDomain>().WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Restrict);
        });

        ConfigureAggregate<Subscription>(modelBuilder, "Subscriptions", b =>
        {
            b.Property(x => x.ProductName).IsRequired();
            b.Property(x => x.Status).HasConversion<string>();
            b.HasOne<Wallet>().WithMany().HasForeignKey(x => x.WalletId).OnDelete(DeleteBehavior.Restrict);
        });

        ConfigureAggregate<Group>(modelBuilder, "Groups", b =>
        {
            b.Property(x => x.Name).IsRequired();
            b.Property(x => x.OwnerType).HasConversion<string>();
            b.HasIndex(x => new { x.OwnerType, x.OwnerId });
        });

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.HasKey(x => x.Id);
            b.Property(x => x.DisplayName).IsRequired();
            b.Property(x => x.Email).IsRequired();
        });

        modelBuilder.Entity<GrantAuditEntry>(b =>
        {
            b.ToTable("GrantAudit");
            b.HasKey(x => x.Id);
            b.Property(x => x.Action).HasConversion<string>();
            b.Property(x => x.Status).HasConversion<string>();
            b.HasIndex(x => new { x.ResourceType, x.ResourceId });
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("OutboxMessages");
            b.HasKey(x => x.Sequence);
            b.Property(x => x.Type).IsRequired();
            b.Property(x => x.Payload).IsRequired();
            b.HasIndex(x => x.ProcessedAt);
        });
    }

    private static void ConfigureAggregate<T>(ModelBuilder modelBuilder, string table, Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T>> configure)
        where T : AggregateRoot
    {
        modelBuilder.Entity<T>(b =>
        {
            b.ToTable(table);
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Property(x => x.CreatedBy).IsRequired();
            b.Property(x => x.RowVersion).IsConcurrencyToken();
            configure(b);
        });
    }

    /// <summary>SQLite reports an immediate FK failure as 787 on insert but as a plain error 19 for ON DELETE RESTRICT.</summary>
    private static bool IsForeignKeyViolation(Exception? exception) =>
        exception is SqliteException { SqliteExtendedErrorCode: 787 }
        || (exception is SqliteException { SqliteErrorCode: 19 } sqlite
            && sqlite.Message.Contains("FOREIGN KEY", StringComparison.Ordinal));

    /// <summary>
    /// Saves the aggregates and the outbox rows for their domain events in one transaction, bumps the integer row
    /// version of modified aggregates, and translates database failures into application exceptions.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        var aggregates = ChangeTracker.Entries<AggregateRoot>().ToList();

        foreach (var entry in aggregates.Where(e => e.State == EntityState.Modified))
            entry.Entity.IncrementRowVersion();

        var now = time.GetUtcNow();
        foreach (var domainEvent in aggregates.SelectMany(e => e.Entity.DomainEvents))
        {
            var (type, payload) = OutboxEventSerializer.Serialize(domainEvent);
            OutboxMessages.Add(new OutboxMessage { Type = type, Payload = payload, CreatedAt = now });
        }

        int saved;
        try
        {
            saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConflictException("The object was changed by someone else. Reload it and try again.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
        {
            throw new ConflictException("An object with that name or id already exists.", ex);
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex.InnerException))
        {
            throw new ConflictException("The object refers to, or is referred to by, another object.", ex);
        }

        foreach (var entry in aggregates) entry.Entity.ClearDomainEvents();
        return saved;
    }
}
