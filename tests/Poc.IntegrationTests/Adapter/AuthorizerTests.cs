using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.IntegrationTests.Fixtures;

namespace Poc.IntegrationTests.Adapter;

[Collection(SpiceDbCollectionDefinition.Name)]
public sealed class AuthorizerTests(SpiceDbFixture fx)
{
    private static CancellationToken Ct => CancellationToken.None;

    private async Task<MiniModel> SeedAsync()
    {
        var model = new MiniModel();
        await model.WriteAsync(fx.Relationships);
        return model;
    }

    // ---- CheckAsync ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_follows_admin_inheritance_down_the_whole_tree()
    {
        var m = await SeedAsync();
        Assert.True(await fx.Authorizer.CheckAsync(m.CustomerAdmin, WalletSchema.Permissions.TopUp, m.Wallet, Ct));
        Assert.True(await fx.Authorizer.CheckAsync(m.CustomerAdmin, SubscriptionSchema.Permissions.Cancel, m.Subscription, Ct));
        Assert.True(await fx.Authorizer.CheckAsync(m.PlatformAdmin, WalletSchema.Permissions.Delete, m.Wallet, Ct));
    }

    [Fact]
    public async Task Check_resolves_group_and_nested_group_membership_without_inheritance_for_spenders()
    {
        var m = await SeedAsync();
        Assert.True(await fx.Authorizer.CheckAsync(m.Spender, WalletSchema.Permissions.Spend, m.Wallet, Ct));
        Assert.True(await fx.Authorizer.CheckAsync(m.Nested, WalletSchema.Permissions.Spend, m.Wallet, Ct));
        Assert.False(await fx.Authorizer.CheckAsync(m.Spender, WalletSchema.Permissions.TopUp, m.Wallet, Ct));
        Assert.False(await fx.Authorizer.CheckAsync(m.Spender, SubscriptionSchema.Permissions.View, m.Subscription, Ct));
    }

    [Fact]
    public async Task Check_gives_support_view_only_and_strangers_nothing()
    {
        var m = await SeedAsync();
        Assert.True(await fx.Authorizer.CheckAsync(m.Support, SubscriptionSchema.Permissions.View, m.Subscription, Ct));
        Assert.False(await fx.Authorizer.CheckAsync(m.Support, WalletSchema.Permissions.Spend, m.Wallet, Ct));
        Assert.False(await fx.Authorizer.CheckAsync(m.Stranger, CustomerSchema.Permissions.View, m.Customer, Ct));
    }

    [Fact]
    public async Task Check_is_immediately_consistent_after_a_revoke()
    {
        var m = await SeedAsync();
        Assert.True(await fx.Authorizer.CheckAsync(m.Nested, WalletSchema.Permissions.Spend, m.Wallet, Ct));
        await fx.Relationships.WriteAsync(
            [RelationshipUpdate.Delete(new Relationship(new(GroupSchema.Type, m.SpendGroup), GroupSchema.Relations.Member,
                SubjectRef.GroupMembers(m.ContractorGroup)))], Ct);
        Assert.False(await fx.Authorizer.CheckAsync(m.Nested, WalletSchema.Permissions.Spend, m.Wallet, Ct));
        Assert.True(await fx.Authorizer.CheckAsync(m.Spender, WalletSchema.Permissions.Spend, m.Wallet, Ct));
    }

    [Fact]
    public async Task Check_on_an_unknown_permission_throws_and_never_returns_false()
    {
        var m = await SeedAsync();
        await Assert.ThrowsAsync<AuthorizationUnavailableException>(
            () => fx.Authorizer.CheckAsync(m.CustomerAdmin, "no_such_permission", m.Wallet, Ct));
    }

    // ---- CheckManyAsync -----------------------------------------------------------------------------------------

    [Fact]
    public async Task CheckMany_returns_one_answer_per_distinct_permission()
    {
        var m = await SeedAsync();
        var result = await fx.Authorizer.CheckManyAsync(m.Spender, m.Wallet,
        [
            WalletSchema.Permissions.View, WalletSchema.Permissions.Spend, WalletSchema.Permissions.TopUp,
            WalletSchema.Permissions.CreateSubscription, WalletSchema.Permissions.Edit, WalletSchema.Permissions.Spend,
        ], Ct);

        Assert.Equal(5, result.Count);
        Assert.True(result[WalletSchema.Permissions.View]);
        Assert.True(result[WalletSchema.Permissions.Spend]);
        Assert.True(result[WalletSchema.Permissions.CreateSubscription]);
        Assert.False(result[WalletSchema.Permissions.TopUp]);
        Assert.False(result[WalletSchema.Permissions.Edit]);
    }

    [Fact]
    public async Task CheckMany_with_nothing_requested_returns_empty_without_calling_the_engine()
    {
        var m = new MiniModel();
        using var dead = FailureTests.DeadConnection();
        var authorizer = new Poc.Infrastructure.SpiceDb.SpiceDbAuthorizer(
            dead, Microsoft.Extensions.Logging.Abstractions.NullLogger<Poc.Infrastructure.SpiceDb.SpiceDbAuthorizer>.Instance);
        Assert.Empty(await authorizer.CheckManyAsync(m.Stranger, m.Wallet, [], Ct));
    }

