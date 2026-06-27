using System.Text.Json;
using Coda.Common;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk.Telemetry;

/// <summary>
/// An <see cref="ILogger"/> that serializes each event to a single redacted JSON
/// line and appends it via a shared <see cref="JsonLinesFileWriter"/>.
/// </summary>
internal sealed class JsonLinesFileLogger : ILogger
{
    private readonly string category;
    private readonly LogLevel minLevel;
    private readonly JsonLinesFileWriter writer;

    public JsonLinesFileLogger(string category, LogLevel minLevel, JsonLinesFileWriter writer)
    {
        this.category = category;
        this.minLevel = minLevel;
        this.writer = writer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= this.minLevel && logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!this.IsEnabled(logLevel))
        {
            return;
        }

        // A logger must never crash its caller: a misbehaving formatter or a
        // serialization hiccup is swallowed rather than propagated.
        try
        {
            var message = SecretRedactor.Redact(formatter(state, exception));

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("ts", DateTimeOffset.UtcNow.ToString("O"));
                json.WriteString("level", logLevel.ToString());
                json.WriteString("category", this.category);
                json.WriteString("message", message);
                if (eventId.Id != 0)
                {
                    json.WriteNumber("eventId", eventId.Id);
                }

                if (exception is not null)
                {
                    json.WriteString("exceptionType", exception.GetType().FullName);
                    json.WriteString("exception", SecretRedactor.Redact(exception.Message));
                    if (this.minLevel <= LogLevel.Debug && exception.StackTrace is not null)
                    {
                        // Stack traces can embed secrets (e.g. an exception message that
                        // includes a token); redact them like every other field.
                        json.WriteString("stack", SecretRedactor.Redact(exception.StackTrace));
                    }
                }

                json.WriteEndObject();
            }

            this.writer.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch
        {
            // Telemetry is best-effort; never let logging break the caller.
        }
    }
}
