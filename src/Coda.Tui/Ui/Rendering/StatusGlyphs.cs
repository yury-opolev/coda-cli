namespace Coda.Tui.Ui.Rendering;

/// <summary>
/// The state a browser row reports at a glance. One vocabulary shared by every browser so a glyph
/// means the same thing in the MCP list as it does in Skills or Plugins.
/// </summary>
internal enum BrowserItemState
{
    /// <summary>Connected, running, healthy — nothing needs attention.</summary>
    Healthy,

    /// <summary>Enabled but not currently active: configured and idle, or disconnected.</summary>
    Idle,

    /// <summary>Switched off by configuration.</summary>
    Disabled,

    /// <summary>Failed. The only state that reads as an error.</summary>
    Error,

    /// <summary>Needs a decision from the user — untrusted, expiring credentials.</summary>
    Attention,

    /// <summary>Shadowed by a higher-precedence entry, so it is configured but not in effect.</summary>
    Overridden,
}

/// <summary>
/// The status glyphs browsers render, with an ASCII fallback for terminals that cannot draw
/// geometric shapes. Modelled on <see cref="TranscriptGlyphs"/>, which is the only other formal
/// glyph set in the TUI and the only one that already degrades: every other marker in the browsers
/// is a hard-coded literal with no fallback.
/// </summary>
/// <remarks>
/// Every glyph is exactly one cell wide in both sets, so a column of them stays aligned. That is
/// enforced by test rather than by inspection — several plausible candidates (for example ⊘ and ✗)
/// are ambiguous-width in some terminals.
/// </remarks>
internal sealed record StatusGlyphs(
    string Healthy,
    string Idle,
    string Disabled,
    string Error,
    string Attention,
    string Overridden)
{
    /// <summary>Unicode glyph set: ● ○ ⊘ ✗ ! ↑</summary>
    public static StatusGlyphs Unicode { get; } = new(
        "\u25cf", // ●
        "\u25cb", // ○
        "\u2298", // ⊘
        "\u2717", // ✗
        "!",
        "\u2191"); // ↑

    /// <summary>ASCII glyph set: * o x ! ! ^</summary>
    public static StatusGlyphs Ascii { get; } = new("*", "o", "x", "!", "!", "^");

    /// <summary>
    /// Returns <see cref="Unicode"/> or <see cref="Ascii"/> depending on whether the terminal can
    /// render geometric characters. Mirrors <see cref="TranscriptGlyphs.For"/>.
    /// </summary>
    public static StatusGlyphs For(bool unicodeOutput) => unicodeOutput ? Unicode : Ascii;

    /// <summary>The glyph for <paramref name="state"/>.</summary>
    public string this[BrowserItemState state] => state switch
    {
        BrowserItemState.Healthy => this.Healthy,
        BrowserItemState.Idle => this.Idle,
        BrowserItemState.Disabled => this.Disabled,
        BrowserItemState.Error => this.Error,
        BrowserItemState.Attention => this.Attention,
        BrowserItemState.Overridden => this.Overridden,
        _ => this.Idle,
    };
}
