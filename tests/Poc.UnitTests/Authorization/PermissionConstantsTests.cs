using System.Reflection;
using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;

namespace Poc.UnitTests.Authorization;

/// <summary>
/// Fitness function (PRD): the permission and relation constants in Poc.Application and authz/schema.zed must
/// agree exactly, in both directions.
/// </summary>
public sealed class PermissionConstantsTests
{
    private static readonly SchemaModel Schema = SchemaModel.Load();

    private static readonly Type[] SchemaClasses = typeof(IAuthorizer).Assembly.GetTypes()
        .Where(t => t.Namespace == typeof(WalletSchema).Namespace && t.IsAbstract && t.IsSealed && t.Name.EndsWith("Schema", StringComparison.Ordinal))
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .ToArray();

    public static TheoryData<string> SchemaTypes => new(Schema.Types.Keys.Order());

    private static string TypeNameOf(Type schemaClass) => (string)schemaClass
        .GetField("Type", BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;

    private static SortedSet<string> ConstantsOf(Type schemaClass, string nested)
    {
        var holder = schemaClass.GetNestedType(nested, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{schemaClass.Name} has no nested class '{nested}'.");
        return new SortedSet<string>(
            holder.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!),
            StringComparer.Ordinal);
    }

    [Fact]
    public void Every_schema_type_has_exactly_one_constants_class_and_vice_versa()
    {
        var classesByType = SchemaClasses.ToDictionary(TypeNameOf, StringComparer.Ordinal);
        Assert.Equal(Schema.Types.Keys.Order(), classesByType.Keys.Order());
    }

    [Theory]
    [MemberData(nameof(SchemaTypes))]
    public void Constants_match_the_schema_for(string typeName)
    {
        var schemaClass = SchemaClasses.Single(c => TypeNameOf(c) == typeName);
        var (relations, permissions) = Schema.Types[typeName];

        Assert.Equal(relations, ConstantsOf(schemaClass, "Relations"));
        Assert.Equal(permissions, ConstantsOf(schemaClass, "Permissions"));
    }

    [Fact]
    public void Constants_are_unique_within_a_class_and_fully_lower_snake_case()
    {
        foreach (var schemaClass in SchemaClasses)
        {
            foreach (var nested in new[] { "Relations", "Permissions" })
            {
                var holder = schemaClass.GetNestedType(nested)!;
                var values = holder.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetRawConstantValue()!).ToList();
                Assert.Equal(values.Count, values.Distinct().Count());
                Assert.All(values, v => Assert.Matches("^[a-z][a-z_]*$", v));
            }
        }
    }

    [Fact]
    public void Constants_would_catch_drift_in_either_direction()
    {
        // Proves the comparison is able to fail: a schema with an extra permission differs from the constants.
        var drifted = SchemaModel.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "authz", "schema.zed"))
            .Replace("permission cancel = admin", "permission cancel = admin\n    permission refund = admin", StringComparison.Ordinal));
        var subscription = SchemaClasses.Single(c => TypeNameOf(c) == "subscription");

        Assert.NotEqual(drifted.Types["subscription"].Permissions, ConstantsOf(subscription, "Permissions"));
        Assert.Contains("refund", drifted.Types["subscription"].Permissions);
    }

    [Fact]
    public void Main_platform_ref_uses_the_constants()
    {
        Assert.Equal("platform:main", PlatformSchema.Main.ToString());
        Assert.Equal("group:uk-it#member", SubjectRef.GroupMembers("uk-it").ToString());
        Assert.Equal("user:alice", SubjectRef.User("alice").ToString());
    }
}
