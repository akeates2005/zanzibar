namespace Poc.IntegrationTests.Fixtures;

/// <summary>
/// A running SpiceDB the tests can talk to. Today the only implementation is a native process
/// (<see cref="NativeSpiceDbHost"/>) because Docker is unavailable (DECISIONS.md D2); a Testcontainers
/// implementation can replace it without touching any test.
/// </summary>
public interface ISpiceDbHost : IAsyncDisposable
{
    string Endpoint { get; }

    string PresharedKey { get; }
}
