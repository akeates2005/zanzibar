using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Poc.Api.Endpoints;
using Poc.Api.Hosting;
using Poc.Api.Security;
using Poc.Application.Common;
using Poc.Domain.Common;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Poc.Infrastructure;
using Poc.Infrastructure.SpiceDb;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Configuration: every environment-specific value comes from configuration, never from code -------------------
var pocOptions = new PocOptions();
config.GetSection("Poc").Bind(pocOptions);
// A relative schema path means "next to the application", not "relative to wherever the process was started": the working
// directory differs under `dotnet run`, a service manager or a container, and the build copies the schema beside the binaries.
if (!Path.IsPathRooted(pocOptions.SchemaPath)) pocOptions.SchemaPath = Path.Combine(AppContext.BaseDirectory, pocOptions.SchemaPath);

var authority = config["Authentication:Authority"];
if (string.IsNullOrWhiteSpace(authority))
    throw new InvalidOperationException("Authentication:Authority is not configured. Point it at the token issuer (the stub in development, B2C later).");
var audience = config["Authentication:Audience"] ?? throw new InvalidOperationException("Authentication:Audience is not configured.");

// ---- Authentication: the standard JWT bearer handler, so moving to B2C is a configuration change -----------------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.Audience = audience;
        // Where to fetch discovery and keys from, when that differs from the public authority that tokens name as their issuer
        // (for example inside a container network, where the issuer is reached as http://issuer:5090 but browsers see localhost).
        if (config["Authentication:MetadataAddress"] is { Length: > 0 } metadataAddress) options.MetadataAddress = metadataAddress;
        options.RequireHttpsMetadata = config.GetValue("Authentication:RequireHttpsMetadata", true);
        options.MapInboundClaims = false; // keep claim names exactly as issued
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                // The internal user id is the only identity the engine sees. A token without it is rejected, and `sub`
                // is never a fallback (PRD: Token contents).
                var userId = context.Principal?.FindFirst(CurrentUser.UserIdClaim)?.Value;
                if (string.IsNullOrWhiteSpace(userId) || userId == Actor.SystemUserId || !IsWellFormedId(userId))
                    context.Fail($"The token has no usable {CurrentUser.UserIdClaim} claim.");
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(config["Cors:WebOrigin"] ?? "http://localhost:5173")
    .WithHeaders("Authorization", "Content-Type")
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")));

// ---- Application ------------------------------------------------------------------------------------------------
builder.Services.AddPocInfrastructure(pocOptions);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ResourceAccess>();
builder.Services.AddScoped<AccessFilter>();
builder.Services.AddScoped<ObjectViews>();
builder.Services.AddScoped<AuthContext>();
builder.Services.AddHostedService<StartupHostedService>();

// ---- Observability: a span per SpiceDB call, joined to the request that caused it (PRD: Observability) -------------
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("zanzibar-poc-api"))
    .WithTracing(tracing =>
    {
        tracing.AddSource(SpiceDbTelemetry.ActivitySourceName)
            .AddAspNetCoreInstrumentation(o => o.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal));
        // Export only where asked: OTLP when an endpoint is configured (the standard OTEL_EXPORTER_OTLP_ENDPOINT), console on request.
        if (!string.IsNullOrWhiteSpace(config["OTEL_EXPORTER_OTLP_ENDPOINT"])) tracing.AddOtlpExporter();
        if (config.GetValue("Telemetry:Console", false)) tracing.AddConsoleExporter();
    });

// Framework request logs print the full URL, and the user search puts what the person typed (possibly an email address) in
// the query string. Keep them off whatever the configuration says: no email address is ever logged (PRD).
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = c =>
{
    c.ProblemDetails.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? c.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Zanzibar proof of concept API";
    document.Info.Version = "v1";
    document.Components ??= new();
    document.Components.SecuritySchemes["bearer"] = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
    };
    document.Components.Schemas["Problem"] = new Microsoft.OpenApi.Models.OpenApiSchema
    {
        Type = "object",
        Description = "RFC 9457 problem details.",
        Properties = new Dictionary<string, Microsoft.OpenApi.Models.OpenApiSchema>
        {
            ["type"] = new() { Type = "string", Nullable = true },
            ["title"] = new() { Type = "string" },
            ["status"] = new() { Type = "integer", Format = "int32" },
            ["detail"] = new() { Type = "string", Nullable = true },
            ["traceId"] = new() { Type = "string", Nullable = true },
            ["permission"] = new() { Type = "string", Nullable = true, Description = "Present on 403: the permission the caller lacks." },
            ["code"] = new() { Type = "string", Nullable = true, Description = "Present on 422: a stable rule code." },
        },
    };
    document.SecurityRequirements.Add(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.Models.OpenApiSecurityScheme { Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "bearer" } }] = [],
    });
    return Task.CompletedTask;
}).AddOperationTransformer((operation, context, _) =>
{
    // Every /api operation can answer with an RFC 9457 problem; describe it so generated clients can type their errors.
    if (context.Description.RelativePath?.StartsWith("api/", StringComparison.Ordinal) != true) return Task.CompletedTask;
    var problem = new Microsoft.OpenApi.Models.OpenApiMediaType
    {
        Schema = new Microsoft.OpenApi.Models.OpenApiSchema
        {
            Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.Schema, Id = "Problem" },
        },
    };
    foreach (var (status, text) in new[]
             {
                 ("400", "Malformed request"), ("401", "Not signed in"), ("403", "Missing permission (named in the problem)"),
                 ("404", "No such object, or not visible to the caller"), ("409", "Conflicts with current state"),
                 ("422", "Refused by a rule (code in the problem)"), ("503", "Authorisation service unavailable or system starting"),
             })
    {
        operation.Responses.TryAdd(status, new Microsoft.OpenApi.Models.OpenApiResponse
        {
            Description = text,
            Content = { ["application/problem+json"] = problem },
        });
    }

    return Task.CompletedTask;
}));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// The built client, when one is present (the container image puts it in wwwroot): static files plus a fallback to index.html
// so client-side routes survive a reload. The fallback never answers for /api, /health or the API description, which must keep
// returning JSON problems rather than a web page.
var webRoot = app.Environment.WebRootPath;
var serveClient = webRoot is not null && File.Exists(Path.Combine(webRoot, "index.html"));
if (serveClient)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
app.UseCors();
app.UseMiddleware<ReadinessMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

// Everything under /api requires a signed-in user and passes through the one enforcement filter.
var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<AccessFilter>();
BrowseEndpoints.Map(api);
ObjectEndpoints.Map(api);
AccessEndpoints.Map(api);
GroupEndpoints.Map(api);
SystemEndpoints.Map(api);

SystemEndpoints.MapHealth(app);
app.MapOpenApi().AllowAnonymous().WithMetadata(new AccessRequirement.Anonymous("The API description contains no data."));

if (serveClient)
{
    app.MapFallbackToFile("{*path:nonfile:regex(^(?!api/|api$|health|openapi/).*)}", "index.html").AllowAnonymous()
        .WithMetadata(new AccessRequirement.Anonymous("The built client's entry page: static, no data."));
}

app.Run();

static bool IsWellFormedId(string id)
{
    try
    {
        EntityId.Validate(id, "user id");
        return true;
    }
    catch (DomainRuleException)
    {
        return false;
    }
}

/// <summary>Exposed so integration tests can host the real application.</summary>
public partial class Program;
