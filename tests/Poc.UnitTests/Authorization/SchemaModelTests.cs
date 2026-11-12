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
    public void The_shipped_schema_defines_the_seven_object_types()
    {
        var model = SchemaModel.Load();
        Assert.Equal(["customer", "domain", "group", "platform", "subscription", "user", "wallet"], model.Types.Keys.Order());
        Assert.Contains("change_quantity", model.Types["subscription"].Permissions);
    }
}
