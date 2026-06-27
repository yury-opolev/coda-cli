namespace Coda.Tui.Repl;

/// <summary>Outcome of running a slash command: keep looping, or exit the REPL.</summary>
public sealed record CommandResult
{
    public required bool ShouldExit { get; init; }

    /// <summary>
    /// When non-null, the REPL will run this prompt through the agent after the command returns.
    /// Implies <see cref="ShouldExit"/> = false.
    /// </summary>
    public string? PromptToRun { get; init; }

    public static CommandResult Continue { get; } = new() { ShouldExit = false };

    public static CommandResult Exit { get; } = new() { ShouldExit = true };

    /// <summary>Returns a result that keeps the REPL running and schedules <paramref name="prompt"/> as the next agent turn.</summary>
    public static CommandResult RunPrompt(string prompt) => new() { ShouldExit = false, PromptToRun = prompt };
}
