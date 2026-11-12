using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Poc.Api.Security;
using Poc.Application.Authorization;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// Phase 4 exit criterion, part two, and one of the PRD's three permanent fitness functions: every endpoint declares the
/// permission it requires, so an unprotected route cannot ship unnoticed. The routes are enumerated from the running
/// application, then exercised over HTTP to prove the declaration is what is enforced.
/// </summary>
public sealed class EndpointDeclarationTests : ApiTestBase
{
    private static readonly Dictionary<string, string> SampleIds = new()
    {
        ["platform"] = "main",
        ["customer"] = "acme",
        ["domain"] = "acme-uk",
        ["wallet"] = "uk-ops",
        ["subscription"] = "m365-e3",
        ["group"] = "uk-it",
    };

    private sealed record Route(RouteEndpoint Endpoint, string Pattern, string Method, AccessRequirement? Requirement);

    private List<Route> Routes() => Host.Services.GetServices<EndpointDataSource>()
        .SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
        .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            .Select(m => new Route(e, e.RoutePattern.RawText!, m, e.Metadata.GetMetadata<AccessRequirement>())))
        .OrderBy(r => r.Pattern, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal).ToList();

    private static IEnumerable<Route> ApiRoutes(IEnumerable<Route> routes) => routes.Where(r => r.Pattern.StartsWith("/api", StringComparison.Ordinal));

    [Fact]
    public void Every_endpoint_in_the_application_declares_its_access()
    {
        var routes = Routes();
        Assert.True(routes.Count > 40, $"expected to find the whole API, found only {routes.Count} routes");

        var undeclared = routes.Where(r => r.Requirement is null).Select(r => $"{r.Method} {r.Pattern}").ToList();
        Assert.Empty(undeclared);
    }

    [Fact]
    public void Only_health_and_the_api_description_are_anonymous()
    {
        var anonymous = Routes().Where(r => r.Requirement is AccessRequirement.Anonymous).Select(r => $"{r.Method} {r.Pattern}").ToList();
        Assert.Equal(["GET /health", "GET /openapi/{documentName}.json"], anonymous);
    }

    [Fact]
    public void Every_api_route_sits_behind_the_enforcement_filter_and_demands_authentication()
    {
        foreach (var route in ApiRoutes(Routes()))
        {
            // Filters are not endpoint metadata, so the filter itself is proven by behaviour: see the 401 and 404 sweeps below.
            Assert.NotNull(route.Endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>());
            Assert.Null(route.Endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>());
        }
    }

    [Fact]
    public void Every_declared_permission_exists_in_the_schema_for_every_type_it_can_address()
    {
        foreach (var route in Routes().Where(r => r.Requirement is AccessRequirement.OnResource))
        {
            var requirement = (AccessRequirement.OnResource)route.Requirement!;
            var types = requirement.Locator.FixedType is { } fixedType ? [fixedType] : ResourceLocator.NamedTypes;
            foreach (var type in types)
                Assert.True(SchemaCatalog.HasPermission(type, requirement.Permission), $"{route.Method} {route.Pattern} requires '{requirement.Permission}', which {type} does not define");
        }
    }

    [Fact]
    public void Handler_checked_endpoints_are_a_short_known_list()
    {
        var handlerChecked = Routes().Where(r => r.Requirement is AccessRequirement.HandlerChecked).Select(r => $"{r.Method} {r.Pattern}").ToList();
        Assert.Equal(["POST /api/access/explain"], handlerChecked);
    }

    [Fact]
    public async Task No_api_route_answers_an_anonymous_caller_with_anything_but_401()
    {
        using var anonymous = Host.ClientWithToken(null);
        var failures = new List<string>();

        foreach (var route in ApiRoutes(Routes()))
        {
            var url = System.Text.RegularExpressions.Regex.Replace(route.Pattern, @"\{[^}]+\}", "x");
            using var response = await Send(anonymous, route.Method, url);
            if (response.StatusCode != HttpStatusCode.Unauthorized) failures.Add($"{route.Method} {route.Pattern} -> {(int)response.StatusCode}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task A_user_with_no_access_gets_404_from_every_resource_route_on_real_objects()
    {
        // The strongest generic check: for each declared route, aim it at a real seeded object of the right type as a
        // user who can see nothing. Existence must never leak (404), and nothing may be created, changed or deleted.
        var frank = await AsAsync("frank");
        var failures = new List<string>();
        var attempted = 0;

        foreach (var route in ApiRoutes(Routes()).Where(r => r.Requirement is AccessRequirement.OnResource))
        {
            var requirement = (AccessRequirement.OnResource)route.Requirement!;
            foreach (var url in Urls(route, requirement.Locator))
            {
                attempted++;
                using var response = await Send(frank, route.Method, url);
                if (response.StatusCode != HttpStatusCode.NotFound) failures.Add($"{route.Method} {url} -> {(int)response.StatusCode}");
            }
        }

        Assert.True(attempted > 40, $"only {attempted} requests were attempted");
        Assert.Empty(failures);
        Assert.Equal(0, (await (await frank.GetAsync("/api/tree")).JsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task A_support_user_who_can_view_everything_is_refused_403_naming_the_permission_on_every_mutating_route()
    {
        // Sam can view the whole business tree through the platform's support role, but holds no other permission.
        var sam = await AsAsync("sam");
        var failures = new List<string>();
        var attempted = 0;
        var viewable = new[] { "platform", "customer", "domain", "wallet", "subscription" };

        foreach (var route in ApiRoutes(Routes()).Where(r => r.Requirement is AccessRequirement.OnResource { Permission: not "view" }))
        {
            var requirement = (AccessRequirement.OnResource)route.Requirement!;
            if (requirement.Locator.FixedType is { } fixedType && !viewable.Contains(fixedType)) continue; // groups: Sam is not a viewer
            foreach (var url in Urls(route, requirement.Locator))
            {
                attempted++;
                using var response = await Send(sam, route.Method, url);
                if (response.StatusCode != HttpStatusCode.Forbidden) { failures.Add($"{route.Method} {url} -> {(int)response.StatusCode}"); continue; }
                var problem = await response.JsonAsync();
                if (problem.GetProperty("permission").GetString() != requirement.Permission)
                    failures.Add($"{route.Method} {url} named '{problem.GetProperty("permission")}' not '{requirement.Permission}'");
            }
        }

        Assert.True(attempted > 25, $"only {attempted} requests were attempted");
        Assert.Empty(failures);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT" or "PATCH") request.Content = JsonContent.Create(new { }, options: System.Text.Json.JsonSerializerOptions.Web);
        return client.SendAsync(request);
    }

    /// <summary>Concrete URLs for a route: its id parameter set to a real object, once per type for type-in-the-route endpoints.</summary>
    private static IEnumerable<string> Urls(Route route, ResourceLocator locator)
    {
        IEnumerable<string> types = locator.FixedType is { } t ? [t] : ResourceLocator.NamedTypes;
        foreach (var type in types)
        {
            var url = route.Pattern;
            if (locator.TypeKey is { } typeKey && locator.Source == LocatorSource.Route) url = url.Replace("{" + typeKey + "}", type, StringComparison.Ordinal);
            if (locator.IdKey is { } idKey && locator.Source == LocatorSource.Route) url = url.Replace("{" + idKey + "}", SampleIds[type], StringComparison.Ordinal);
            if (locator.Source == LocatorSource.Query) url += $"?{locator.TypeKey}={type}&{locator.IdKey}={SampleIds[type]}";
            yield return url;
        }
    }
}
