using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Poc.Application.Authorization;
using Poc.DevIssuer;
using Poc.Infrastructure.SpiceDb;
using Poc.IntegrationTests.Fixtures;
using Poc.IntegrationTests.Platform;

namespace Poc.IntegrationTests.Api;

/// <summary>
/// The whole system over HTTP: a real SpiceDB, the stub issuer on a real port (so the API performs genuine OIDC
/// discovery and key fetching), and the real API hosted in-process with its background workers running.
/// </summary>
public sealed class ApiHost : IAsyncDisposable
{
    private static readonly string[] CapturedCategories = ["Poc", "Microsoft.AspNetCore.Authentication", "Microsoft.EntityFrameworkCore", "Grpc"];

    private readonly WebApplicationFactory<Program> _factory;
    private readonly WebApplication _issuer;
    private readonly HttpClient _issuerClient;
    private readonly Dictionary<string, string> _tokens = [];

    private ApiHost(NativeSpiceDbHost engine, WebApplication issuer, TokenFactory tokens, string issuerUrl,
        WebApplicationFactory<Program> factory, FaultInjectingRelationshipStore faults, CapturingLoggerProvider logs, ExportedSpanCollection spans)
    {
        Engine = engine;
        _issuer = issuer;
        Tokens = tokens;
        IssuerUrl = issuerUrl;
        _factory = factory;
        Faults = faults;
        Logs = logs;
        Spans = spans;
        _issuerClient = new HttpClient { BaseAddress = new Uri(issuerUrl) };
    }

    public NativeSpiceDbHost Engine { get; private set; }

    /// <summary>The spans the application's tracer provider exported.</summary>
    public ExportedSpanCollection Spans { get; }

    /// <summary>Every log line the API wrote, at every level.</summary>
    public CapturingLoggerProvider Logs { get; }

    public TokenFactory Tokens { get; }

    public string IssuerUrl { get; }

    public FaultInjectingRelationshipStore Faults { get; }

    public IServiceProvider Services => _factory.Services;

    public static async Task<ApiHost> StartAsync(Action<Dictionary<string, string?>>? configure = null, string? publicIssuer = null, string? webRoot = null)
    {
        var engine = await NativeSpiceDbHost.StartAsync();
        try
        {
            return await StartCoreAsync(engine, configure, publicIssuer, webRoot);
        }
        catch
        {
            // xunit does not call DisposeAsync when InitializeAsync throws, so a half-started host must clean up itself.
            await engine.DisposeAsync();
            throw;
        }
    }

    private static async Task<ApiHost> StartCoreAsync(NativeSpiceDbHost engine, Action<Dictionary<string, string?>>? configure, string? publicIssuer, string? webRoot)
    {

        var issuerPort = NativeSpiceDbHost.FreePort();
        var issuerUrl = $"http://127.0.0.1:{issuerPort}";
        var issuer = DevIssuerApp.Build(
            ["--environment=Development", $"--urls={issuerUrl}", $"--Issuer:Issuer={publicIssuer ?? issuerUrl}", "--Issuer:Audience=poc-api", "--Logging:LogLevel:Default=Warning"], out var tokens);
        await issuer.StartAsync();

        var settings = new Dictionary<string, string?>
        {
            // With a public name the API is told where tokens say they come from, and separately where to actually fetch keys.
            ["Authentication:Authority"] = publicIssuer ?? issuerUrl,
            ["Authentication:MetadataAddress"] = publicIssuer is null ? null : $"{issuerUrl}/.well-known/openid-configuration",
            ["Authentication:Audience"] = "poc-api",
            ["Authentication:RequireHttpsMetadata"] = "false",
            ["Poc:SqliteDatabaseName"] = $"poc-api-{Guid.NewGuid():N}",
            ["Poc:SchemaPath"] = Path.Combine(AppContext.BaseDirectory, "authz", "schema.zed"),
            ["Poc:OutboxFlushInterval"] = "00:00:00.100",
            ["Poc:ReconciliationInterval"] = "00:00:30",
            ["Poc:SchemaCheckInterval"] = "00:00:00.200",
            ["Poc:RecoveryRetryInterval"] = "00:00:00.300",
            ["Poc:EngineStartupTimeout"] = "00:00:20",
            ["Poc:SpiceDb:Endpoint"] = engine.Endpoint,
            ["Poc:SpiceDb:PresharedKey"] = engine.PresharedKey,
            ["Poc:SpiceDb:AllowInsecureTransport"] = "true",
            ["Poc:SpiceDb:Deadline"] = "00:00:02",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        configure?.Invoke(settings);

        FaultInjectingRelationshipStore? faults = null;
        var logs = new CapturingLoggerProvider();
        var spans = new ExportedSpanCollection();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            if (webRoot is not null) builder.UseWebRoot(webRoot);
            builder.ConfigureLogging(logging =>
            {
                logging.AddProvider(logs);
                // Capture our own code at every level, plus the framework categories most likely to carry something sensitive. The
                // provider is deliberately NOT given a rule for the rest, so framework request logging is judged by the real filter.
                foreach (var category in CapturedCategories)
                    logging.AddFilter<CapturingLoggerProvider>(category, LogLevel.Trace);
            });
            // Program reads configuration eagerly, before ConfigureAppConfiguration would apply, so settings go in as host settings.
            foreach (var (key, value) in settings)
                if (value is not null) builder.UseSetting(key, value);
            builder.ConfigureTestServices(services => services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(spans)));
            builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IRelationshipStore>(sp =>
                faults = new FaultInjectingRelationshipStore(new SpiceDbRelationshipStore(sp.GetRequiredService<SpiceDbConnection>())))));
        });

        _ = factory.Server; // start the host (hosted services begin the start-up sequence)
        var host = new ApiHost(engine, issuer, tokens, issuerUrl, factory, faults ?? throw new InvalidOperationException("Fault store was not created."), logs, spans);
        await host.WaitUntilReadyAsync(TimeSpan.FromSeconds(60));
        return host;
    }

    /// <summary>An authenticated client for a seeded user. Tokens come from the stub issuer exactly as a browser's would.</summary>
    public async Task<HttpClient> ClientAsync(string user)
    {
        if (!_tokens.TryGetValue(user, out var token))
        {
            var response = await _issuerClient.PostAsJsonAsync("/dev/token", new { userId = user });
            response.EnsureSuccessStatusCode();
            token = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("accessToken").GetString()!;
            _tokens[user] = token;
        }

        return ClientWithToken(token);
    }

    /// <summary>Every access token this host has handed out, so a test can look for one in the logs.</summary>
    public IReadOnlyCollection<string> IssuedTokens => _tokens.Values;

    public HttpClient ClientWithToken(string? token)
    {
        var client = _factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Restarts SpiceDB on the same port with the same key. Its datastore is in memory, so the new process holds no schema
    /// and no tuples: exactly what happens when its container restarts.
    /// </summary>
    public async Task RestartEngineAsync()
    {
        var port = new Uri(Engine.Endpoint).Port;
        var key = Engine.PresharedKey;
        await Engine.DisposeAsync();
        Engine = await NativeSpiceDbHost.StartAsync(port, key);
    }

    public async Task WaitUntilReadyAsync(TimeSpan timeout)
    {
        using var client = _factory.CreateClient();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/health");
            if (response.IsSuccessStatusCode) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"The API did not become healthy within {timeout}.");
    }

    public async ValueTask DisposeAsync()
    {
        _issuerClient.Dispose();
        await _factory.DisposeAsync();
        await _issuer.DisposeAsync();
        Tokens.Dispose();
        await Engine.DisposeAsync();
    }
}
