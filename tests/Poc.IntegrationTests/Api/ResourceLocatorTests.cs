using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Poc.Api.Security;
using Poc.Application.Authorization;

namespace Poc.IntegrationTests.Api;

/// <summary>How a request is turned into the object an endpoint's permission is checked on. No engine, no host.</summary>
public sealed class ResourceLocatorTests
{
    private static HttpRequest Request(Dictionary<string, string>? route = null, string? query = null)
    {
        var http = new DefaultHttpContext();
        foreach (var (key, value) in route ?? []) http.Request.RouteValues[key] = value;
        if (query is not null) http.Request.QueryString = new QueryString(query);
        return http.Request;
    }

    [Fact]
    public void A_fixed_type_with_an_id_from_the_route_resolves()
    {
        var resolved = ResourceLocator.Route("wallet").Resolve(Request(new() { ["id"] = "uk-ops" }));
        Assert.Equal(new ResourceRef("wallet", "uk-ops"), resolved);
    }

    [Fact]
    public void The_platform_locator_needs_nothing_from_the_request()
    {
        Assert.Equal(new ResourceRef("platform", "main"), ResourceLocator.Platform.Resolve(Request()));
    }

    [Fact]
    public void A_type_named_in_the_route_must_be_one_of_the_known_object_types()
    {
        var locator = ResourceLocator.RouteTyped("type");
        foreach (var type in new[] { "platform", "customer", "domain", "wallet", "subscription" })
            Assert.Equal(new ResourceRef(type, "x1"), locator.Resolve(Request(new() { ["type"] = type, ["id"] = "x1" })));

        foreach (var type in new[] { "group", "user", "nonsense", "", "Customer", "customer ", "customer:acme" })
            Assert.Null(locator.Resolve(Request(new() { ["type"] = type, ["id"] = "x1" })));
    }

    [Theory]
    [InlineData("Bad_Upper")]
    [InlineData("a#member")]
    [InlineData("a:b")]
    [InlineData("a@b")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("a b")]
    public void A_malformed_id_cannot_exist_so_it_never_resolves(string id)
    {
        Assert.Null(ResourceLocator.Route("customer").Resolve(Request(new() { ["id"] = id })));
        Assert.Null(ResourceLocator.QueryTyped("type", "id").Resolve(Request(query: $"?type=customer&id={Uri.EscapeDataString(id)}")));
    }

    [Fact]
    public void A_missing_route_value_or_query_parameter_does_not_resolve()
    {
        Assert.Null(ResourceLocator.Route("customer").Resolve(Request()));
        Assert.Null(ResourceLocator.QueryTyped("type", "id").Resolve(Request(query: "?type=customer")));
        Assert.Null(ResourceLocator.QueryTyped("type", "id").Resolve(Request(query: "?id=acme")));
    }

    [Fact]
    public void A_query_locator_reads_the_query_string_and_ignores_route_values()
    {
        var locator = ResourceLocator.QueryTyped("type", "id");
        Assert.Equal(new ResourceRef("domain", "acme-uk"), locator.Resolve(Request(new() { ["id"] = "ignored" }, "?type=domain&id=acme-uk")));
    }

    [Fact]
    public void Repeated_query_values_do_not_smuggle_a_second_object()
    {
        // "?id=acme&id=globex" arrives as one comma-joined value, which is not a valid id, so it resolves to nothing.
        var http = new DefaultHttpContext();
        http.Request.Query = new QueryCollection(new Dictionary<string, StringValues> { ["type"] = "customer", ["id"] = new StringValues(["acme", "globex"]) });
        Assert.Null(ResourceLocator.QueryTyped("type", "id").Resolve(http.Request));
    }
}
