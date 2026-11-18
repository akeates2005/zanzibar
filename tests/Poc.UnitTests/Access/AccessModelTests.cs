using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.UnitTests.Authorization;

namespace Poc.UnitTests.Access;

/// <summary>The rules that turn a reflected schema into resource types and grantable roles, one rule per test.</summary>
public sealed class AccessModelTests
{
    private static AccessModel Derive(string schema) => AccessModel.FromSchema(SchemaModel.Parse(schema).Definitions);

    private const string People = """
        definition user {}
        definition group {
            relation owner: platform
            relation member: user | group#member
        }
        definition platform {
            relation administrator: user | group#member
            permission manage_access = administrator
        }
        """;

    [Fact]
    public void A_type_with_a_parent_view_and_manage_access_is_a_resource_type_without_any_code_change()
    {
        var model = Derive(People + """
            definition shipping/parcel {
                relation parent: platform
                relation handler: user | group#member
                permission view = handler
                permission manage_access = handler
            }
            """);
        Assert.Equal(["platform", "shipping/parcel"], model.ResourceTypes);
        Assert.Equal(["handler"], model.GrantableRelations("shipping/parcel"));
    }

    [Fact]
    public void Platform_is_a_resource_type_and_group_and_user_are_not()
    {
        var model = Derive(People);
        Assert.True(model.IsResourceType("platform"));
        Assert.False(model.IsResourceType("group"));   // person-shaped member relation, but groups have their own commands
        Assert.False(model.IsResourceType("user"));
        Assert.Empty(model.GrantableRelations("group"));
    }

    [Theory]
    [InlineData("relation parent: platform\n relation handler: user | group#member\n permission view = handler")]        // no manage_access
    [InlineData("relation handler: user | group#member\n permission view = handler\n permission manage_access = handler")] // no parent
    [InlineData("relation parent: platform\n relation handler: user | group#member\n permission manage_access = handler")] // no view: the filter checks it
    public void A_type_missing_parent_view_or_manage_access_is_not_a_resource_type(string body)
    {
        var model = Derive(People + "definition shipping/parcel {\n" + body + "\n}");
        Assert.False(model.IsResourceType("shipping/parcel"));
        Assert.Empty(model.GrantableRelations("shipping/parcel"));
    }

    [Theory]
    [InlineData("user", true)]
    [InlineData("group#member", true)]
    [InlineData("user | group#member", true)]
    [InlineData("platform", false)]                  // points at an object: structural
    [InlineData("user | platform", false)]           // one non-person subject is enough to make it structural
    [InlineData("user:*", false)]                    // a wildcard is not a person
    [InlineData("user | user:*", false)]
    [InlineData("user with on_weekdays", false)]     // grants carry no caveat context, so a caveated relation is not offered
    [InlineData("group", false)]                     // a bare group object, not its members
    [InlineData("group#owner", false)]
    public void A_relation_is_a_role_only_when_every_allowed_subject_is_a_person(string subjects, bool isRole)
    {
        var model = Derive(People + $$"""
            definition shipping/parcel {
                relation parent: platform
                relation handler: {{subjects}}
                permission view = parent->manage_access
                permission manage_access = parent->manage_access
            }
            """);
        Assert.Equal(isRole, model.IsGrantable("shipping/parcel", "handler"));
        Assert.False(model.IsGrantable("shipping/parcel", "parent"));
    }

    [Fact]
    public void The_empty_model_resolves_nothing()
    {
        Assert.Empty(AccessModel.Empty.ResourceTypes);
        Assert.False(AccessModel.Empty.IsResourceType("platform"));
        Assert.Empty(AccessModel.Empty.GrantableRelations("platform"));
    }

    [Fact]
    public void Roles_keep_schema_order()
    {
        var model = Derive(People + """
            definition shipping/parcel {
                relation parent: platform
                relation zeta: user
                relation alpha: user
                permission view = zeta
                permission manage_access = zeta
            }
            """);
        Assert.Equal(["zeta", "alpha"], model.GrantableRelations("shipping/parcel"));
    }

    [Fact]
    public async Task The_source_is_empty_until_loaded_then_holds_what_the_engine_reflects()
    {
        var source = new AccessModelSource(new FixedSchema(SchemaModel.Load().Definitions));
        Assert.Same(AccessModel.Empty, source.Current);

        var loaded = await source.LoadAsync(CancellationToken.None);

        Assert.Same(loaded, source.Current);
        Assert.True(source.Current.IsGrantable("commerce/wallet", "spender"));
    }

    private sealed class FixedSchema(IReadOnlyList<ReflectedDefinition> definitions) : ISchemaStore
    {
        public Task WriteAsync(string schemaText, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> ReadAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ReflectedDefinition>> ReflectAsync(CancellationToken ct) => Task.FromResult(definitions);
    }
}
