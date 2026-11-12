using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// In containers the browser reaches the issuer as localhost:5090, so tokens name that as their issuer, but the API reaches
/// it as http://issuer:5090. The public name here does not resolve at all, so the API can only work if it fetches discovery
/// from the configured metadata address and the issuer points at keys on the address it was asked on.
/// </summary>
public sealed class IssuerNamingTests : ApiTestBase
{
    private const string PublicName = "http://issuer.invalid:5090";

    public override async Task InitializeAsync() => Host = await ApiHost.StartAsync(publicIssuer: PublicName);

    [Fact]
    public async Task The_api_validates_tokens_whose_issuer_is_a_public_name_it_cannot_reach()
    {
        var me = await (await (await AsAsync("alice")).GetAsync("/api/me")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("alice", me.GetProperty("userId").GetString());
        await (await (await AsAsync("alice")).GetAsync("/api/customers/acme")).ExpectAsync(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_issuer_names_itself_publicly_but_points_at_keys_on_the_address_it_was_asked_on()
    {
        using var http = new HttpClient();
        var discovery = (await http.GetFromJsonAsync<JsonElement>($"{Host.IssuerUrl}/.well-known/openid-configuration"));

        Assert.Equal(PublicName, discovery.GetProperty("issuer").GetString());
        Assert.Equal($"{Host.IssuerUrl}/.well-known/jwks.json", discovery.GetProperty("jwks_uri").GetString());
    }

    [Fact]
    public async Task A_token_claiming_an_issuer_other_than_the_public_name_is_still_rejected()
    {
        var token = Host.Tokens.Issue(new Dictionary<string, object> { ["extension_userId"] = "alice" }, TimeSpan.FromMinutes(5), issuer: Host.IssuerUrl).Token;
        using var client = Host.ClientWithToken(token);
        await (await client.GetAsync("/api/me")).ExpectProblemAsync(HttpStatusCode.Unauthorized);
    }
}
