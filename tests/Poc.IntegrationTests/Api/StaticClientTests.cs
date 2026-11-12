using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Poc.Api.Security;

namespace Poc.IntegrationTests.Api;

/// <summary>The API serving the built client (the container image puts it in wwwroot), without ever shadowing the API.</summary>
public sealed class StaticClientTests : ApiTestBase
{
    private string _webRoot = "";

    public override async Task InitializeAsync()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), $"poc-webroot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>client</title><div id=\"root\"></div>");
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "assets", "app.js"), "console.log('client')");
        Host = await ApiHost.StartAsync(webRoot: _webRoot);
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_webRoot, recursive: true);
    }

    private HttpClient Anonymous() => Host.ClientWithToken(null);

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/resources")]
    [InlineData("/resources/customer/acme")]
    [InlineData("/customers/acme/groups")]
    [InlineData("/explorer")]
    public async Task The_clients_pages_and_its_routes_all_serve_the_entry_page_so_a_reload_works(string path)
    {
        using var client = Anonymous();
        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<div id=\"root\">", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Static_assets_are_served_as_themselves_not_as_the_entry_page()
    {
        using var client = Anonymous();
        using var response = await client.GetAsync("/assets/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("console.log('client')", await response.Content.ReadAsStringAsync());
        Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/no/such/route")]
    [InlineData("/api/customers/does-not-exist")]
    public async Task Unknown_api_paths_stay_json_problems_never_the_web_page(string path)
    {
        var alice = await AsAsync("alice");
        using var response = await alice.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_api_still_demands_authentication_and_health_and_the_description_are_not_shadowed()
    {
        using var client = Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        var health = await (await client.GetAsync("/health")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("healthy", health.GetProperty("status").GetString());
        var openApi = await (await client.GetAsync("/openapi/v1.json")).ExpectAsync(HttpStatusCode.OK);
        Assert.True(openApi.TryGetProperty("paths", out _));
    }

    [Theory]
    [InlineData("/..%2f..%2fappsettings.json")]
    [InlineData("/%2e%2e/%2e%2e/appsettings.json")]
    [InlineData("/assets/../../appsettings.json")]
    public async Task Path_traversal_cannot_reach_files_outside_the_web_root(string path)
    {
        using var client = Anonymous();
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("PresharedKey", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Authentication\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_endpoint_including_the_clients_fallback_declares_its_access()
    {
        var routes = Host.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();

        Assert.All(routes, e => Assert.NotNull(e.Metadata.GetMetadata<AccessRequirement>()));
        var anonymous = routes.Where(e => e.Metadata.GetMetadata<AccessRequirement>() is AccessRequirement.Anonymous).Select(e => e.RoutePattern.RawText).ToList();
        Assert.Equal(3, anonymous.Count); // health, the API description, and the client's entry page
    }
}
