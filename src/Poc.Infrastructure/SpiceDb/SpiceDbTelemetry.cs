using System.Diagnostics;

namespace Poc.Infrastructure.SpiceDb;

public static class SpiceDbTelemetry
{
    /// <summary>Register this source with OpenTelemetry to get a span per SpiceDB call.</summary>
    public const string ActivitySourceName = "Poc.SpiceDb";

    internal static readonly ActivitySource Source = new(ActivitySourceName);
}
