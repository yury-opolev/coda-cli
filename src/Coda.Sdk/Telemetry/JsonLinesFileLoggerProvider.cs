using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk.Telemetry;

/// <summary>
/// An <see cref="ILoggerProvider"/> that hands out <see cref="JsonLinesFileLogger"/>
/// instances sharing one <see cref="JsonLinesFileWriter"/> for the session.
/// </summary>
public sealed class JsonLinesFileLoggerProvider : ILoggerProvider
{
    private readonly LogLevel minLevel;
    private readonly JsonLinesFileWriter writer;
    private readonly ConcurrentDictionary<string, JsonLinesFileLogger> loggers = new();

    public JsonLinesFileLoggerProvider(
        string directory,
        LogLevel minLevel,
        long maxFileSizeBytes,
        int maxRunParts,
        string? sessionStem = null)
    {
        this.minLevel = minLevel;
        this.writer = new JsonLinesFileWriter(directory, maxFileSizeBytes, maxRunParts, sessionStem);
    }

    /// <summary>The file currently being written (for surfacing in /log).</summary>
    public string CurrentFilePath => this.writer.CurrentFilePath;

    public ILogger CreateLogger(string categoryName) =>
        this.loggers.GetOrAdd(categoryName, name => new JsonLinesFileLogger(name, this.minLevel, this.writer));

    public void Dispose() => this.writer.Dispose();
}
