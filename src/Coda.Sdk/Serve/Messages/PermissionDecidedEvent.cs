using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// A <c>PermissionRequest</c> hook decided a pending tool approval without the interactive prompt.
/// </summary>
/// <remarks>
/// Emitted only for <c>allow</c> and <c>deny</c>. A <c>prompt</c> decision means the hook expressed
/// no opinion and the normal approval flow proceeds, so nothing is surfaced.
/// </remarks>
public sealed record PermissionDecidedEvent(
    [property: JsonPropertyName("hookCommand")] string HookCommand,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("decision")] string Decision);
