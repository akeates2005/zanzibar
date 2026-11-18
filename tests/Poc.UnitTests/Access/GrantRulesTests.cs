using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;
using Poc.Domain.Directory;
using Poc.UnitTests.Authorization;

namespace Poc.UnitTests.Access;

public sealed class GrantRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static Group CustomerGroup(string id, string owner) => Group.Create(GroupOwnerType.Customer, owner, id, "a", Now, id);

    private static Group PlatformGroup(string id) => Group.Create(GroupOwnerType.Platform, "main", id, "a", Now, id);

    // ---- relations ----------------------------------------------------------------------------------------------

    /// <summary>The model the app derives from authz/schema.zed (the integration tests prove the engine reflects the same).</summary>
    private static readonly AccessModel Model = AccessModel.FromSchema(SchemaModel.Load().Definitions);

    [Fact]
    public void Grantable_relations_derived_from_the_schema_are_exactly_the_agreed_roles()
    {
        // Fitness function: the subject-type rule must give the table that was hand-written before it was derived
        // (DECISIONS.md, "Access model read from the engine"). A schema change that alters a role must change this test.
        var expected = new Dictionary<string, string[]>
        {
            ["platform"] = ["administrator", "support"],
            ["customer"] = ["administrator", "viewer"],
            ["commerce/domain"] = ["administrator", "viewer"],
            ["commerce/wallet"] = ["administrator", "spender", "viewer"],
            ["commerce/subscription"] = ["administrator", "operator", "viewer"],
        };
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), Model.ResourceTypes);
        foreach (var (type, roles) in expected)
            Assert.Equal(roles, Model.GrantableRelations(type));

        Assert.Empty(Model.GrantableRelations("group")); // membership has its own command
        Assert.Empty(Model.GrantableRelations("user"));
    }

    [Theory]
    [InlineData("customer", "viewer")]
    [InlineData("commerce/wallet", "spender")]
    [InlineData("commerce/subscription", "operator")]
    [InlineData("platform", "support")]
    public void A_grantable_relation_passes(string type, string relation) => GrantRules.RequireGrantableRelation(Model, type, relation);

    [Theory]
    [InlineData("customer", "parent")]      // structural: only the projector writes it
    [InlineData("commerce/wallet", "owner")]
    [InlineData("customer", "operator")]    // exists on subscription, not customer
    [InlineData("commerce/domain", "spender")]
    [InlineData("commerce/wallet", "no_such_relation")]
    [InlineData("commerce/wallet", "view")]          // a permission, not a relation
    [InlineData("group", "member")]
    [InlineData("nonsense", "viewer")]
    public void Anything_else_is_refused_with_a_stable_code(string type, string relation)
    {
        var ex = Assert.Throws<RuleViolationException>(() => GrantRules.RequireGrantableRelation(Model, type, relation));
        Assert.Equal(GrantRules.Codes.UnknownRelation, ex.Code);
    }

    [Fact]
    public void Before_a_schema_is_loaded_nothing_is_grantable()
    {
        var ex = Assert.Throws<RuleViolationException>(() => GrantRules.RequireGrantableRelation(AccessModel.Empty, "customer", "viewer"));
        Assert.Equal(GrantRules.Codes.UnknownRelation, ex.Code);
    }

    // ---- subjects -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_user_or_a_groups_members_are_valid_subjects()
    {
        GrantRules.RequireValidSubjectShape(SubjectRef.User("alice"));
        GrantRules.RequireValidSubjectShape(SubjectRef.GroupMembers("uk-it"));
    }

    [Theory]
    [InlineData("user", "alice", "member")]      // a user has no members
    [InlineData("group", "uk-it", null)]         // a bare group object is not a subject; its members are
    [InlineData("group", "uk-it", "manage")]     // wrong relation
    [InlineData("customer", "acme", null)]       // objects other than users and groups cannot be subjects
    [InlineData("platform", "main", "administrator")]
    public void Anything_else_is_refused(string type, string id, string? relation)
    {
        var ex = Assert.Throws<RuleViolationException>(() => GrantRules.RequireValidSubjectShape(new SubjectRef(type, id, relation)));
        Assert.Equal(GrantRules.Codes.InvalidSubject, ex.Code);
    }

    // ---- rule 1: group scope ------------------------------------------------------------------------------------

    [Fact]
    public void Rule1_a_customer_group_may_be_granted_under_its_own_customer()
        => GrantRules.RequireGroupInScope(CustomerGroup("uk-it", "acme"), "acme");

    [Fact]
    public void Rule1_a_customer_group_may_not_be_granted_under_another_customer()
    {
        var ex = Assert.Throws<RuleViolationException>(() => GrantRules.RequireGroupInScope(CustomerGroup("uk-it", "acme"), "globex"));
        Assert.Equal(GrantRules.Codes.GroupOwnerMismatch, ex.Code);
    }

    [Fact]
    public void Rule1_a_customer_group_may_not_be_granted_on_the_platform_which_has_no_root_customer()
    {
        var ex = Assert.Throws<RuleViolationException>(() => GrantRules.RequireGroupInScope(CustomerGroup("uk-it", "acme"), null));
        Assert.Equal(GrantRules.Codes.GroupOwnerMismatch, ex.Code);
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("globex")]
    [InlineData(null)]
    public void Rule1_a_platform_group_passes_the_scope_rule_anywhere(string? rootCustomer)
        => GrantRules.RequireGroupInScope(PlatformGroup("support-team"), rootCustomer);

    // ---- rule 2: nesting ----------------------------------------------------------------------------------------

    [Fact]
    public void Rule2_groups_with_the_same_owner_may_nest()
    {
        GrantRules.RequireNestable(CustomerGroup("a", "acme"), CustomerGroup("b", "acme"));
        GrantRules.RequireNestable(PlatformGroup("a"), PlatformGroup("b"));
    }

    [Fact]
    public void Rule2_groups_with_different_owners_may_not_nest_in_either_direction()
    {
        foreach (var (container, child) in new[]
                 {
                     (CustomerGroup("a", "acme"), CustomerGroup("b", "globex")),
                     (CustomerGroup("a", "acme"), PlatformGroup("b")),
                     (PlatformGroup("a"), CustomerGroup("b", "acme")),
                 })
        {
            var ex = Assert.Throws<RuleViolationException>(() => GrantRules.RequireNestable(container, child));
            Assert.Equal(GrantRules.Codes.GroupOwnerMismatch, ex.Code);
        }
    }

    [Fact]
    public void A_group_may_not_be_nested_in_itself()
    {
        var ex = Assert.Throws<RuleViolationException>(() => GrantRules.RequireNestable(CustomerGroup("a", "acme"), CustomerGroup("a", "acme")));
        Assert.Equal(GrantRules.Codes.GroupSelfNesting, ex.Code);
    }

    // ---- actors -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_system_actor_is_distinct_and_cannot_be_claimed_by_a_user_id()
    {
        Assert.True(Actor.System.IsSystem);
        Assert.False(Actor.ForUser("alice").IsSystem);
        Assert.Throws<ArgumentException>(() => Actor.ForUser("system"));
        Assert.Throws<ArgumentException>(() => Actor.ForUser(" "));
    }
}
