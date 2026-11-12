using Poc.Application.Authorization.Schema;

namespace Poc.Application.Access;

/// <summary>
/// The relations an administrator may assign on each object type. Structural relations (<c>parent</c>, <c>owner</c>)
/// are written only by the projector and are deliberately absent. A unit test keeps this in step with the schema.
/// </summary>
public static class GrantableRelations
{
    private static readonly Dictionary<string, string[]> ByType = new(StringComparer.Ordinal)
    {
        [PlatformSchema.Type] = [PlatformSchema.Relations.Administrator, PlatformSchema.Relations.Support],
        [CustomerSchema.Type] = [CustomerSchema.Relations.Administrator, CustomerSchema.Relations.Viewer],
        [DomainSchema.Type] = [DomainSchema.Relations.Administrator, DomainSchema.Relations.Viewer],
        [WalletSchema.Type] = [WalletSchema.Relations.Administrator, WalletSchema.Relations.Spender, WalletSchema.Relations.Viewer],
        [SubscriptionSchema.Type] = [SubscriptionSchema.Relations.Administrator, SubscriptionSchema.Relations.Operator, SubscriptionSchema.Relations.Viewer],
    };

    public static IReadOnlyCollection<string> ResourceTypes => ByType.Keys;

    /// <summary>The grantable relations for an object type, or an empty list if the type takes no direct grants.</summary>
    public static IReadOnlyList<string> For(string resourceType) =>
        ByType.TryGetValue(resourceType, out var relations) ? relations : [];

    public static bool IsGrantable(string resourceType, string relation) => For(resourceType).Contains(relation);
}
