using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// A <c>PreCompact</c> hook cancelled a compaction attempt. The next trigger
/// (auto threshold or <c>/compact</c>) will offer a fresh chance.
/// </summary>
public sealed record CompactionCancelledEvent(
    [property: JsonPropertyName("hookCommand")] string HookCommand,
    [property: JsonPropertyName("trigger")] string Trigger);
