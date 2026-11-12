using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;
using Poc.Application.Persistence;
using Poc.Application.Projection;
using Poc.Application.Reconciliation;
using Poc.Domain.Access;
using Poc.Domain.Commerce;
using Poc.Infrastructure;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Phase3;

/// <summary>The outbox projector against a real SpiceDB, through the real repositories and unit of work.</summary>
public sealed class ProjectorTests : PocTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private Task AddWalletAsync(string id, string domainId = "acme-uk", string by = "alice") =>
        CommitAsync(sp => sp.GetRequiredService<IRepository<Wallet>>().Add(Wallet.Create(domainId, id, by, Now, id)));

    private async Task MutateAsync<T>(string id, Action<T> change)
        where T : class
    {
        await InScopeAsync(async sp =>
        {
            var entity = await sp.GetRequiredService<IRepository<T>>().FindAsync(id, Ct) ?? throw new InvalidOperationException(id);
            change(entity);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        });
    }

    private Task<List<Poc.Infrastructure.Persistence.OutboxMessage>> OutboxAsync() =>
        QueryAsync(db => db.OutboxMessages.AsNoTracking().OrderBy(m => m.Sequence).ToListAsync(Ct));

    // ---- Each event type reaches the engine -------------------------------------------------------------------

    [Fact]
    public async Task A_whole_new_branch_becomes_visible_to_inherited_admins_once_flushed()
    {
        await CommitAsync(sp =>
        {
            sp.GetRequiredService<IRepository<Customer>>().Add(Customer.Create("Initech", "priya", Now, "initech"));
            sp.GetRequiredService<IRepository<CustomerDomain>>().Add(CustomerDomain.Create("initech", "Main", "priya", Now, "initech-main"));
            sp.GetRequiredService<IRepository<Wallet>>().Add(Wallet.Create("initech-main", "Ops", "priya", Now, "initech-ops"));
            sp.GetRequiredService<IRepository<Subscription>>().Add(Subscription.Create("initech-ops", "Tools", 3, "priya", Now, "initech-tools"));
        });
        Assert.False(await CanAsync("priya", SubscriptionSchema.Permissions.Cancel, Res("subscription", "initech-tools"))); // not projected yet

        var outcome = await FlushAsync();

        Assert.True(outcome.FullyApplied);
        Assert.Equal(4, outcome.Processed);
        Assert.True(await CanAsync("priya", SubscriptionSchema.Permissions.Cancel, Res("subscription", "initech-tools")));
        Assert.True(await CanAsync("sam", SubscriptionSchema.Permissions.View, Res("subscription", "initech-tools"))); // support flows down
        Assert.False(await CanAsync("alice", CustomerSchema.Permissions.View, Res("customer", "initech"))); // another customer's admin
        Assert.True(await HasTupleAsync("subscription:initech-tools#parent@wallet:initech-ops"));
    }

    [Fact]
    public async Task Flushing_stamps_the_zed_token_on_each_row_and_marks_the_message_processed()
    {
        await AddWalletAsync("stamped");
        await FlushAsync();

        var token = await QueryAsync(db => db.Wallets.Where(w => w.Id == "stamped").Select(w => w.AuthzToken).SingleAsync(Ct));
        Assert.False(string.IsNullOrEmpty(token));
        var message = (await OutboxAsync()).Last(m => m.Payload.Contains("stamped", StringComparison.Ordinal));
        Assert.NotNull(message.ProcessedAt);
        Assert.Equal(1, message.Attempts);
        Assert.Null(message.LastError);
    }

    [Fact]
    public async Task Moving_a_wallet_takes_access_from_the_old_domains_admins_on_the_next_request()
    {
        // PRD scenario: Alice moves uk-rnd from acme-uk to acme-ie; Bob and Heidi lose all access to uk-rnd and github.
        Assert.True(await CanAsync("bob", WalletSchema.Permissions.Spend, Res("wallet", "uk-rnd")));
        Assert.True(await CanAsync("heidi", SubscriptionSchema.Permissions.Cancel, Res("subscription", "github")));

        await MutateAsync<Wallet>("uk-rnd", w => w.MoveTo("acme-ie"));
        await FlushAsync();

        foreach (var user in new[] { "bob", "heidi" })
        {
            foreach (var permission in new[] { "view", "spend", "admin" })
                Assert.False(await CanAsync(user, permission, Res("wallet", "uk-rnd")), $"{user} {permission} uk-rnd");
            Assert.False(await CanAsync(user, SubscriptionSchema.Permissions.View, Res("subscription", "github")), $"{user} view github");
        }

        Assert.True(await CanAsync("alice", WalletSchema.Permissions.Spend, Res("wallet", "uk-rnd")));
        Assert.True(await CanAsync("bob", WalletSchema.Permissions.Spend, Res("wallet", "uk-ops"))); // the rest of acme-uk is untouched
        Assert.False(await HasTupleAsync("wallet:uk-rnd#parent@domain:acme-uk"));
        Assert.True(await HasTupleAsync("wallet:uk-rnd#parent@domain:acme-ie"));
    }

    [Fact]
    public async Task Deleting_an_object_removes_every_tuple_where_it_is_the_resource_including_its_grants()
    {
        await AddWalletAsync("doomed");
        await FlushAsync();
        await InScopeAsync(sp => sp.GetRequiredService<AccessService>().GrantAsync(
            Actor.System, new GrantTarget(Res("wallet", "doomed"), WalletSchema.Relations.Viewer, SubjectRef.User("erin")), Ct));
        Assert.True(await CanAsync("erin", WalletSchema.Permissions.View, Res("wallet", "doomed")));

        await InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<IRepository<Wallet>>();
            var wallet = (await repo.FindAsync("doomed", Ct))!;
            wallet.Delete();
            repo.Remove(wallet);
            await sp.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
        });
        await FlushAsync();

        Assert.Empty(await Host.Get<IRelationshipStore>().ReadAsync(new RelationshipFilter(WalletSchema.Type, "doomed"), Ct));
        Assert.False(await CanAsync("erin", WalletSchema.Permissions.View, Res("wallet", "doomed")));
        Assert.False(await CanAsync("alice", WalletSchema.Permissions.View, Res("wallet", "doomed")));
    }

    [Fact]
    public async Task Deleting_a_group_removes_it_and_every_place_it_was_granted_or_nested()
    {
        Assert.True(await CanAsync("heidi", DomainSchema.Permissions.Admin, Res("domain", "acme-uk")));

        await InScopeAsync(async sp =>
        {
            var access = sp.GetRequiredService<AccessService>();
            await access.DeleteGroupAsync(Actor.ForUser("alice"), "uk-contractors", Ct);
        });

        Assert.False(await CanAsync("heidi", DomainSchema.Permissions.Admin, Res("domain", "acme-uk")));
        Assert.True(await CanAsync("bob", DomainSchema.Permissions.Admin, Res("domain", "acme-uk"))); // Bob is in uk-it directly
        Assert.False(await HasTupleAsync("group:uk-it#member@group:uk-contractors#member"));
        Assert.Empty(await Host.Get<IRelationshipStore>().ReadAsync(new RelationshipFilter(GroupSchema.Type, "uk-contractors"), Ct));
    }

    [Fact]
    public async Task Deleting_a_group_removes_its_grants_on_every_resource_type()
    {
        await InScopeAsync(async sp =>
        {
            var access = sp.GetRequiredService<AccessService>();
            await access.CreateGroupAsync(Actor.ForUser("alice"), Poc.Domain.Directory.GroupOwnerType.Customer, "acme", "Everywhere", Ct, "everywhere");
            var members = SubjectRef.GroupMembers("everywhere");
            await access.GrantAsync(Actor.System, new GrantTarget(Res("customer", "acme"), CustomerSchema.Relations.Viewer, members), Ct);
            await access.GrantAsync(Actor.System, new GrantTarget(Res("domain", "acme-ie"), DomainSchema.Relations.Viewer, members), Ct);
            await access.GrantAsync(Actor.System, new GrantTarget(Res("wallet", "ie-ops"), WalletSchema.Relations.Spender, members), Ct);
            await access.GrantAsync(Actor.System, new GrantTarget(Res("subscription", "m365-bp"), SubscriptionSchema.Relations.Operator, members), Ct);
            await access.AddGroupMemberAsync(Actor.System, "finance-ap", members, Ct);
        });
        Assert.Equal(5, (await AllTuplesAsync()).Count(t => t.Subject.Id == "everywhere"));

        await InScopeAsync(sp => sp.GetRequiredService<AccessService>().DeleteGroupAsync(Actor.ForUser("alice"), "everywhere", Ct));

        Assert.DoesNotContain(await AllTuplesAsync(), t => t.Subject.Id == "everywhere" || t.Resource.Id == "everywhere");
    }

    // ---- Ordering, replay ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Messages_for_one_object_apply_in_the_order_they_were_written()
    {
        await AddWalletAsync("ordered", "acme-uk");
        await MutateAsync<Wallet>("ordered", w => w.MoveTo("acme-ie"));
        await FlushAsync();

        Assert.True(await HasTupleAsync("wallet:ordered#parent@domain:acme-ie"));
        Assert.False(await HasTupleAsync("wallet:ordered#parent@domain:acme-uk"));
    }

    [Fact]
    public async Task Replaying_every_message_leaves_the_engine_exactly_as_it_was()
    {
        var (seed, _) = ValidationYaml.Load();
        var before = (await AllTuplesAsync()).Select(t => t.ToString()).Order().ToList();
        var total = (await OutboxAsync()).Count;
        await QueryAsync(db => db.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null), Ct));

        var outcome = await FlushAsync();

        Assert.Equal(total, outcome.Processed);
        Assert.True(outcome.FullyApplied);
        Assert.Equal(before, (await AllTuplesAsync()).Select(t => t.ToString()).Order());
        Assert.Equal(seed.Select(t => t.ToString()).Order(), before);
    }

    [Fact]
    public async Task An_empty_outbox_flushes_to_nothing()
    {
        await FlushAsync();
        var outcome = await FlushAsync();
        Assert.Equal(new FlushOutcome(0, 0, 0, false), outcome);
    }

    [Fact]
    public async Task Concurrent_flushes_do_not_double_apply_or_reorder()
    {
        await AddWalletAsync("race-1");
        await AddWalletAsync("race-2");
        await AddWalletAsync("race-3");
        await MutateAsync<Wallet>("race-2", w => w.MoveTo("acme-ie"));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => FlushAsync()));

        Assert.Equal(4, outcomes.Sum(o => o.Processed));
        Assert.All(outcomes, o => Assert.True(o.FullyApplied));
        Assert.True(await HasTupleAsync("wallet:race-2#parent@domain:acme-ie"));
        Assert.False(await HasTupleAsync("wallet:race-2#parent@domain:acme-uk"));
        Assert.All(await OutboxAsync(), m => Assert.Equal(1, m.Attempts));
    }

    // ---- Failure handling ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_poisoned_message_is_retried_five_times_then_left_unprocessed_with_its_error_while_others_proceed()
    {
        await AddWalletAsync("poisoned");
        await AddWalletAsync("healthy");
        Host.Faults.PoisonWrite = updates => updates.Any(u => u.Relationship.Resource.Id == "poisoned");

        var first = await FlushAsync();
        Assert.Equal(1, first.Processed);
        Assert.Equal(1, first.Failed);
        Assert.Equal(1, first.Remaining);
        Assert.True(await HasTupleAsync("wallet:healthy#parent@domain:acme-uk"));
        Assert.False(await HasTupleAsync("wallet:poisoned#parent@domain:acme-uk"));

        for (var attempt = 2; attempt <= 5; attempt++) Assert.Equal(1, (await FlushAsync()).Failed);

        var status = await Host.Get<IOutboxInspector>().GetStatusAsync(Ct);
        Assert.Equal(1, status.Failed);
        var failure = Assert.Single(status.Failures);
        Assert.Equal(5, failure.Attempts);
        Assert.Contains("injected poison", failure.LastError, StringComparison.Ordinal);
        Assert.Equal("WalletCreated", failure.Type);

        var writesBefore = Host.Faults.WriteCalls;
        var sixth = await FlushAsync();
        Assert.Equal(writesBefore, Host.Faults.WriteCalls); // a dead message is not retried again
        Assert.Equal(0, sixth.Failed);
        Assert.Equal(1, sixth.Remaining);
    }

    [Fact]
    public async Task A_dead_message_blocks_later_messages_for_the_same_object_but_not_for_others()
    {
        await AddWalletAsync("stuck");
        Host.Faults.PoisonWrite = updates => updates.Any(u => u.Relationship.Resource.Id == "stuck");
        for (var i = 0; i < 5; i++) await FlushAsync();

        Host.Faults.PoisonWrite = null; // the cause is gone, but the message is already dead
        await MutateAsync<Wallet>("stuck", w => w.MoveTo("acme-ie")); // a later message for the same object
        await AddWalletAsync("unrelated");

        var outcome = await FlushAsync();

        Assert.Equal(1, outcome.Processed); // only "unrelated"
        Assert.Equal(2, outcome.Remaining); // the dead create and the blocked move
        Assert.True(await HasTupleAsync("wallet:unrelated#parent@domain:acme-uk"));
        Assert.False(await HasTupleAsync("wallet:stuck#parent@domain:acme-ie"));
        Assert.False(await HasTupleAsync("wallet:stuck#parent@domain:acme-uk"));
    }

    [Fact]
    public async Task A_failure_for_one_object_blocks_its_own_later_messages_within_the_same_flush()
    {
        await AddWalletAsync("first-fails");
        await MutateAsync<Wallet>("first-fails", w => w.MoveTo("acme-ie"));
        Host.Faults.PoisonWrite = updates => updates.Any(u => u.Relationship.Resource.Id == "first-fails" && u.Operation == RelationshipOperation.Touch && u.Relationship.Subject.Id == "acme-uk");

        var outcome = await FlushAsync();

        Assert.Equal(0, outcome.Processed);
        Assert.Equal(2, outcome.Remaining);
        Assert.False(await HasTupleAsync("wallet:first-fails#parent@domain:acme-ie")); // the move must not leapfrog the create
    }

    [Fact]
    public async Task An_outage_never_burns_attempts_so_it_can_outlast_any_retry_limit()
    {
        Host.Faults.Down = true;
        await AddWalletAsync("outage");

        for (var i = 0; i < 12; i++)
        {
            var outcome = await FlushAsync();
            Assert.True(outcome.EngineUnavailable);
            Assert.Equal(1, outcome.Remaining);
            Assert.False(outcome.FullyApplied);
        }

        var message = (await OutboxAsync()).Single(m => m.Payload.Contains("outage", StringComparison.Ordinal));
        Assert.Equal(0, message.Attempts);
        Assert.Contains("injected outage", message.LastError, StringComparison.Ordinal);
        Assert.Equal(0, (await Host.Get<IOutboxInspector>().GetStatusAsync(Ct)).Failed);

        Host.Faults.Down = false;
        var recovered = await FlushAsync();

        Assert.True(recovered.FullyApplied);
        Assert.True(await HasTupleAsync("wallet:outage#parent@domain:acme-uk"));
        Assert.True((await Host.Get<ReconciliationRunner>().RunAsync(repair: false, Ct)).InSync);
    }

    [Fact]
    public async Task A_message_that_cannot_be_interpreted_fails_visibly_instead_of_being_skipped()
    {
        await QueryAsync(async db =>
        {
            db.OutboxMessages.Add(new Poc.Infrastructure.Persistence.OutboxMessage { Type = "NoSuchEvent", Payload = "{}", CreatedAt = Now });
            return await db.SaveChangesAsync(Ct);
        });

        for (var i = 0; i < 5; i++) await FlushAsync();

        var status = await Host.Get<IOutboxInspector>().GetStatusAsync(Ct);
        Assert.Equal(1, status.Failed);
        Assert.Contains("NoSuchEvent", Assert.Single(status.Failures).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_reports_pending_count_and_the_age_of_the_oldest_message()
    {
        await AddWalletAsync("lag-1");
        await AddWalletAsync("lag-2");

        var status = await Host.Get<IOutboxInspector>().GetStatusAsync(Ct);

        Assert.Equal(2, status.Pending);
        Assert.Equal(0, status.Failed);
        Assert.NotNull(status.OldestPendingAt);
        await FlushAsync();
        Assert.Null((await Host.Get<IOutboxInspector>().GetStatusAsync(Ct)).OldestPendingAt);
    }

    // ---- Default grant on creation ------------------------------------------------------------------------------

    [Fact]
    public async Task A_spender_who_creates_a_subscription_becomes_its_operator_through_the_audit()
    {
        // PRD scenario: Carol creates a subscription in uk-ops; she can view it and change its quantity, cannot cancel it.
        await CommitAsync(sp => sp.GetRequiredService<IRepository<Subscription>>().Add(
            Subscription.Create("uk-ops", "Visio", 4, "carol", Now, "carol-sub")));
        await FlushAsync();

        var sub = Res("subscription", "carol-sub");
        Assert.True(await CanAsync("carol", SubscriptionSchema.Permissions.View, sub));
        Assert.True(await CanAsync("carol", SubscriptionSchema.Permissions.ChangeQuantity, sub));
        Assert.False(await CanAsync("carol", SubscriptionSchema.Permissions.Cancel, sub));
        Assert.False(await CanAsync("carol", SubscriptionSchema.Permissions.View, Res("subscription", "azure-plan"))); // other subscriptions stay hidden

        var entry = Assert.Single((await AuditAsync()), a => a.ResourceId == "carol-sub");
        Assert.Equal((GrantAction.Grant, "operator", "user", "carol", GrantStatus.Applied), (entry.Action, entry.Relation, entry.SubjectType, entry.SubjectId, entry.Status));
        Assert.Equal(Actor.SystemUserId, entry.ActorUserId);
        Assert.False(string.IsNullOrEmpty(entry.ZedToken));
    }

    [Fact]
    public async Task The_default_grant_is_not_repeated_when_the_message_is_replayed()
    {
        await CommitAsync(sp => sp.GetRequiredService<IRepository<Subscription>>().Add(
            Subscription.Create("uk-ops", "Visio", 4, "carol", Now, "carol-sub")));
        await FlushAsync();
        await QueryAsync(db => db.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null), Ct));

        await FlushAsync();

        Assert.Single((await AuditAsync()), a => a.ResourceId == "carol-sub");
    }

    [Fact]
    public async Task An_admin_who_creates_a_subscription_needs_no_default_grant()
    {
        await CommitAsync(sp => sp.GetRequiredService<IRepository<Subscription>>().Add(
            Subscription.Create("uk-ops", "Teams", 2, "alice", Now, "alice-sub")));
        await FlushAsync();

        Assert.DoesNotContain((await AuditAsync()), a => a.ResourceId == "alice-sub");
        Assert.DoesNotContain(await AllTuplesAsync(), t => t.Resource.Id == "alice-sub" && t.Relation == "operator");
        Assert.True(await CanAsync("alice", SubscriptionSchema.Permissions.Cancel, Res("subscription", "alice-sub")));
    }

    [Fact]
    public async Task Subscriptions_made_by_the_system_get_no_default_grant()
    {
        Assert.DoesNotContain(await AuditAsync(), a => a.SubjectId == Actor.SystemUserId);
        Assert.Single((await AuditAsync()), a => a.Relation == "operator"); // only Dave's seeded grant
    }
}
