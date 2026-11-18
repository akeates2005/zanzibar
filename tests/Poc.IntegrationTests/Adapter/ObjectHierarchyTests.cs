using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.IntegrationTests.Fixtures;

namespace Poc.IntegrationTests.Adapter;

/// <summary>The engine walk that replaced the per-type database lookup: existence, ancestry and the root customer.</summary>
[Collection(SpiceDbCollectionDefinition.Name)]
public sealed class ObjectHierarchyTests(SpiceDbFixture spice)
{
    private static CancellationToken Ct => CancellationToken.None;

    private async Task<ObjectHierarchy> HierarchyAsync()
    {
        var source = new AccessModelSource(spice.Schema);
        await source.LoadAsync(Ct);
        return new ObjectHierarchy(spice.Relationships, source);
    }

    private Task<string> ParentAsync(string type, string id, string parentType, string parentId) =>
        spice.Relationships.WriteAsync(
            [RelationshipUpdate.Touch(new Relationship(new ResourceRef(type, id), "parent", new SubjectRef(parentType, parentId)))], Ct);

    [Fact]
    public async Task A_subscription_is_placed_under_its_whole_chain_and_its_customer()
    {
        var u = SpiceDbFixture.Unique();
        await ParentAsync("customer", $"c-{u}", "platform", "main");
        await ParentAsync("commerce/domain", $"d-{u}", "customer", $"c-{u}");
        await ParentAsync("commerce/wallet", $"w-{u}", "commerce/domain", $"d-{u}");
        await ParentAsync("commerce/subscription", $"s-{u}", "commerce/wallet", $"w-{u}");

        var placement = await (await HierarchyAsync()).PlaceAsync(new ResourceRef("commerce/subscription", $"s-{u}"), Ct);

        Assert.NotNull(placement);
        Assert.Equal(["platform:main", $"customer:c-{u}", $"commerce/domain:d-{u}", $"commerce/wallet:w-{u}"], placement.Ancestors.Select(a => a.ToString()));
        Assert.Equal($"c-{u}", placement.RootCustomerId);
    }

    [Fact]
    public async Task A_customer_is_its_own_root_and_the_platform_has_none()
    {
        var u = SpiceDbFixture.Unique();
        await ParentAsync("customer", $"c-{u}", "platform", "main");
        var hierarchy = await HierarchyAsync();

        Assert.Equal($"c-{u}", (await hierarchy.PlaceAsync(new ResourceRef("customer", $"c-{u}"), Ct))!.RootCustomerId);
        var platform = await hierarchy.PlaceAsync(new ResourceRef("platform", "main"), Ct);
        Assert.NotNull(platform);
        Assert.Empty(platform.Ancestors);
        Assert.Null(platform.RootCustomerId);
    }

    [Fact]
    public async Task An_object_without_a_parent_tuple_or_of_a_type_that_takes_no_grants_does_not_exist()
    {
        var hierarchy = await HierarchyAsync();
        Assert.Null(await hierarchy.PlaceAsync(new ResourceRef("commerce/wallet", $"missing-{SpiceDbFixture.Unique()}"), Ct));
        Assert.Null(await hierarchy.PlaceAsync(new ResourceRef("platform", "other"), Ct));
        Assert.Null(await hierarchy.PlaceAsync(new ResourceRef("group", "uk-it"), Ct));
        Assert.Null(await hierarchy.PlaceAsync(new ResourceRef("nonsense", "x"), Ct));
    }

    [Fact]
    public async Task A_broken_chain_stops_at_the_last_ancestor_found_so_no_customer_scope_applies()
    {
        var u = SpiceDbFixture.Unique();
        await ParentAsync("commerce/wallet", $"w-{u}", "commerce/domain", $"orphan-{u}"); // the domain has no parent tuple

        var placement = await (await HierarchyAsync()).PlaceAsync(new ResourceRef("commerce/wallet", $"w-{u}"), Ct);

        Assert.Equal([$"commerce/domain:orphan-{u}"], placement!.Ancestors.Select(a => a.ToString()));
        Assert.Null(placement.RootCustomerId);
    }

    [Fact]
    public async Task Two_parents_is_corrupt_structure_and_is_refused()
    {
        var u = SpiceDbFixture.Unique();
        await ParentAsync("commerce/wallet", $"w-{u}", "commerce/domain", $"d1-{u}");
        await ParentAsync("commerce/wallet", $"w-{u}", "commerce/domain", $"d2-{u}");

        var hierarchy = await HierarchyAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => hierarchy.PlaceAsync(new ResourceRef("commerce/wallet", $"w-{u}"), Ct));
    }
}
