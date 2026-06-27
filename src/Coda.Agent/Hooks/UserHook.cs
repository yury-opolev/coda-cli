namespace Coda.Agent.Hooks;

/// <summary>
/// A single user-configured shell hook that fires on an agent lifecycle event.
/// </summary>
/// <param name="Event">The lifecycle event: "PreToolUse", "PostToolUse", or "Stop".</param>
/// <param name="Command">The shell command to execute.</param>
/// <param name="Matcher">
/// Optional tool-name filter. When null the hook fires for all tools.
/// For "Stop" hooks this is always ignored.
/// </param>
public sealed record UserHook(string Event, string Command, string? Matcher);
