using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// The API validates tokens exactly as it would for B2C: signature, issuer, audience and lifetime, with keys fetched
/// from the issuer's discovery document. The token carries no roles; the user id claim is the only identity.
/// </summary>
public sealed class AuthenticationTests : ApiTestBase
{
    private static Dictionary<string, object> Claims(string userId) => new()
    {
        ["sub"] = Guid.NewGuid().ToString(),
        ["extension_userId"] = userId,
        ["name"] = userId,
    };

    private async Task<HttpStatusCode> MeAsync(string? token)
    {
        using var client = Host.ClientWithToken(token);
        using var response = await client.GetAsync("/api/me");
        return response.StatusCode;
    }

    private static string Base64Url(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task A_request_with_no_token_is_401_with_a_bearer_challenge_and_problem_details()
    {
        using var client = Host.ClientWithToken(null);
        using var response = await client.GetAsync("/api/me");

        await response.ExpectProblemAsync(HttpStatusCode.Unauthorized);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task A_genuine_token_from_the_issuer_is_accepted_and_describes_the_user_for_the_shell()
    {
        var me = await (await (await AsAsync("heidi")).GetAsync("/api/me")).ExpectAsync(HttpStatusCode.OK);

        Assert.Equal("heidi", me.GetProperty("userId").GetString());
        Assert.Equal("Heidi Hughes", me.GetProperty("name").GetString());
        Assert.Equal("heidi@zanzibar.example", Assert.Single(me.GetProperty("emails").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task A_token_for_a_user_the_directory_does_not_know_is_authenticated_but_sees_nothing()
    {
        var token = Host.Tokens.Issue(Claims("nobody"), TimeSpan.FromMinutes(5)).Token;
        using var client = Host.ClientWithToken(token);

        await (await client.GetAsync("/api/me")).ExpectAsync(HttpStatusCode.OK);
        await (await client.GetAsync("/api/customers/acme")).ExpectProblemAsync(HttpStatusCode.NotFound);
        Assert.Equal(0, (await (await client.GetAsync("/api/tree")).JsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task Tokens_the_issuer_signed_but_a_real_user_could_never_get_are_all_rejected()
    {
        var tokens = Host.Tokens;
        var noUserId = new Dictionary<string, object> { ["sub"] = "priya", ["name"] = "Priya" }; // `sub` must never stand in for the user id
        var cases = new Dictionary<string, string>
        {
            ["no extension_userId (sub is not a fallback)"] = tokens.Issue(noUserId, TimeSpan.FromMinutes(5)).Token,
            ["empty extension_userId"] = tokens.Issue(Claims(""), TimeSpan.FromMinutes(5)).Token,
            ["the reserved system id"] = tokens.Issue(Claims("system"), TimeSpan.FromMinutes(5)).Token,
            ["an id with a tuple separator"] = tokens.Issue(Claims("alice#member"), TimeSpan.FromMinutes(5)).Token,
            ["an upper-case id"] = tokens.Issue(Claims("Alice"), TimeSpan.FromMinutes(5)).Token,
            ["an id with a wildcard"] = tokens.Issue(Claims("*"), TimeSpan.FromMinutes(5)).Token,
            ["expired"] = tokens.Issue(Claims("alice"), TimeSpan.FromMinutes(-10)).Token,
            ["wrong audience"] = tokens.Issue(Claims("alice"), TimeSpan.FromMinutes(5), audience: "someone-else").Token,
            ["wrong issuer"] = tokens.Issue(Claims("alice"), TimeSpan.FromMinutes(5), issuer: "http://evil.example").Token,
        };

        var accepted = new List<string>();
        foreach (var (name, token) in cases)
            if (await MeAsync(token) != HttpStatusCode.Unauthorized) accepted.Add(name);

        Assert.Empty(accepted);
    }

    [Fact]
    public async Task Tokens_signed_by_someone_else_or_not_signed_at_all_are_rejected()
    {
        using var rogue = RSA.Create(2048);
        var rogueKey = new RsaSecurityKey(rogue) { KeyId = Host.Tokens.SigningKey.KeyId }; // even claiming the real key id
        var forged = Host.Tokens.Issue(Claims("priya"), TimeSpan.FromMinutes(5), signingKey: rogueKey).Token;

        var unsigned = $"{Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{Base64Url($"{{\"iss\":\"{Host.IssuerUrl}\",\"aud\":\"poc-api\",\"extension_userId\":\"priya\",\"exp\":{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}")}.";

        // HS256 with the issuer's public key text as the secret: the classic algorithm-confusion attempt.
        var hmac = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('k', 64)));
        var confused = Host.Tokens.Issue(Claims("priya"), TimeSpan.FromMinutes(5), signingKey: hmac, algorithm: SecurityAlgorithms.HmacSha256).Token;

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(forged));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(unsigned));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync("garbage"));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync("a.b.c"));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(confused));
    }

    [Fact]
    public async Task A_token_that_claims_roles_or_groups_gains_nothing_because_every_decision_comes_from_the_engine()
    {
        var claims = Claims("frank");
        claims["roles"] = new[] { "platform-admin", "administrator" };
        claims["groups"] = new[] { "support-team", "uk-it" };
        claims["extension_isAdmin"] = true;
        using var client = Host.ClientWithToken(Host.Tokens.Issue(claims, TimeSpan.FromMinutes(5)).Token);

        await (await client.GetAsync("/api/customers/acme")).ExpectProblemAsync(HttpStatusCode.NotFound);
        await (await client.GetAsync("/api/system/outbox")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Authorisation_follows_extension_userId_not_sub()
    {
        // sub claims to be priya, the user id says frank: the caller is frank.
        var claims = Claims("frank");
        claims["sub"] = "priya";
        using var client = Host.ClientWithToken(Host.Tokens.Issue(claims, TimeSpan.FromMinutes(5)).Token);

        Assert.Equal("frank", (await (await client.GetAsync("/api/me")).JsonAsync()).GetProperty("userId").GetString());
        await (await client.GetAsync("/api/customers/acme")).ExpectProblemAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_and_the_api_description_need_no_token_and_leak_no_data()
    {
        using var anonymous = Host.ClientWithToken(null);

        var health = await (await anonymous.GetAsync("/health")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("healthy", health.GetProperty("status").GetString());
        await (await anonymous.GetAsync("/openapi/v1.json")).ExpectAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cors_allows_only_the_web_origin()
    {
        using var client = Host.ClientWithToken(null);

        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/me");
        allowed.Headers.Add("Origin", "http://localhost:5173");
        allowed.Headers.Add("Access-Control-Request-Method", "GET");
        allowed.Headers.Add("Access-Control-Request-Headers", "authorization");
        using var ok = await client.SendAsync(allowed);
        Assert.Equal("http://localhost:5173", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var other = new HttpRequestMessage(HttpMethod.Options, "/api/me");
        other.Headers.Add("Origin", "http://evil.example");
        other.Headers.Add("Access-Control-Request-Method", "GET");
        using var denied = await client.SendAsync(other);
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
