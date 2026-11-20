using Poc.DevIssuer;

var app = DevIssuerApp.Build(args, out _);
app.Run();

/// <summary>
/// Kept internal on purpose. .NET 10 generates a public <c>Program</c> for every web project unless one is declared, and
/// the integration tests reference both this project and Poc.Api, so two public <c>Program</c> types would be
/// ambiguous there. Tests host the issuer through <see cref="DevIssuerApp"/>, never through this type.
/// </summary>
internal partial class Program;
