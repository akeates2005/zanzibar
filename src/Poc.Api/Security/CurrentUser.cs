using Poc.Application.Common;

namespace Poc.Api.Security;

/// <summary>
/// The caller, taken only from the validated <c>extension_userId</c> claim. The token's <c>sub</c> is never used for
/// authorisation, and a token without the claim never gets this far (it is rejected with 401 at validation).
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor accessor)
{
    public const string UserIdClaim = "extension_userId";

    private Actor? _actor;

    public Actor Actor => _actor ??= Actor.ForUser(
        accessor.HttpContext?.User.FindFirst(UserIdClaim)?.Value ?? throw new UnauthorizedAccessException("No user id in the token."));

    public string UserId => Actor.UserId;

    /// <summary>Display value from the token. Shown in the shell only; never logged.</summary>
    public string? Name => accessor.HttpContext?.User.FindFirst("name")?.Value;

    public IReadOnlyList<string> Emails =>
        accessor.HttpContext?.User.FindAll("emails").Select(c => c.Value).ToList() ?? [];
}
