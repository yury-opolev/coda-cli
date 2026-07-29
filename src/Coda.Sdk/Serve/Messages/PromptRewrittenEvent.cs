using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// A <c>UserPromptSubmit</c> hook rewrote the user's prompt before the model saw it. The
/// orchestrator should surface this to the user so they can see both the original and the
/// modified text. The modified text is what the model actually received; the original is
/// provided here for auditability.
/// </summary>
public sealed record PromptRewrittenEvent(
    [property: JsonPropertyName("hookCommand")] string HookCommand,
    [property: JsonPropertyName("originalPrompt")] string OriginalPrompt,
    [property: JsonPropertyName("modifiedPrompt")] string ModifiedPrompt);
