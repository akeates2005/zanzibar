using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// The client's TypeScript types are generated from <c>web/openapi.json</c>. This test keeps that committed file equal to
/// what the running API serves, so the types cannot drift from the server. To refresh the file after an intentional API
/// change, run the test with <c>POC_UPDATE_OPENAPI=1</c> and commit the result together with the regenerated types.
/// </summary>
public sealed class OpenApiContractTests : ApiTestBase
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Poc.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Poc.sln not found above the test output directory.");
    }

    /// <summary>The served document without the host-specific <c>servers</c> list, so it is the same wherever it is generated.</summary>
    private async Task<string> LiveDocumentAsync()
    {
        using var anonymous = Host.ClientWithToken(null);
        var json = await (await anonymous.GetAsync("/openapi/v1.json")).ExpectAsync(HttpStatusCode.OK);
        var node = JsonNode.Parse(json.GetRawText())!.AsObject();
        node.Remove("servers");
        return node.ToJsonString(Pretty) + "\n";
    }

    [Fact]
    public async Task The_committed_openapi_document_equals_what_the_api_serves()
    {
        var path = Path.Combine(RepoRoot(), "web", "openapi.json");
        var live = await LiveDocumentAsync();

        if (Environment.GetEnvironmentVariable("POC_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, live);
            return;
        }

        Assert.True(File.Exists(path), "web/openapi.json is missing; run this test with POC_UPDATE_OPENAPI=1 to create it.");
        Assert.True(await File.ReadAllTextAsync(path) == live,
            "web/openapi.json is out of date with the API. Run this test with POC_UPDATE_OPENAPI=1, then regenerate the client types.");
    }

    [Fact]
    public async Task The_document_is_deterministic_across_two_reads()
    {
        Assert.Equal(await LiveDocumentAsync(), await LiveDocumentAsync());
    }
}