    [Fact]
    public async Task CheckMany_with_an_unknown_permission_throws_rather_than_reporting_false()
    {
        var m = await SeedAsync();
        await Assert.ThrowsAsync<AuthorizationUnavailableException>(() => fx.Authorizer.CheckManyAsync(
            m.CustomerAdmin, m.Wallet, [WalletSchema.Permissions.View, "no_such_permission"], Ct));
    }

    // ---- Lookups ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LookupResources_returns_exactly_what_the_subject_can_reach()
    {
        var m = await SeedAsync();
        Assert.Equal([m.Wallet.Id], await fx.Authorizer.LookupResourcesAsync(m.CustomerAdmin, WalletSchema.Type, WalletSchema.Permissions.View, Ct));
        Assert.Equal([m.Wallet.Id], await fx.Authorizer.LookupResourcesAsync(m.Spender, WalletSchema.Type, WalletSchema.Permissions.Spend, Ct));
        Assert.Empty(await fx.Authorizer.LookupResourcesAsync(m.Spender, SubscriptionSchema.Type, SubscriptionSchema.Permissions.View, Ct));
        Assert.Empty(await fx.Authorizer.LookupResourcesAsync(m.Stranger, WalletSchema.Type, WalletSchema.Permissions.View, Ct));
    }

    [Fact]
    public async Task LookupSubjects_resolves_through_groups_nesting_and_inheritance()
    {
        var m = await SeedAsync();
        var spenders = await fx.Authorizer.LookupSubjectsAsync(m.Wallet, WalletSchema.Permissions.Spend, UserSchema.Type, Ct);
        Assert.Equal(new[] { m.CustomerAdmin, m.PlatformAdmin, m.Spender, m.Nested }.OrderBy(s => s.Id), spenders.OrderBy(s => s.Id));

        var viewers = await fx.Authorizer.LookupSubjectsAsync(m.Wallet, WalletSchema.Permissions.View, UserSchema.Type, Ct);
        Assert.Equal(new[] { m.CustomerAdmin, m.PlatformAdmin, m.Spender, m.Nested, m.Support }.OrderBy(s => s.Id), viewers.OrderBy(s => s.Id));
    }

    [Fact]
    public async Task LookupResources_pages_past_the_engines_1000_result_cap()
    {
        var user = SubjectRef.User($"bulk-{SpiceDbFixture.Unique()}");
        var prefix = $"bw-{SpiceDbFixture.Unique()}-";
        var expected = await WriteViewerWalletsAsync(user, prefix, 1200);

        var found = await fx.Authorizer.LookupResourcesAsync(user, WalletSchema.Type, WalletSchema.Permissions.View, Ct);

        Assert.Equal(1200, found.Count);
        Assert.Equal(expected.Order(), found.Order());
    }

    [Theory]
    [InlineData(25)] // partial last page
    [InlineData(21)] // exact multiple of the page size: the final page is full and the next is empty
    public async Task LookupResources_pages_through_the_cursor_with_a_small_page_size(int count)
    {
        using var connection = SpiceDbFixture.NewConnection(fx.Host, o => o.LookupPageSize = 7);
        var authorizer = new Poc.Infrastructure.SpiceDb.SpiceDbAuthorizer(
            connection, Microsoft.Extensions.Logging.Abstractions.NullLogger<Poc.Infrastructure.SpiceDb.SpiceDbAuthorizer>.Instance);
        var user = SubjectRef.User($"small-{SpiceDbFixture.Unique()}");
        var expected = await WriteViewerWalletsAsync(user, $"sw-{SpiceDbFixture.Unique()}-", count);

        var found = await authorizer.LookupResourcesAsync(user, WalletSchema.Type, WalletSchema.Permissions.View, Ct);

        Assert.Equal(expected.Order(), found.Order());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(21)]
    [InlineData(1200)] // above the 1000 cap that applies to LookupResources; LookupSubjects must still be complete
    public async Task LookupSubjects_returns_every_subject_in_one_stream(int count)
    {
        var wallet = new ResourceRef(WalletSchema.Type, $"ssw-{SpiceDbFixture.Unique()}");
        var users = await WriteWalletViewersAsync(wallet, "su", count);

        var found = await fx.Authorizer.LookupSubjectsAsync(wallet, WalletSchema.Permissions.View, UserSchema.Type, Ct);

        Assert.Equal(count, found.Count);
        Assert.Equal(users.Order(), found.Select(s => s.Id).Order());
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task<List<string>> WriteViewerWalletsAsync(SubjectRef user, string prefix, int count)
    {
        var ids = Enumerable.Range(0, count).Select(i => $"{prefix}{i}").ToList();
        foreach (var chunk in ids.Chunk(500))
            await fx.Relationships.WriteAsync(
                chunk.Select(id => RelationshipUpdate.Touch(
                    new Relationship(new(WalletSchema.Type, id), WalletSchema.Relations.Viewer, user))).ToList(), Ct);
        return ids;
    }

    private async Task<List<string>> WriteWalletViewersAsync(ResourceRef wallet, string prefix, int count)
    {
        var unique = SpiceDbFixture.Unique();
        var ids = Enumerable.Range(0, count).Select(i => $"{prefix}-{unique}-{i}").ToList();
        foreach (var chunk in ids.Chunk(500))
            await fx.Relationships.WriteAsync(
                chunk.Select(id => RelationshipUpdate.Touch(
                    new Relationship(wallet, WalletSchema.Relations.Viewer, SubjectRef.User(id)))).ToList(), Ct);
        return ids;
    }
}
