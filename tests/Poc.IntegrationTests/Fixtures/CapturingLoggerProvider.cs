using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Poc.IntegrationTests.Fixtures;

public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>Keeps every log line in memory, at every level, so a test can assert on what was (and was not) logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>A snapshot. `ConcurrentQueue.ToArray` is safe while other threads log; a spread over the queue is not.</summary>
    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class Capture(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
    }
}
