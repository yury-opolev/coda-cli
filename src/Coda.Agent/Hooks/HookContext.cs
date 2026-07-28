namespace Coda.Agent.Hooks;

/// <summary>
/// Session-level context threaded into every hook invocation so hooks receive
/// a stable envelope alongside the event-specific payload.
/// </summary>
/// <param name="SessionId">Stable identifier for the current session.</param>
/// <param name="Cwd">The agent's working directory.</param>
public sealed record HookContext(string SessionId, string Cwd);
