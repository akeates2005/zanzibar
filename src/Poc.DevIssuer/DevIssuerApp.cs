using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Poc.Domain.Directory;

namespace Poc.DevIssuer;

public sealed record TokenRequest(string? UserId);

public static class DevIssuerApp
{
    private static readonly string[] SigningAlgorithms = ["RS256"];
    private static readonly string[] SubjectTypes = ["public"];
    private static readonly string[] ResponseTypes = ["id_token"];

    /// <summary>Loopback only by default (PRD: the Compose file also binds the port to localhost only).</summary>
    private const string DefaultUrl = "http://127.0.0.1:5090";

    /// <summary>
    /// Builds the stub issuer. It refuses to run outside the Development environment (PRD) so it can never stand in for
    /// a real authority in a deployed system. <paramref name="tokens"/> lets tests mint tokens a real user could not get.
    /// </summary>
    public static WebApplication Build(string[] args, out TokenFactory tokens)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (!builder.Environment.IsDevelopment())
            throw new InvalidOperationException(
                $"The stub token issuer runs only in the Development environment (current: {builder.Environment.EnvironmentName}).");

        var issuer = builder.Configuration["Issuer:Issuer"] ?? throw new InvalidOperationException("Issuer:Issuer is not configured.");
        var audience = builder.Configuration["Issuer:Audience"] ?? throw new InvalidOperationException("Issuer:Audience is not configured.");
        var lifetime = TimeSpan.FromMinutes(builder.Configuration.GetValue("Issuer:TokenLifetimeMinutes", 60));
        var webOrigin = builder.Configuration["Issuer:WebOrigin"] ?? "http://localhost:5173";

        if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls(DefaultUrl);

        var factory = new TokenFactory(issuer.TrimEnd('/'), audience, lifetime);
        tokens = factory;
        builder.Services.AddSingleton(factory);
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(webOrigin).WithHeaders("Content-Type").WithMethods("GET", "POST")));

        var app = builder.Build();
        app.UseCors();

        app.MapGet("/.well-known/openid-configuration", (HttpRequest request) => Results.Json(new
        {
            issuer = factory.Issuer,
            // The keys are fetched from wherever this document was fetched: a caller reaches the issuer by its container or host
            // name, which need not be the public name that tokens carry as their issuer.
            jwks_uri = $"{request.Scheme}://{request.Host}/.well-known/jwks.json",
            id_token_signing_alg_values_supported = SigningAlgorithms,
            subject_types_supported = SubjectTypes,
            response_types_supported = ResponseTypes,
        }));

        app.MapGet("/.well-known/jwks.json", () => Results.Json(factory.Jwks()));

        app.MapGet("/dev/users", () => Results.Json(SeedUsers.All.Select(u => new
        {
            id = u.Id,
            displayName = u.DisplayName,
            email = SeedUsers.EmailFor(u.Id),
        })));

        app.MapPost("/dev/token", (TokenRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserId))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "userId is required.");
            return factory.IssueForUser(request.UserId) is { } issued
                ? Results.Json(new { accessToken = issued.Token, expiresAt = issued.ExpiresAt, userId = request.UserId })
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"No seeded user '{request.UserId}'.");
        });

        return app;
    }
}
