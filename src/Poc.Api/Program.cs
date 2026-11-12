using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Poc.Api.Endpoints;
using Poc.Api.Hosting;
using Poc.Api.Security;
using Poc.Application.Common;
using Poc.Domain.Common;
using Poc.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Configuration: every environment-specific value comes from configuration, never from code -------------------
var pocOptions = new PocOptions();
config.GetSection("Poc").Bind(pocOptions);

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
    document.SecurityRequirements.Add(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.Models.OpenApiSecurityScheme { Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "bearer" } }] = [],
    });
    return Task.CompletedTask;
}));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
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
