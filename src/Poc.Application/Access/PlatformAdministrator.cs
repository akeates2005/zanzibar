using Poc.Application.Authorization;
using Poc.Application.Authorization.Schema;
using Poc.Application.Common;

namespace Poc.Application.Access;

/// <summary>
/// The one statement of "is this caller a platform administrator" for the access rules: <c>admin</c> on
/// <c>platform:main</c>. The system actor counts as one, as it bypasses every permission check.
/// </summary>
public static class PlatformAdministrator
{
    public static async Task<bool> IsAsync(IAuthorizer authorizer, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        return actor.IsSystem
            || await authorizer.CheckAsync(actor.Subject, PlatformSchema.Permissions.Admin, PlatformSchema.Main, ct).ConfigureAwait(false);
    }

    /// <summary>Throws the same permission-denied error a failed endpoint check gives.</summary>
    public static async Task RequireAsync(IAuthorizer authorizer, Actor actor, CancellationToken ct)
    {
        if (!await IsAsync(authorizer, actor, ct).ConfigureAwait(false))
            throw new PermissionDeniedException(PlatformSchema.Permissions.Admin, PlatformSchema.Main);
    }
}
