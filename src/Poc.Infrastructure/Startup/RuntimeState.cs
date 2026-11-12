namespace Poc.Infrastructure.Startup;

/// <summary>Whether the start-up sequence has completed. Background workers and the readiness probe read it.</summary>
public sealed class RuntimeState
{
    private volatile bool _ready;

    public bool IsReady => _ready;

    public int StartupCount { get; private set; }

    public void MarkNotReady() => _ready = false;

    public void MarkReady()
    {
        StartupCount++;
        _ready = true;
    }
}
