namespace Coda.Tui.Ui.Rendering;

/// <summary>Where a rendered transcript row sits in the gutter/tree shape.</summary>
public enum TranscriptGutterKind
{
    None,               // no gutter (system chrome: notices, permissions, context usage, diffs, command output, session boundary)
    UserMarker,         // first row of a user (or pending-user) message
    AgentActive,        // first row of an in-progress agent entry
    AgentComplete,      // first row of a completed agent entry
    Continuation,       // wrapped/continuation row under a marker row
    Child,              // dependent child row, continuing connector
    LastChild,          // final dependent child row, terminating connector
    ChildContinuation,  // wrapped continuation of a child row: child indent, no connector
}

/// <summary>The marker and connector glyphs that shape the transcript, with an ASCII fallback for
/// terminals that cannot render box-drawing characters.</summary>
public sealed record TranscriptGlyphs(
    string UserMarker,
    string AgentActiveMarker,
    string AgentCompleteMarker,
    string ChildConnector,
    string LastChildConnector)
{
    /// <summary>Unicode glyph set: ❯ ○ ● │ └</summary>
    public static TranscriptGlyphs Unicode { get; } = new("\u276f", "\u25cb", "\u25cf", "\u2502", "\u2514"); // ❯ ○ ● │ └

    /// <summary>ASCII glyph set: &gt; o * | `</summary>
    public static TranscriptGlyphs Ascii { get; }   = new(">", "o", "*", "|", "`");

    /// <summary>Returns <see cref="Unicode"/> or <see cref="Ascii"/> depending on whether the terminal
    /// supports Unicode box-drawing characters.</summary>
    public static TranscriptGlyphs For(bool unicodeOutput) => unicodeOutput ? Unicode : Ascii;

    /// <summary>Cells reserved by a marker/continuation row: one space, one marker cell, one space.</summary>
    public const int MarkerCells = 3;

    /// <summary>Cells reserved by a child row: the marker indent, one connector cell, one space.</summary>
    public const int ChildCells = 5;

    /// <summary>The literal prefix for <paramref name="kind"/>, or empty for <see cref="TranscriptGutterKind.None"/>.</summary>
    public string Prefix(TranscriptGutterKind kind) => kind switch
    {
        TranscriptGutterKind.UserMarker => " " + this.UserMarker + " ",
        TranscriptGutterKind.AgentActive => " " + this.AgentActiveMarker + " ",
        TranscriptGutterKind.AgentComplete => " " + this.AgentCompleteMarker + " ",
        TranscriptGutterKind.Continuation => "   ",
        TranscriptGutterKind.Child => "   " + this.ChildConnector + " ",
        TranscriptGutterKind.LastChild => "   " + this.LastChildConnector + " ",
        TranscriptGutterKind.ChildContinuation => "     ",
        _ => string.Empty,
    };
}
