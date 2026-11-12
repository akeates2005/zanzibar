using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Poc.Infrastructure.SpiceDb;
using Poc.IntegrationTests.Api;

namespace Poc.IntegrationTests.Observability;

/// <summary>
/// PRD: Observability. Checks are logged with the request's trace id, grants at information level, every SpiceDB call is a
/// span in the request's trace, and no token or email address is ever logged. Spans are read from what the application's own
/// tracer provider exported.
/// </summary>
public sealed partial class ObservabilityTests : ApiTestBase
{
    /// <summary>
    /// GETs <paramref name="url"/> in a trace of its own. ActivitySources are process-wide, so every test host in this process
    /// also receives the spans of the others running in parallel; a trace id chosen here is the only thing no other test can
    /// share, and the server honours the W3C <c>traceparent</c> header.
    /// </summary>
    private static async Task<(HttpResponseMessage Response, TracedRequest Trace)> GetTracedAsync(HttpClient client, string url)
    {
        var trace = ActivityTraceId.CreateRandom();
        var caller = ActivitySpanId.CreateRandom();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("traceparent", $"00-{trace}-{caller}-01");
        return (await client.SendAsync(request), new TracedRequest(trace, caller));
    }

    /// <summary>The trace a test started and the span id it claimed as the caller, which the server span is a child of.</summary>
    private sealed record TracedRequest(ActivityTraceId Trace, ActivitySpanId Caller);

    /// <summary>
    /// The server span for the traced request: the one whose parent is the caller span the test sent. The trace holds others, too:
    /// on a first token validation the API calls the stub issuer (discovery and keys), and the trace context propagates, so the
    /// issuer's own server spans belong to the same trace.
    /// </summary>
    private async Task<Activity> RequestSpanAsync(TracedRequest traced)
    {
        Activity? found = null;
        await EventuallyAsync(() =>
        {
            found = Host.Spans.Snapshot().FirstOrDefault(a => a.Source.Name == "Microsoft.AspNetCore" && a.TraceId == traced.Trace && a.ParentSpanId == traced.Caller);
            return Task.FromResult(found is not null);
        }, "no server span was exported for the traced request", seconds: 10);
        return found!;
    }

    private IEnumerable<Activity> SpiceDbSpansIn(TracedRequest traced) =>
        Host.Spans.Snapshot().Where(a => a.Source.Name == SpiceDbTelemetry.ActivitySourceName && a.TraceId == traced.Trace);

    // ---- Traces ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_SpiceDB_call_made_for_a_request_is_a_span_in_that_requests_trace()
    {
        var alice = await AsAsync("alice");

        var (response, trace) = await GetTracedAsync(alice, "/api/customers/acme");
        using (response) await response.ExpectAsync(HttpStatusCode.OK);

        var request = await RequestSpanAsync(trace);
        var spans = SpiceDbSpansIn(trace).ToList();
        // The filter's view check, the object's permission map, and the one ancestor's view check: three calls, three spans.
        Assert.Equal(["spicedb.CheckBulkPermissions", "spicedb.CheckPermission", "spicedb.CheckPermission"], spans.Select(s => s.OperationName).Order(StringComparer.Ordinal).ToArray());
        Assert.All(spans, s =>
        {
            Assert.Equal(ActivityKind.Client, s.Kind);
            Assert.Equal("grpc", s.GetTagItem("rpc.system"));
            Assert.NotNull(s.GetTagItem("rpc.method"));
            Assert.NotEqual(ActivityStatusCode.Error, s.Status);
            Assert.Equal(request.SpanId, s.ParentSpanId); // directly under the request that caused it
        });
    }

    [Fact]
    public async Task A_failed_engine_call_is_a_span_with_error_status()
    {
        var alice = await AsAsync("alice");
        Host.Engine.Pause();

        var (response, trace) = await GetTracedAsync(alice, "/api/customers/acme");
        using (response) await response.ExpectProblemAsync(HttpStatusCode.ServiceUnavailable);

        await RequestSpanAsync(trace);
        var failed = Assert.Single(SpiceDbSpansIn(trace));
        Assert.Equal("spicedb.CheckPermission", failed.OperationName);
        Assert.Equal(ActivityStatusCode.Error, failed.Status);
        Assert.Equal("DeadlineExceeded", failed.StatusDescription);
    }

    [Fact]
    public async Task Health_probes_are_not_exported_so_they_do_not_drown_real_requests()
    {
        using var anonymous = Host.ClientWithToken(null);
        var alice = await AsAsync("alice");

        var (health, healthTrace) = await GetTracedAsync(anonymous, "/health");
        health.Dispose();
        var (me, meTrace) = await GetTracedAsync(alice, "/api/me");
        using (me) await me.ExpectAsync(HttpStatusCode.OK);
        await RequestSpanAsync(meTrace); // the real request is exported...

        // ...and the health probe, made first and in a trace of its own, is not.
        Assert.DoesNotContain(Host.Spans.Snapshot(), a => a.TraceId == healthTrace.Trace && a.Source.Name == "Microsoft.AspNetCore" && a.ParentSpanId == healthTrace.Caller);
    }

