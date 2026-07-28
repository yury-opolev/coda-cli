using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// A <c>PreToolUse</c> or <c>PermissionRequest</c> hook replaced the arguments a tool call ran with.
/// </summary>
/// <remarks>
/// The replacement is total, not a merge: <see cref="ModifiedInput"/> is exactly the JSON the tool
/// executed with, and is also what the tool-call activity reports.
/// </remarks>
public sealed record ToolInputModifiedEvent(
    [property: JsonPropertyName("hookCommand")] string HookCommand,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("originalInput")] string OriginalInput,
    [property: JsonPropertyName("modifiedInput")] string ModifiedInput);
