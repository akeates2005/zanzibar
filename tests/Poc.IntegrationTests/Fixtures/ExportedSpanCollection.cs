using System.Collections;
using System.Diagnostics;

namespace Poc.IntegrationTests.Fixtures;

/// <summary>
/// What the application's own tracer provider exported, collected through OpenTelemetry's in-memory exporter. Asserting on
/// this (rather than listening to the activity sources directly) shows what a real collector would have received, after the
/// provider's sources, filters and sampling have been applied.
/// </summary>
public sealed class ExportedSpanCollection : ICollection<Activity>
{
    private readonly object _gate = new();
    private readonly List<Activity> _items = [];

    public int Count
    {
        get
        {
            lock (_gate) return _items.Count;
        }
    }

    public bool IsReadOnly => false;

    public IReadOnlyList<Activity> Snapshot()
    {
        lock (_gate) return [.. _items];
    }

    public void Add(Activity item)
    {
        lock (_gate) _items.Add(item);
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
    }

    public bool Contains(Activity item)
    {
        lock (_gate) return _items.Contains(item);
    }

    public void CopyTo(Activity[] array, int arrayIndex)
    {
        lock (_gate) _items.CopyTo(array, arrayIndex);
    }

    public bool Remove(Activity item)
    {
        lock (_gate) return _items.Remove(item);
    }

    public IEnumerator<Activity> GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
