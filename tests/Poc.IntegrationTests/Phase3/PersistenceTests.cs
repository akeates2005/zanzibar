using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Common;
using Poc.Application.Persistence;
using Poc.Domain.Commerce;
using Poc.Infrastructure.Persistence;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Phase3;

/// <summary>The application database on in-memory SQLite: transactions, outbox atomicity, concurrency and constraints.</summary>
public sealed class PersistenceTests : PocTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    protected override bool SeedOnStartup => false;

    private Task SaveAsync(params Action<IServiceProvider>[] changes) => InScopeAsync(async sp =>
    {
        foreach (var change in changes) change(sp);
        await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
    });

    private static Customer NewCustomer(string id = "acme") => Customer.Create("Acme", "alice", Now, id);

    [Fact]
    public async Task An_aggregate_and_its_outbox_message_commit_together()
    {
        await SaveAsync(sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer()));

        Assert.Equal(1, await QueryAsync(db => db.Customers.CountAsync(Ct)));
        var message = Assert.Single(await QueryAsync(db => db.OutboxMessages.AsNoTracking().ToListAsync(Ct)));
        Assert.Equal("CustomerCreated", message.Type);
        Assert.Contains("\"customerId\":\"acme\"", message.Payload, StringComparison.Ordinal);
        Assert.Null(message.ProcessedAt);
        Assert.Equal(0, message.Attempts);
    }

    [Fact]
    public async Task A_failed_save_leaves_neither_the_row_nor_its_outbox_message()
    {
        // The domain's customer is valid but its sibling violates a foreign key, so the whole transaction must roll back.
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(
            sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer("globex")),
            sp => sp.GetRequiredService<IRepository<CustomerDomain>>().Add(CustomerDomain.Create("no-such-customer", "Orphan", "alice", Now))));

        Assert.Equal(0, await QueryAsync(db => db.Customers.CountAsync(Ct)));
        Assert.Equal(0, await QueryAsync(db => db.OutboxMessages.CountAsync(Ct)));
    }

    [Fact]
    public async Task Domain_events_are_cleared_after_a_save_so_a_second_save_does_not_duplicate_them()
    {
        await InScopeAsync(async sp =>
        {
            var customer = NewCustomer();
            sp.GetRequiredService<IRepository<Customer>>().Add(customer);
            var uow = sp.GetRequiredService<IUnitOfWork>();
            await uow.SaveChangesAsync(Ct);
            customer.Rename("Acme Limited");
            await uow.SaveChangesAsync(Ct);
            await uow.SaveChangesAsync(Ct);
        });

        Assert.Equal(1, await QueryAsync(db => db.OutboxMessages.CountAsync(Ct)));
    }

    [Fact]
    public async Task Deleting_an_aggregate_still_writes_its_deleted_event()
    {
        await SaveAsync(sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer()));

        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<IRepository<Customer>>();
            var customer = (await repo.FindAsync("acme", Ct))!;
            customer.Delete();
            repo.Remove(customer);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        });

        Assert.Equal(0, await QueryAsync(db => db.Customers.CountAsync(Ct)));
        Assert.Equal(["CustomerCreated", "CustomerDeleted"], (await QueryAsync(db => db.OutboxMessages.OrderBy(m => m.Sequence).Select(m => m.Type).ToListAsync(Ct))).ToArray());
    }

    [Fact]
    public async Task Outbox_sequence_numbers_increase_in_commit_order()
    {
        await SaveAsync(sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer("one")));
        await SaveAsync(sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer("two")));
        await SaveAsync(sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer("three")));

        var sequences = await QueryAsync(db => db.OutboxMessages.OrderBy(m => m.Sequence).Select(m => m.Sequence).ToListAsync(Ct));
        Assert.Equal(sequences.Order(), sequences);
        Assert.Equal(3, sequences.Distinct().Count());
    }

    [Fact]
    public async Task Foreign_keys_are_enforced()
    {
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(
            sp => sp.GetRequiredService<IRepository<Wallet>>().Add(Wallet.Create("no-such-domain", "Ops", "alice", Now))));
    }

    [Fact]
    public async Task A_parent_with_children_cannot_be_removed_at_the_database_level()
    {
        await SaveAsync(
            sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer()),
            sp => sp.GetRequiredService<IRepository<CustomerDomain>>().Add(CustomerDomain.Create("acme", "UK", "alice", Now, "acme-uk")));

        await Assert.ThrowsAsync<ConflictException>(() => InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<IRepository<Customer>>();
            repo.Remove((await repo.FindAsync("acme", Ct))!);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        }));
    }

    [Fact]
    public async Task A_domain_name_is_unique_within_its_customer_but_may_repeat_across_customers()
    {
        await SaveAsync(
            sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer("acme")),
            sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer("globex")),
            sp => sp.GetRequiredService<IRepository<CustomerDomain>>().Add(CustomerDomain.Create("acme", "Main", "alice", Now, "acme-main")));

        await SaveAsync(sp => sp.GetRequiredService<IRepository<CustomerDomain>>().Add(CustomerDomain.Create("globex", "Main", "alice", Now, "globex-main")));
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(
            sp => sp.GetRequiredService<IRepository<CustomerDomain>>().Add(CustomerDomain.Create("acme", "Main", "alice", Now, "acme-main-2"))));
    }

    [Fact]
    public async Task Row_version_increments_on_every_modification()
    {
        await SaveAsync(sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer()));

        for (var expected = 1; expected <= 3; expected++)
        {
            await InScopeAsync(async sp =>
            {
                var customer = (await sp.GetRequiredService<IRepository<Customer>>().FindAsync("acme", Ct))!;
                customer.Rename($"Acme {expected}");
                await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
            });
            Assert.Equal(expected, await QueryAsync(db => db.Customers.Where(c => c.Id == "acme").Select(c => c.RowVersion).SingleAsync(Ct)));
        }
    }

    [Fact]
    public async Task A_stale_write_loses_with_a_conflict_instead_of_overwriting()
    {
        await SaveAsync(sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer()));

        await using var first = Host.Scope();
        await using var second = Host.Scope();
        var a = (await first.ServiceProvider.GetRequiredService<IRepository<Customer>>().FindAsync("acme", Ct))!;
        var b = (await second.ServiceProvider.GetRequiredService<IRepository<Customer>>().FindAsync("acme", Ct))!;

        a.Rename("First writer");
        await first.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        b.Rename("Second writer, working from stale data");
        await Assert.ThrowsAsync<ConflictException>(() => second.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct));

        Assert.Equal("First writer", await QueryAsync(db => db.Customers.Where(c => c.Id == "acme").Select(c => c.Name).SingleAsync(Ct)));
    }

    [Fact]
    public async Task Stamping_an_authz_token_does_not_bump_the_row_version_or_disturb_a_concurrent_edit()
    {
        await SaveAsync(sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer()));
        await using var editor = Host.Scope();
        var customer = (await editor.ServiceProvider.GetRequiredService<IRepository<Customer>>().FindAsync("acme", Ct))!;

        await Host.Get<IAuthzTokenStore>().StampAsync(new Poc.Application.Authorization.ResourceRef("customer", "acme"), "token-1", Ct);
        customer.Rename("Edited after the stamp");
        await editor.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct); // must not conflict

        var row = await QueryAsync(db => db.Customers.AsNoTracking().SingleAsync(Ct));
        Assert.Equal("Edited after the stamp", row.Name);
        Assert.Equal(1, row.RowVersion);
        Assert.Equal("token-1", row.AuthzToken);
    }

    [Fact]
    public async Task Stamping_a_missing_row_is_harmless()
    {
        await Host.Get<IAuthzTokenStore>().StampAsync(new Poc.Application.Authorization.ResourceRef("wallet", "gone"), "t", Ct);
        await Host.Get<IAuthzTokenStore>().StampAsync(new Poc.Application.Authorization.ResourceRef("platform", "main"), "t", Ct);
    }

    [Fact]
    public async Task Concurrent_writers_and_readers_on_the_shared_in_memory_database_do_not_fail()
    {
        // SQLite allows one writer at a time and the shared cache locks by table. This is the PRD's stated risk.
        var writers = Enumerable.Range(0, 40).Select(i => Task.Run(() => SaveAsync(
            sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer($"c{i}")))));
        var readers = Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            for (var n = 0; n < 5; n++) await QueryAsync(db => db.Customers.AsNoTracking().ToListAsync(Ct));
        }));
        var flushes = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            for (var n = 0; n < 5; n++) await QueryAsync(db => db.OutboxMessages.AsNoTracking().Where(m => m.ProcessedAt == null).CountAsync(Ct));
        }));

        await Task.WhenAll(writers.Concat(readers).Concat(flushes));

        Assert.Equal(40, await QueryAsync(db => db.Customers.CountAsync(Ct)));
        Assert.Equal(40, await QueryAsync(db => db.OutboxMessages.CountAsync(Ct)));
    }

    [Fact]
    public async Task The_database_survives_all_scopes_closing_because_of_the_keep_alive_connection()
    {
        await SaveAsync(sp => sp.GetRequiredService<IRepository<Customer>>().Add(NewCustomer()));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(1, await QueryAsync(db => db.Customers.CountAsync(Ct)));
    }
}
