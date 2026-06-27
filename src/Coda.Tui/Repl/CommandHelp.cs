namespace Coda.Tui.Repl;

/// <summary>
/// Structured help for one slash command: a usage line, optional prose description,
/// an optional argument list, and optional example invocations. The single source of
/// truth rendered by both the TUI (<c>CommandHelpRenderer</c>) and headless
/// (<c>HelpRunner</c>).
/// </summary>
public sealed record CommandHelp(
    string Usage,
    string? Description = null,
    IReadOnlyList<(string Arg, string Meaning)>? Options = null,
    IReadOnlyList<string>? Examples = null);
