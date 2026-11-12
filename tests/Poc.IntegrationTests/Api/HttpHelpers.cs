using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Poc.IntegrationTests.Api;

public static class HttpHelpers
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object? body = null) =>
        client.PostAsJsonAsync(url, body ?? new { }, JsonSerializerOptions.Web);

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string url, object? body = null) =>
        client.PutAsJsonAsync(url, body ?? new { }, JsonSerializerOptions.Web);

    public static Task<HttpResponseMessage> PatchJsonAsync(this HttpClient client, string url, object? body = null) =>
        client.PatchAsJsonAsync(url, body ?? new { }, JsonSerializerOptions.Web);

    /// <summary>Asserts the status and returns the body, with the body in the failure message.</summary>
    public static async Task<JsonElement> ExpectAsync(this HttpResponseMessage response, HttpStatusCode status)
    {
        var json = await response.JsonAsync();
        Assert.True(response.StatusCode == status, $"expected {(int)status} {status} but got {(int)response.StatusCode} {response.StatusCode}: {json}");
        return json;
    }

    /// <summary>Asserts an RFC 9457 problem response with the given status, optionally naming a permission or code.</summary>
    public static async Task<JsonElement> ExpectProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string? permission = null, string? code = null)
    {
        var json = await response.ExpectAsync(status);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        if (permission is not null) Assert.Equal(permission, json.GetProperty("permission").GetString());
        if (code is not null) Assert.Equal(code, json.GetProperty("code").GetString());
        return json;
    }

    public static bool Permission(this JsonElement dto, string name) => dto.GetProperty("permissions").GetProperty(name).GetBoolean();
}
