using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// A <c>SubagentStart</c> hook blocked a subagent from running.
/// The <c>task</c> tool will surface <see cref="Reason"/> as an error result.
/// </summary>
public sealed record SubagentBlockedEvent(
    [property: JsonPropertyName("hookCommand")] string HookCommand,
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("reason")] string Reason);
