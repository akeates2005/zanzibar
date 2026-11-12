using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Poc.Api.Security;

namespace Poc.IntegrationTests.Api;

/// <summary>The enforcement filter on its own: no engine, no host.</summary>
public sealed class AccessFilterTests
{
    private static async Task<(int Status, string Body)> InvokeAsync(Endpoint? endpoint, Func<ValueTask<object?>>? next = null)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        if (endpoint is not null) http.SetEndpoint(endpoint);

        // The filter must refuse before it ever needs the authoriser or a user, so none are supplied.
        var filter = new AccessFilter(null!, null!, NullLogger<AccessFilter>.Instance);
        var result = await filter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http), _ => next?.Invoke() ?? ValueTask.FromResult<object?>("handler ran"));

        if (result is IResult httpResult) await httpResult.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task An_endpoint_with_no_access_declaration_is_refused_and_its_handler_never_runs()
    {
        var handlerRan = false;
        var endpoint = new RouteEndpoint(_ => Task.CompletedTask, Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/api/undeclared"), 0,
            new EndpointMetadataCollection(), "undeclared");

        var (status, body) = await InvokeAsync(endpoint, () =>
        {
            handlerRan = true;
            return ValueTask.FromResult<object?>("handler ran");
        });

        Assert.Equal(500, status);
        Assert.Contains("no access declaration", body, StringComparison.OrdinalIgnoreCase);
        Assert.False(handlerRan, "an undeclared endpoint must fail closed");
    }

    [Fact]
    public async Task A_request_with_no_endpoint_at_all_is_refused_too()
    {
        var (status, _) = await InvokeAsync(null);
        Assert.Equal(500, status);
    }
}
