using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.IntegrationTests.Fixtures;

namespace Poc.IntegrationTests.Adapter;

[Collection(SpiceDbCollectionDefinition.Name)]
public sealed class RelationshipStoreTests(SpiceDbFixture fx)
{
    private static CancellationToken Ct => CancellationToken.None;

    private static Relationship Viewer(ResourceRef wallet, SubjectRef subject) =>
        new(wallet, WalletSchema.Relations.Viewer, subject);

    private static ResourceRef NewWallet() => new(WalletSchema.Type, $"w-{SpiceDbFixture.Unique()}");

    [Fact]
    public async Task Write_returns_a_zed_token_and_replaying_a_touch_is_harmless()
    {
        var wallet = NewWallet();
        var tuple = Viewer(wallet, SubjectRef.User($"u-{SpiceDbFixture.Unique()}"));

        var first = await fx.Relationships.WriteAsync([RelationshipUpdate.Touch(tuple)], Ct);
        var second = await fx.Relationships.WriteAsync([RelationshipUpdate.Touch(tuple)], Ct);

        Assert.False(string.IsNullOrEmpty(first));
        Assert.False(string.IsNullOrEmpty(second));
        var read = await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, wallet.Id), Ct);
        Assert.Equal([tuple], read);
    }

    [Fact]
    public async Task Write_applies_touch_and_delete_together_in_one_request()
    {
        var wallet = NewWallet();
        var oldUser = SubjectRef.User($"old-{SpiceDbFixture.Unique()}");
        var newUser = SubjectRef.User($"new-{SpiceDbFixture.Unique()}");
        await fx.Relationships.WriteAsync([RelationshipUpdate.Touch(Viewer(wallet, oldUser))], Ct);

        await fx.Relationships.WriteAsync(
            [RelationshipUpdate.Delete(Viewer(wallet, oldUser)), RelationshipUpdate.Touch(Viewer(wallet, newUser))], Ct);

        var read = await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, wallet.Id), Ct);
        Assert.Equal([Viewer(wallet, newUser)], read);
    }

    [Fact]
    public async Task A_failing_write_is_atomic_and_surfaces_as_a_typed_exception()
    {
        var wallet = NewWallet();
        var good = Viewer(wallet, SubjectRef.User($"u-{SpiceDbFixture.Unique()}"));
        var bad = new Relationship(wallet, "no_such_relation", SubjectRef.User("x"));

        await Assert.ThrowsAsync<AuthorizationUnavailableException>(
            () => fx.Relationships.WriteAsync([RelationshipUpdate.Touch(good), RelationshipUpdate.Touch(bad)], Ct));

        Assert.Empty(await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, wallet.Id), Ct));
    }

    [Fact]
    public async Task Read_filters_by_resource_relation_and_subject()
    {
        var wallet = NewWallet();
        var group = SubjectRef.GroupMembers($"g-{SpiceDbFixture.Unique()}");
        var user = SubjectRef.User($"u-{SpiceDbFixture.Unique()}");
        var viaGroup = Viewer(wallet, group);
        var viaUser = Viewer(wallet, user);
        var spender = new Relationship(wallet, WalletSchema.Relations.Spender, user);
        await fx.Relationships.WriteAsync(
            [RelationshipUpdate.Touch(viaGroup), RelationshipUpdate.Touch(viaUser), RelationshipUpdate.Touch(spender)], Ct);

        Assert.Equal(3, (await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, wallet.Id), Ct)).Count);
        Assert.Equivalent(new[] { viaGroup, viaUser },
            await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, wallet.Id, WalletSchema.Relations.Viewer), Ct));
        Assert.Equal([viaGroup],
            await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, wallet.Id,
                Subject: new SubjectFilter(GroupSchema.Type, group.Id, GroupSchema.Relations.Member)), Ct));
        Assert.Equivalent(new[] { viaUser, spender },
            await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, wallet.Id,
                Subject: new SubjectFilter(UserSchema.Type, user.Id)), Ct));
    }

    [Fact]
    public async Task Delete_removes_only_the_matching_resource_and_returns_a_token()
    {
        var keep = NewWallet();
        var drop = NewWallet();
        var user = SubjectRef.User($"u-{SpiceDbFixture.Unique()}");
        await fx.Relationships.WriteAsync([RelationshipUpdate.Touch(Viewer(keep, user)), RelationshipUpdate.Touch(Viewer(drop, user))], Ct);

        var token = await fx.Relationships.DeleteAsync(new RelationshipFilter(WalletSchema.Type, drop.Id), Ct);

        Assert.False(string.IsNullOrEmpty(token));
        Assert.Empty(await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, drop.Id), Ct));
        Assert.Single(await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, keep.Id), Ct));
    }

    [Fact]
    public async Task Delete_with_nothing_to_match_succeeds()
    {
        var token = await fx.Relationships.DeleteAsync(new RelationshipFilter(WalletSchema.Type, NewWallet().Id), Ct);
        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public async Task Delete_by_subject_filter_removes_a_group_from_every_place_it_is_granted()
    {
        // The GroupDeleted case: remove every relationship whose subject is group:{id}#member.
        var group = $"g-{SpiceDbFixture.Unique()}";
        var subject = SubjectRef.GroupMembers(group);
        var w1 = NewWallet();
        var w2 = NewWallet();
        var survivor = Viewer(w2, SubjectRef.User($"u-{SpiceDbFixture.Unique()}"));
        await fx.Relationships.WriteAsync(
            [RelationshipUpdate.Touch(Viewer(w1, subject)), RelationshipUpdate.Touch(Viewer(w2, subject)), RelationshipUpdate.Touch(survivor)], Ct);

        var filter = new RelationshipFilter(WalletSchema.Type, Subject: new SubjectFilter(GroupSchema.Type, group, GroupSchema.Relations.Member));
        await fx.Relationships.DeleteAsync(filter, Ct);

        Assert.Empty(await fx.Relationships.ReadAsync(filter, Ct));
        Assert.Equal([survivor], await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, w2.Id), Ct));
    }

    [Fact]
    public async Task Delete_loops_over_partial_deletions_until_everything_is_gone()
    {
        var wallet = NewWallet();
        var users = Enumerable.Range(0, 1200).Select(i => SubjectRef.User($"bulk-{SpiceDbFixture.Unique()}-{i}")).ToList();
        foreach (var chunk in users.Chunk(600))
            await fx.Relationships.WriteAsync(chunk.Select(u => RelationshipUpdate.Touch(Viewer(wallet, u))).ToList(), Ct);
        Assert.Equal(1200, (await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, wallet.Id), Ct)).Count);

        await fx.Relationships.DeleteAsync(new RelationshipFilter(WalletSchema.Type, wallet.Id), Ct);

        Assert.Empty(await fx.Relationships.ReadAsync(new RelationshipFilter(WalletSchema.Type, wallet.Id), Ct));
    }

    [Fact]
    public async Task Write_with_no_updates_is_rejected_before_any_call()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => fx.Relationships.WriteAsync([], Ct));
    }

    [Fact]
    public void A_filter_must_name_a_resource_type()
    {
        Assert.Throws<ArgumentException>(() => new RelationshipFilter(""));
    }

    [Fact]
    public async Task Refs_with_blank_ids_are_rejected_before_any_call()
    {
        var bad = new Relationship(new ResourceRef(WalletSchema.Type, ""), WalletSchema.Relations.Viewer, SubjectRef.User("u"));
        await Assert.ThrowsAsync<ArgumentException>(() => fx.Relationships.WriteAsync([RelationshipUpdate.Touch(bad)], Ct));
    }
}
