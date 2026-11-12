using System.Reflection;
using Poc.Application.Authorization.Schema;

namespace Poc.Application.Authorization;

/// <summary>The permissions each object type defines, read from the constants that a test keeps equal to the schema.</summary>
public static class SchemaCatalog
{
    private static readonly Dictionary<string, string[]> PermissionsByType = typeof(SchemaCatalog).Assembly.GetTypes()
        .Where(t => t.Namespace == typeof(WalletSchema).Namespace && t.IsAbstract && t.IsSealed && t.Name.EndsWith("Schema", StringComparison.Ordinal))
        .ToDictionary(
            t => (string)t.GetField("Type", BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!,
            t => t.GetNestedType("Permissions")!
                .GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral)
                .Select(f => (string)f.GetRawConstantValue()!).ToArray(),
            StringComparer.Ordinal);

    /// <summary>Every permission of the type, in declaration order; empty for an unknown type.</summary>
    public static IReadOnlyList<string> PermissionsOf(string objectType) =>
        PermissionsByType.TryGetValue(objectType, out var permissions) ? permissions : [];

    public static bool IsKnownType(string objectType) => PermissionsByType.ContainsKey(objectType);

    public static bool HasPermission(string objectType, string permission) => PermissionsOf(objectType).Contains(permission);
}
