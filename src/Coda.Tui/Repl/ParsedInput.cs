namespace Coda.Tui.Repl;

public enum ParsedInputKind
{
    /// <summary>Whitespace only.</summary>
    Empty,

    /// <summary>A slash command (<c>Name</c> + <c>Args</c>); bare "/" has an empty name (menu trigger).</summary>
    Slash,

    /// <summary>Free-text the user wants to send to the agent.</summary>
    Prompt,

    /// <summary>A <c>!</c>-prefixed shell command to run directly.</summary>
    Bash,
}

/// <summary>The parsed result of one line of REPL input.</summary>
public sealed record ParsedInput
{
    public required ParsedInputKind Kind { get; init; }

    /// <summary>Slash command name (lowercased, no leading slash). Empty string = the menu trigger.</summary>
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>The raw prompt text (for <see cref="ParsedInputKind.Prompt"/>).</summary>
    public string Text { get; init; } = string.Empty;

    public static ParsedInput Empty { get; } = new() { Kind = ParsedInputKind.Empty };

    public static ParsedInput Slash(string name, IReadOnlyList<string> args) =>
        new() { Kind = ParsedInputKind.Slash, Name = name, Args = args };

    public static ParsedInput Prompt(string text) =>
        new() { Kind = ParsedInputKind.Prompt, Text = text };

    public static ParsedInput Bash(string command) =>
        new() { Kind = ParsedInputKind.Bash, Text = command };
}
