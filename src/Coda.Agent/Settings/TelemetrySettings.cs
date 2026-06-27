using Microsoft.Extensions.Logging;

namespace Coda.Agent.Settings;

/// <summary>
/// Resolved telemetry/logging configuration: whether logging is enabled, the
/// minimum <see cref="LogLevel"/>, where files go, and how they roll/retain.
/// </summary>
public sealed record TelemetrySettings
{
    /// <summary>Master switch. When false, no files are created and all loggers no-op.</summary>
    public bool Enabled { get; init; }

    /// <summary>Minimum level written. Trace also logs full request/response bodies.</summary>
    public LogLevel MinLevel { get; init; } = LogLevel.Information;

    /// <summary>Echo log lines to stderr in addition to the file.</summary>
    public bool LogToStderr { get; init; }

    /// <summary>How many previous runs to keep on startup (newest-N). 0 keeps all.</summary>
    public int RetainedFileCount { get; init; } = 7;

    /// <summary>Roll to the next part when the current file exceeds this many bytes. 0 = no cap.</summary>
    public long MaxFileSizeBytes { get; init; } = 20L * 1024 * 1024;

    /// <summary>Ring-buffer bound on parts within one run. 0 = unbounded within a run.</summary>
    public int MaxRunParts { get; init; } = 10;

    /// <summary>Override the logs directory (default <c>~/.coda/logs</c>); null = default.</summary>
    public string? DirectoryOverride { get; init; }

    /// <summary>Logging-disabled sentinel; every other property keeps its default, so <c>with { Enabled = true }</c> yields a fully-configured instance.</summary>
    public static TelemetrySettings Disabled { get; } = new() { Enabled = false };
}
