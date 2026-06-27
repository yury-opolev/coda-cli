using Coda.Agent.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Coda.Sdk.Telemetry;

/// <summary>
/// Builds the engine's <see cref="ILoggerFactory"/> from resolved telemetry
/// settings: a JSON-lines file provider (plus optional stderr echo), or the no-op
/// <see cref="NullLoggerFactory"/> when telemetry is disabled.
/// </summary>
public static class CodaLoggerFactory
{
    /// <summary>The default logs directory: <c>~/.coda/logs</c>.</summary>
    public static string DefaultLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".coda",
        "logs");

    /// <summary>
    /// Builds the engine logger wired to the JSON-lines file sink (and optionally
    /// stderr), returning both the <see cref="ILoggerFactory"/> and the active log
    /// file path. Returns a <see cref="CodaLoggerSetup"/> with
    /// <see cref="NullLoggerFactory.Instance"/> and a null path when
    /// <see cref="TelemetrySettings.Enabled"/> is false.
    /// </summary>
    public static CodaLoggerSetup Create(TelemetrySettings settings)
    {
        if (!settings.Enabled)
        {
            return new CodaLoggerSetup(NullLoggerFactory.Instance, null);
        }

        var directory = settings.DirectoryOverride ?? DefaultLogDirectory;

        // Prune old runs before opening this run's file.
        JsonLinesFileWriter.PruneRuns(directory, settings.RetainedFileCount);

        var fileProvider = new JsonLinesFileLoggerProvider(
            directory,
            settings.MinLevel,
            settings.MaxFileSizeBytes,
            settings.MaxRunParts);

        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(settings.MinLevel);
            builder.AddProvider(fileProvider);
            if (settings.LogToStderr)
            {
                builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            }
        });

        return new CodaLoggerSetup(factory, fileProvider.CurrentFilePath);
    }
}
