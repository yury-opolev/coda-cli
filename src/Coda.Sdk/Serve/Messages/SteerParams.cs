using System.Text.Json.Serialization;

namespace Coda.Sdk.Serve.Messages;

/// <summary>
/// Parameters for <c>session/steer</c>: a steering comment posted by the orchestrator to redirect a
/// turn that is already running (or the next turn, if posted while idle). Delivered to the engine's
/// steering inbox and injected as a synthetic user message before the loop's next model call.
/// </summary>
public sealed record SteerParams(
    [property: JsonPropertyName("text")] string Text);