    // ---- Logs -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_permission_check_is_logged_at_debug_with_subject_permission_resource_result_duration_and_trace_id()
    {
        var alice = await AsAsync("alice");

        var (response, trace) = await GetTracedAsync(alice, "/api/wallets/uk-ops");
        using (response) await response.ExpectAsync(HttpStatusCode.OK);

        var traceId = trace.Trace.ToString();
        var checks = Host.Logs.Entries
            .Where(l => l.Level == LogLevel.Debug && l.Category.EndsWith("SpiceDbAuthorizer", StringComparison.Ordinal) && l.Message.Contains(traceId, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(checks);
        Assert.All(checks, c => Assert.Contains("subject=user:alice", c.Message, StringComparison.Ordinal));

        var view = checks.First(c => c.Message.Contains("permission=view resource=wallet:uk-ops", StringComparison.Ordinal));
        Assert.Contains("allowed=True", view.Message, StringComparison.Ordinal);
        Assert.Matches(@"durationMs=\d+(\.\d+)?", view.Message);
        Assert.Contains($"traceId={traceId}", view.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_denied_check_is_logged_as_denied()
    {
        var dave = await AsAsync("dave");
        await (await dave.GetAsync("/api/wallets/uk-ops")).ExpectProblemAsync(HttpStatusCode.NotFound);

        Assert.Contains(Host.Logs.Entries, l => l.Level == LogLevel.Debug && l.Message.Contains("subject=user:dave permission=view resource=wallet:uk-ops allowed=False", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Grants_and_revokes_are_logged_at_information_with_who_did_what()
    {
        var alice = await AsAsync("alice");
        await (await alice.PutJsonAsync("/api/access/customer/acme/grants", new { relation = "viewer", subject = new { type = "user", id = "frank" } })).ExpectAsync(HttpStatusCode.OK);
        await (await alice.DeleteAsync("/api/access/customer/acme/grants?relation=viewer&subjectType=user&subjectId=frank")).ExpectAsync(HttpStatusCode.OK);

        var info = Host.Logs.Entries.Where(l => l.Level == LogLevel.Information && l.Category.EndsWith("AccessLog", StringComparison.Ordinal)).Select(l => l.Message).ToList();
        Assert.Contains(info, m => m.Contains("Grant applied: customer:acme#viewer@user:frank by alice", StringComparison.Ordinal));
        Assert.Contains(info, m => m.Contains("Revoke applied: customer:acme#viewer@user:frank by alice", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-_\.]{16,}")]
    private static partial Regex BearerToken();

    [Fact]
    public async Task No_access_token_email_address_or_secret_appears_in_any_log_across_a_busy_session_including_failures()
    {
        // A session that exercises success, refusals, validation errors, bad tokens, engine failure and recovery.
        foreach (var user in new[] { "priya", "sam", "alice", "bob", "carol", "frank" })
        {
            var client = await AsAsync(user);
            foreach (var url in new[] { "/api/me", "/api/tree", "/api/customers/acme", "/api/wallets/uk-ops", "/api/users?query=alice@zanzibar.example", "/api/users?query=Hughes", "/api/access/wallet/uk-ops/grants" })
                (await client.GetAsync(url)).Dispose();
        }

        var alice = await AsAsync("alice");
        (await alice.PutJsonAsync("/api/access/customer/acme/grants", new { relation = "viewer", subject = new { type = "user", id = "frank" } })).Dispose();
        (await alice.PostJsonAsync("/api/customers/acme/groups", new { name = "Logs" })).Dispose();
        (await alice.PutJsonAsync("/api/access/customer/acme/grants", new { relation = "bogus", subject = new { type = "user", id = "frank" } })).Dispose(); // 422
        (await (await AsAsync("erin")).PatchJsonAsync("/api/customers/acme", new { name = "x" })).Dispose(); // 403

        foreach (var bad in new[] { "garbage-SECRET-MARKER", "SECRET.MARKER.TOKEN", Host.Tokens.Issue(new Dictionary<string, object> { ["sub"] = "x-SECRET-MARKER" }, TimeSpan.FromMinutes(5)).Token })
        {
            using var client = Host.ClientWithToken(bad);
            (await client.GetAsync("/api/me")).Dispose(); // 401
        }

        Host.Engine.Pause();
        (await alice.GetAsync("/api/customers/acme")).Dispose(); // engine failure: 503
        Host.Engine.Resume();
        await Host.WaitUntilReadyAsync(TimeSpan.FromSeconds(30));

        var everything = string.Join('\n', Host.Logs.Entries.Select(l => $"{l.Category}|{l.Message}|{l.Exception}"));
        Assert.True(Host.Logs.Entries.Count > 200, $"only {Host.Logs.Entries.Count} log lines captured; the test would prove little");

        foreach (var token in Host.IssuedTokens) Assert.DoesNotContain(token, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", everything, StringComparison.Ordinal);               // any JWT, whoever issued it
        Assert.DoesNotContain("SECRET-MARKER", everything, StringComparison.Ordinal);      // what a client put in a bad token
        Assert.DoesNotContain("SECRET.MARKER", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("zanzibar.example", everything, StringComparison.OrdinalIgnoreCase); // no email address
        Assert.DoesNotContain(Host.Engine.PresharedKey, everything, StringComparison.Ordinal);      // not the engine's key either
        Assert.DoesNotMatch(BearerToken(), everything); // the word "Bearer" appears in framework messages; a token after it must not
    }

    [Fact]
    public void Framework_request_logging_is_held_at_warning_so_a_typed_search_term_never_reaches_a_log()
    {
        var factory = Host.Services.GetRequiredService<ILoggerFactory>();
        var hosting = factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");

        // The provider attached here has no rule for this category, so this reflects the real configuration.
        Assert.False(hosting.IsEnabled(LogLevel.Information));
        Assert.False(hosting.IsEnabled(LogLevel.Debug));
    }
}
