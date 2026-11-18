using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Poc.Api.Security;
using Poc.Application.Access;
using Poc.Application.Authorization;
using Poc.UnitTests.Authorization;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// How a request is turned into the object an endpoint's permission is checked on. No engine, no host: the access model is
/// derived from schema.zed by the test reader, which <c>SchemaReflectionTests</c> proves matches what the engine reflects.
/// </summary>
public sealed class ResourceLocatorTests
{
    private static readonly AccessModel Model = AccessModel.FromSchema(SchemaModel.Load().Definitions);

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
        var resolved = ResourceLocator.Route("commerce/wallet").Resolve(Request(new() { ["id"] = "uk-ops" }), Model);
        Assert.Equal(new ResourceRef("commerce/wallet", "uk-ops"), resolved);
    }

    [Fact]
    public void The_platform_locator_needs_nothing_from_the_request()
    {
        Assert.Equal(new ResourceRef("platform", "main"), ResourceLocator.Platform.Resolve(Request(), Model));
    }

    [Fact]
    public void A_type_named_in_the_request_must_be_one_of_the_known_object_types()
    {
        var locator = ResourceLocator.QueryTyped("type", "id");
        foreach (var type in new[] { "platform", "customer", "commerce/domain", "commerce/wallet", "commerce/subscription" })
            Assert.Equal(new ResourceRef(type, "x1"), locator.Resolve(Request(query: $"?type={Uri.EscapeDataString(type)}&id=x1"), Model));

        // The unprefixed names are no longer types, and a prefix alone is not one either.
        foreach (var type in new[] { "group", "user", "nonsense", "", "Customer", "customer ", "customer:acme", "wallet", "commerce", "commerce/" })
            Assert.Null(locator.Resolve(Request(query: $"?type={Uri.EscapeDataString(type)}&id=x1"), Model));
    }

    [Fact]
    public void Before_the_model_is_loaded_no_named_type_resolves()
    {
        var locator = ResourceLocator.QueryTyped("type", "id");
        Assert.Null(locator.Resolve(Request(query: "?type=customer&id=acme"), AccessModel.Empty));
    }

    [Fact]
    public void A_slash_in_the_type_resolves_whether_or_not_the_client_encodes_it()
    {
        var locator = ResourceLocator.QueryTyped("type", "id");
        var expected = new ResourceRef("commerce/wallet", "uk-ops");
        Assert.Equal(expected, locator.Resolve(Request(query: "?type=commerce/wallet&id=uk-ops"), Model));
        Assert.Equal(expected, locator.Resolve(Request(query: "?type=commerce%2Fwallet&id=uk-ops"), Model));
    }

    [Theory]
    [InlineData("a#member")]
    [InlineData("a:b")]
    [InlineData("a@b")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("a b")]
    public void An_id_the_engine_refuses_cannot_exist_so_it_never_resolves(string id)
    {
        Assert.Null(ResourceLocator.Route("customer").Resolve(Request(new() { ["id"] = id }), Model));
        Assert.Null(ResourceLocator.QueryTyped("type", "id").Resolve(Request(query: $"?type=customer&id={Uri.EscapeDataString(id)}"), Model));
    }

    [Theory]
    [InlineData("Bad_Upper")]
    [InlineData("-leading")]
    [InlineData("PCL|2026/0001+x=")]
    public void A_type_named_in_the_request_takes_any_id_the_engine_accepts_but_this_apps_routes_do_not(string id)
    {
        // The query-string routes serve every team's types, so they follow the engine's id rule; a fixed-type route is
        // this application's own type, whose ids follow EntityId.
        Assert.Equal(new ResourceRef("customer", id),
            ResourceLocator.QueryTyped("type", "id").Resolve(Request(query: $"?type=customer&id={Uri.EscapeDataString(id)}"), Model));
        Assert.Null(ResourceLocator.Route("customer").Resolve(Request(new() { ["id"] = id }), Model));
    }

    [Fact]
    public void A_missing_route_value_or_query_parameter_does_not_resolve()
    {
        Assert.Null(ResourceLocator.Route("customer").Resolve(Request(), Model));
        Assert.Null(ResourceLocator.QueryTyped("type", "id").Resolve(Request(query: "?type=customer"), Model));
        Assert.Null(ResourceLocator.QueryTyped("type", "id").Resolve(Request(query: "?id=acme"), Model));
    }

    [Fact]
    public void A_query_locator_reads_the_query_string_and_ignores_route_values()
    {
        var locator = ResourceLocator.QueryTyped("type", "id");
        Assert.Equal(new ResourceRef("commerce/domain", "acme-uk"), locator.Resolve(Request(new() { ["id"] = "ignored" }, "?type=commerce/domain&id=acme-uk"), Model));
    }

    [Fact]
    public void Repeated_query_values_do_not_smuggle_a_second_object()
    {
        // "?id=acme&id=globex" arrives as one comma-joined value, which is not a valid id, so it resolves to nothing.
        var http = new DefaultHttpContext();
        http.Request.Query = new QueryCollection(new Dictionary<string, StringValues> { ["type"] = "customer", ["id"] = new StringValues(["acme", "globex"]) });
        Assert.Null(ResourceLocator.QueryTyped("type", "id").Resolve(http.Request, Model));
    }
}
