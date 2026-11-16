using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Backend.tests.Fixtures;

/// <summary>Captures formatted SDK host log messages for privacy assertions.</summary>
public sealed class TeamsLogCapture : ILoggerProvider, ILogger {
    readonly ConcurrentQueue<string> messages = new();

    /// <summary>The captured log messages.</summary>
    public string[] Messages => messages.ToArray();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => this;

    /// <inheritdoc />
    public void Dispose() { }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception?.ToString());
}
