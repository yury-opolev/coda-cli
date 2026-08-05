using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// Emitted when a background task reaches a terminal state (completed, failed, or stopped).
/// The <see cref="Report"/> is truncated identically to the TUI injection path so a headless
/// orchestrator receives exactly the same bounded payload.
/// </summary>
public sealed record TaskCompletedEvent(
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("report")] string? Report);
