using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.Infrastructure.SpiceDb;
using Poc.IntegrationTests.Fixtures;
using Poc.UnitTests.Authorization;

namespace Poc.IntegrationTests.Adapter;

/// <summary>
/// The access model is read from the engine (SpiceDB ReflectSchema), not from constants. These tests run the adapter against
/// a real engine: the shared one holding schema.zed, and private ones for schemas the shared engine must not be given.
/// </summary>
[Collection(SpiceDbCollectionDefinition.Name)]
public sealed class SchemaReflectionTests(SpiceDbFixture spice)
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Records hold lists by reference, so compare a canonical rendering instead.</summary>
    private static List<string> Render(IEnumerable<ReflectedDefinition> definitions) => definitions
        .OrderBy(d => d.Name, StringComparer.Ordinal)
        .Select(d => $"{d.Name} {{ "
            + string.Join("; ", d.Relations.Select(r => $"{r.Name}: " + string.Join(" | ", r.AllowedSubjects.Select(Render))))
            + " } perms[" + string.Join(",", d.Permissions) + "]")
        .ToList();

    private static string Render(AllowedSubject s) =>
        s.Type + (s.Relation is { } r ? "#" + r : "") + (s.IsWildcard ? ":*" : "") + (s.Caveat is { } c ? " with " + c : "");

    [Fact]
    public async Task The_engine_reflects_schema_zed_exactly_as_the_test_reader_parses_it()
    {
        var reflected = await spice.Schema.ReflectAsync(Ct);

        Assert.Equal(Render(SchemaModel.Load().Definitions), Render(reflected));
    }

    [Fact]
    public async Task The_model_derived_from_the_engine_is_the_agreed_table_of_resource_types_and_roles()
    {
        var model = AccessModel.FromSchema(await spice.Schema.ReflectAsync(Ct));

        Assert.Equal(["commerce/domain", "commerce/subscription", "commerce/wallet", "customer", "platform"], model.ResourceTypes);
        Assert.Equal(["administrator", "support"], model.GrantableRelations("platform"));
        Assert.Equal(["administrator", "viewer"], model.GrantableRelations("customer"));
        Assert.Equal(["administrator", "viewer"], model.GrantableRelations("commerce/domain"));
        Assert.Equal(["administrator", "spender", "viewer"], model.GrantableRelations("commerce/wallet"));
        Assert.Equal(["administrator", "operator", "viewer"], model.GrantableRelations("commerce/subscription"));
        Assert.Empty(model.GrantableRelations("group"));
    }

    [Fact]
    public async Task A_type_added_to_the_deployed_schema_appears_in_the_model_and_wildcards_and_caveats_are_reported()
    {
        // A private engine: the shared one must keep exactly schema.zed.
        await using var host = await NativeSpiceDbHost.StartAsync();
        using var connection = SpiceDbFixture.NewConnection(host);
        var store = new SpiceDbSchemaStore(connection);
        await store.WriteAsync(SpiceDbFixture.SchemaText + """

            caveat on_weekdays(weekday int) {
                weekday < 6
            }

            definition shipping/parcel {
                relation parent: customer
                relation handler: user | group#member
                relation public_reader: user:*
                relation weekday_handler: user with on_weekdays
                permission manage_access = handler + parent->admin
            }
            """, Ct);

        var reflected = await store.ReflectAsync(Ct);
        var parcel = Assert.Single(reflected, d => d.Name == "shipping/parcel");
        Assert.Equal(
            ["parent: customer", "handler: user | group#member", "public_reader: user:*", "weekday_handler: user with on_weekdays"],
            parcel.Relations.Select(r => $"{r.Name}: " + string.Join(" | ", r.AllowedSubjects.Select(Render))));

        var model = AccessModel.FromSchema(reflected);
        Assert.True(model.IsResourceType("shipping/parcel"));
        Assert.Equal(["handler"], model.GrantableRelations("shipping/parcel")); // the wildcard and the caveated relation are not roles
    }

    [Fact]
    public async Task An_engine_with_no_schema_reflects_nothing_so_the_model_is_empty()
    {
        await using var host = await NativeSpiceDbHost.StartAsync();
        using var connection = SpiceDbFixture.NewConnection(host);

        var reflected = await new SpiceDbSchemaStore(connection).ReflectAsync(Ct);

        Assert.Empty(reflected);
        Assert.Empty(AccessModel.FromSchema(reflected).ResourceTypes);
    }
}
