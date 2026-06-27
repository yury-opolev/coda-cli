using Microsoft.Extensions.Logging;

namespace Coda.Sdk.Telemetry;

/// <summary>
/// The result of building the engine logger: the factory plus the active log file
/// path (null when telemetry is disabled).
/// </summary>
public sealed record CodaLoggerSetup(ILoggerFactory Factory, string? LogFilePath);
