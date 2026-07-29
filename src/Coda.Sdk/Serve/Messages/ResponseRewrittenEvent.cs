using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// An <c>AgentResponse</c> hook rewrote the assistant's response. The orchestrator should
/// surface this to the user so they can see both the original and the modified text.
/// </summary>
/// <remarks>
/// Display and history may differ: <see cref="DisplayContent"/> is always what the user sees,
/// while <see cref="ModifiedResponse"/> (when non-null) is what goes into history and what the
/// model believes it said on its next turn.
/// </remarks>
public sealed record ResponseRewrittenEvent(
    [property: JsonPropertyName("hookCommand")] string HookCommand,
    [property: JsonPropertyName("originalResponse")] string OriginalResponse,
    [property: JsonPropertyName("displayContent")] string DisplayContent,
    [property: JsonPropertyName("modifiedResponse")] string? ModifiedResponse);
