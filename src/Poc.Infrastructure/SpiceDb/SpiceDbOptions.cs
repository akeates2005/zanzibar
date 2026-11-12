namespace Poc.Infrastructure.SpiceDb;

public sealed class SpiceDbOptions
{
    /// <summary>gRPC endpoint, e.g. <c>http://localhost:50051</c>.</summary>
    public string Endpoint { get; set; } = "http://localhost:50051";

    /// <summary>Preshared key sent as a bearer token. Comes from configuration; never hard-coded.</summary>
    public string PresharedKey { get; set; } = "";

    /// <summary>Deadline applied to every gRPC call (every page of a paged lookup gets its own).</summary>
    public TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Page size for LookupResources (LookupSubjects is not paged; see DECISIONS.md D4). SpiceDB caps a resource lookup at 1000 results by default, so this must stay below that
    /// and the adapter pages through the cursor until the stream is exhausted.
    /// </summary>
    public int LookupPageSize { get; set; } = 500;

    /// <summary>
    /// Permit sending the preshared key over plain HTTP (local development and the Compose network only).
    /// When false, an <c>http://</c> endpoint is rejected rather than leaking the key.
    /// </summary>
    public bool AllowInsecureTransport { get; set; }
}
