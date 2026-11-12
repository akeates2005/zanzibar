using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Domain.Common;

namespace Poc.Api.Security;

/// <summary>Where an endpoint's target object is named in the request.</summary>
public enum LocatorSource
{
    Route,
    Query,
}

/// <summary>
/// How to find the object an endpoint acts on: a fixed type or one named in the request, and an id from the route or
/// query. For a create route this is the parent, because the check for a create is made on the parent (PRD rule 4).
/// </summary>
public sealed record ResourceLocator(string? FixedType, string? TypeKey, string? FixedId, string? IdKey, LocatorSource Source)
{
    /// <summary>Types a request may name for itself, where the type is not fixed by the route.</summary>
    public static readonly IReadOnlyList<string> NamedTypes =
        [PlatformSchema.Type, CustomerSchema.Type, DomainSchema.Type, WalletSchema.Type, SubscriptionSchema.Type];

    public static ResourceLocator Route(string type, string idKey = "id") => new(type, null, null, idKey, LocatorSource.Route);

    public static ResourceLocator RouteTyped(string typeKey, string idKey = "id") => new(null, typeKey, null, idKey, LocatorSource.Route);

    public static ResourceLocator QueryTyped(string typeKey, string idKey) => new(null, typeKey, null, idKey, LocatorSource.Query);

    public static ResourceLocator Platform { get; } = new(PlatformSchema.Type, null, PlatformSchema.MainId, null, LocatorSource.Route);

    /// <summary>The object, or <c>null</c> when the request names an unknown type or a malformed id (which cannot exist).</summary>
    public ResourceRef? Resolve(HttpRequest request)
    {
        string? Read(string key) => Source == LocatorSource.Route
            ? request.RouteValues.TryGetValue(key, out var v) ? v?.ToString() : null
            : request.Query.TryGetValue(key, out var q) ? q.ToString() : null;

        var type = FixedType ?? (TypeKey is null ? null : Read(TypeKey));
        if (type is null || (FixedType is null && !NamedTypes.Contains(type))) return null;

        var id = FixedId ?? (IdKey is null ? null : Read(IdKey));
        // A malformed id cannot exist, so it is "not found" without bothering the engine.
        return id is not null && IsValidId(id) ? new ResourceRef(type, id) : null;
    }

    private static bool IsValidId(string id)
    {
        try
        {
            EntityId.Validate(id);
            return true;
        }
        catch (DomainRuleException)
        {
            return false;
        }
    }
}

/// <summary>
/// The access declaration every endpoint must carry as metadata. One filter enforces it, and a test fails if any
/// endpoint lacks one, so an unprotected route cannot ship unnoticed.
/// </summary>
public abstract record AccessRequirement
{
    /// <summary>No authentication: health and the API description only.</summary>
    public sealed record Anonymous(string Reason) : AccessRequirement;

    /// <summary>Any signed-in user: the caller's own profile, the tree of what they may see, the user picker.</summary>
    public sealed record Authenticated(string Reason) : AccessRequirement;

    /// <summary>
    /// The object is named in the request body, which a filter cannot read without consuming it, so the handler checks
    /// through <see cref="ResourceAccess"/> before doing anything. A test proves each such endpoint refuses.
    /// </summary>
    public sealed record HandlerChecked(string Permission, string Reason) : AccessRequirement;

    /// <summary>
    /// <paramref name="Permission"/> on the located object. The filter checks <c>view</c> and the permission together:
    /// no view is 404 (existence is never leaked), view without the permission is 403 naming it.
    /// </summary>
    public sealed record OnResource(string Permission, ResourceLocator Locator) : AccessRequirement;
}

public static class AccessRequirementExtensions
{
    public static RouteHandlerBuilder Requires(this RouteHandlerBuilder builder, AccessRequirement requirement) =>
        builder.WithMetadata(requirement);

    public static RouteHandlerBuilder RequiresOn(this RouteHandlerBuilder builder, string permission, ResourceLocator locator) =>
        builder.WithMetadata(new AccessRequirement.OnResource(permission, locator));
}
