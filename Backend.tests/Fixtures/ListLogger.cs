using Microsoft.Extensions.Logging;

namespace Backend.tests.Fixtures;

/// <summary>
/// logger which keeps the formatted text of every message it receives
/// </summary>
/// <typeparam name="T">category type</typeparam>
public class ListLogger<T> : ILogger<T>
{
    readonly List<string> messages = [];

    /// <summary>
    /// formatted messages logged so far, in order
    /// </summary>
    public string[] Messages
    {
        get {
            lock (messages)
                return messages.ToArray();
        }
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (messages)
            messages.Add(formatter(state, exception));
    }
}
