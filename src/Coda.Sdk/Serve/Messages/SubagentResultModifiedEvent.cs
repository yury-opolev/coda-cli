using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// A <c>SubagentStop</c> hook replaced the result a subagent returned to its parent.
/// The parent agent cannot distinguish a modified result from the original.
/// </summary>
public sealed record SubagentResultModifiedEvent(
    [property: JsonPropertyName("hookCommand")] string HookCommand,
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("originalResult")] string OriginalResult,
    [property: JsonPropertyName("modifiedResult")] string ModifiedResult);
