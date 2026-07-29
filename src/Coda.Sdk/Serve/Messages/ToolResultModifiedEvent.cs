using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// A <c>PostToolUse</c> hook replaced the tool result text reported back to the model.
/// </summary>
/// <remarks>
/// The tool has already run and its side effects stand; only what the model sees changed.
/// <see cref="OriginalResult"/> is what the tool actually produced.
/// </remarks>
public sealed record ToolResultModifiedEvent(
    [property: JsonPropertyName("hookCommand")] string HookCommand,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("originalResult")] string OriginalResult,
    [property: JsonPropertyName("modifiedResult")] string ModifiedResult);
