using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Poc.Domain.Directory;

namespace Poc.DevIssuer;

/// <summary>
/// Signs tokens for the seeded users with an RSA key generated at start-up (RS256). The token carries no roles and no
/// groups: every resource-level decision comes from the authorisation engine, never from the token.
/// </summary>
public sealed class TokenFactory : IDisposable
{
    public const string UserIdClaim = "extension_userId";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly Dictionary<string, string> _subjects = SeedUsers.All.ToDictionary(u => u.Id, _ => Guid.NewGuid().ToString());

    public TokenFactory(string issuer, string audience, TimeSpan lifetime)
    {
        Issuer = issuer;
        Audience = audience;
        Lifetime = lifetime;
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = Guid.NewGuid().ToString("N")[..16] };
    }

    public string Issuer { get; }

    public string Audience { get; }

    public TimeSpan Lifetime { get; }

    public RsaSecurityKey SigningKey { get; }

    /// <summary>A token for a seeded user, or <c>null</c> if there is no such user.</summary>
    public (string Token, DateTimeOffset ExpiresAt)? IssueForUser(string userId)
    {
        var user = SeedUsers.All.FirstOrDefault(u => u.Id == userId);
        if (user.Id is null) return null;
        var claims = new Dictionary<string, object>
        {
            // A random GUID per user stands in for the B2C object id. The API ignores it for authorisation.
            ["sub"] = _subjects[user.Id],
            [UserIdClaim] = user.Id,
            ["name"] = user.DisplayName,
            ["emails"] = new[] { SeedUsers.EmailFor(user.Id) },
        };
        return Issue(claims, Lifetime);
    }

    /// <summary>
    /// Mints an arbitrary token signed by this issuer's key, for tests that need tokens a real user could never get
    /// (missing claim, wrong audience, expired). Never exposed over HTTP.
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAt) Issue(
        IDictionary<string, object> claims, TimeSpan lifetime, string? audience = null, string? issuer = null, SecurityKey? signingKey = null, string? algorithm = null)
    {
        var now = DateTime.UtcNow;
        var expires = now.Add(lifetime);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience ?? Audience,
            IssuedAt = now,
            NotBefore = lifetime < TimeSpan.Zero ? now.Add(lifetime).AddMinutes(-1) : now,
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, algorithm ?? SecurityAlgorithms.RsaSha256),
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    /// <summary>The public half of the signing key as a JWKS document.</summary>
    public object Jwks()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = SigningKey.KeyId });
        return new { keys = new[] { new { kty = jwk.Kty, use = "sig", alg = SecurityAlgorithms.RsaSha256, kid = SigningKey.KeyId, n = jwk.N, e = jwk.E } } };
    }

    public void Dispose() => _rsa.Dispose();
}
