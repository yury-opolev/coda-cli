using Microsoft.Extensions.Logging;

namespace Engine.Tests.TestSupport;

/// <summary>
/// Captures every logged entry as a <c>(Level, Message, Exception)</c> 3-tuple so tests can
/// assert on emitted log lines. The carried <see cref="System.Exception"/> lets exception-aware
/// tests inspect it; message-only assertions simply ignore <c>.Exception</c>.
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => this.Entries.Add((logLevel, formatter(state, exception), exception));
}
