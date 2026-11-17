using Poc.UnitTests.Authorization;

namespace Poc.UnitTests.Authorization;

/// <summary>The parser is what the fitness function relies on, so it gets its own tests.</summary>
public sealed class SchemaModelTests
{
    [Fact]
    public void Parses_relations_permissions_and_empty_definitions()
    {
        var model = SchemaModel.Parse("""
            definition user {}
            definition thing {
                relation owner: user | group#member
                relation viewer: user
                permission view = viewer + owner->admin
                permission edit = owner
            }
            """);

        Assert.Equal(["thing", "user"], model.Types.Keys.Order());
        Assert.Empty(model.Types["user"].Relations);
        Assert.Equal(["owner", "viewer"], model.Types["thing"].Relations);
        Assert.Equal(["edit", "view"], model.Types["thing"].Permissions);
    }

    [Fact]
    public void Parses_a_prefixed_definition_and_references_to_it()
    {
        // A `\w+` name pattern skipped these silently; the constants test would then have compared against a schema
        // missing every prefixed type.
        var model = SchemaModel.Parse("""
            definition user {}
            definition team/thing {
                relation parent: team/other
                permission view = parent->view
            }
            """);

        Assert.Equal(["team/thing", "user"], model.Types.Keys.Order());
        Assert.Equal(["parent"], model.Types["team/thing"].Relations);
        Assert.Equal(["view"], model.Types["team/thing"].Permissions);
    }

    [Fact]
    public void The_shipped_schema_defines_the_seven_object_types()
    {
        // The platform's shared types are unprefixed; the business objects carry the `commerce/` prefix.
        var model = SchemaModel.Load();
        Assert.Equal(
            ["commerce/domain", "commerce/subscription", "commerce/wallet", "customer", "group", "platform", "user"],
            model.Types.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("change_quantity", model.Types["commerce/subscription"].Permissions);
    }
}
