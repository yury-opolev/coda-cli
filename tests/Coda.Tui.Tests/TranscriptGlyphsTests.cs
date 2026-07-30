using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

/// <summary>
/// Verifies the glyph-width invariants that keep the gutter math correct: every marker and
/// connector glyph must occupy exactly one terminal cell, and the <see cref="TranscriptGlyphs.Prefix"/>
/// method must return strings whose display width matches <see cref="TranscriptGlyphs.MarkerCells"/>
/// or <see cref="TranscriptGlyphs.ChildCells"/> exactly.
/// </summary>
public sealed class TranscriptGlyphsTests
{
    // ---------------------------------------------------------------------------
    // Individual glyph widths — Unicode set
    // ---------------------------------------------------------------------------

    [Fact]
    public void Unicode_UserMarker_is_one_cell() =>
        Assert.Equal(1, TerminalCellText.Width(TranscriptGlyphs.Unicode.UserMarker));

    [Fact]
    public void Unicode_AgentActiveMarker_is_one_cell() =>
        Assert.Equal(1, TerminalCellText.Width(TranscriptGlyphs.Unicode.AgentActiveMarker));

    [Fact]
    public void Unicode_AgentCompleteMarker_is_one_cell() =>
        Assert.Equal(1, TerminalCellText.Width(TranscriptGlyphs.Unicode.AgentCompleteMarker));

    [Fact]
    public void Unicode_ChildConnector_is_one_cell() =>
        Assert.Equal(1, TerminalCellText.Width(TranscriptGlyphs.Unicode.ChildConnector));

    [Fact]
    public void Unicode_LastChildConnector_is_one_cell() =>
        Assert.Equal(1, TerminalCellText.Width(TranscriptGlyphs.Unicode.LastChildConnector));

    // ---------------------------------------------------------------------------
    // Individual glyph widths — ASCII set
    // ---------------------------------------------------------------------------

    [Fact]
    public void Ascii_UserMarker_is_one_cell() =>
        Assert.Equal(1, TerminalCellText.Width(TranscriptGlyphs.Ascii.UserMarker));

    [Fact]
    public void Ascii_AgentActiveMarker_is_one_cell() =>
        Assert.Equal(1, TerminalCellText.Width(TranscriptGlyphs.Ascii.AgentActiveMarker));

    [Fact]
    public void Ascii_AgentCompleteMarker_is_one_cell() =>
        Assert.Equal(1, TerminalCellText.Width(TranscriptGlyphs.Ascii.AgentCompleteMarker));

    [Fact]
    public void Ascii_ChildConnector_is_one_cell() =>
        Assert.Equal(1, TerminalCellText.Width(TranscriptGlyphs.Ascii.ChildConnector));

    [Fact]
    public void Ascii_LastChildConnector_is_one_cell() =>
        Assert.Equal(1, TerminalCellText.Width(TranscriptGlyphs.Ascii.LastChildConnector));

    // ---------------------------------------------------------------------------
    // Prefix widths — None (0 cells)
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(TranscriptGutterKind.None)]
    public void Prefix_None_is_empty(TranscriptGutterKind kind)
    {
        Assert.Equal(string.Empty, TranscriptGlyphs.Unicode.Prefix(kind));
        Assert.Equal(string.Empty, TranscriptGlyphs.Ascii.Prefix(kind));
    }

    // ---------------------------------------------------------------------------
    // Prefix widths — marker/continuation rows (MarkerCells = 3)
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(TranscriptGutterKind.UserMarker)]
    [InlineData(TranscriptGutterKind.AgentActive)]
    [InlineData(TranscriptGutterKind.AgentComplete)]
    [InlineData(TranscriptGutterKind.Continuation)]
    public void Prefix_for_marker_kinds_has_MarkerCells_width(TranscriptGutterKind kind)
    {
        Assert.Equal(TranscriptGlyphs.MarkerCells, TerminalCellText.Width(TranscriptGlyphs.Unicode.Prefix(kind)));
        Assert.Equal(TranscriptGlyphs.MarkerCells, TerminalCellText.Width(TranscriptGlyphs.Ascii.Prefix(kind)));
    }

    // ---------------------------------------------------------------------------
    // Prefix widths — child rows (ChildCells = 5)
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(TranscriptGutterKind.Child)]
    [InlineData(TranscriptGutterKind.LastChild)]
    public void Prefix_for_child_kinds_has_ChildCells_width(TranscriptGutterKind kind)
    {
        Assert.Equal(TranscriptGlyphs.ChildCells, TerminalCellText.Width(TranscriptGlyphs.Unicode.Prefix(kind)));
        Assert.Equal(TranscriptGlyphs.ChildCells, TerminalCellText.Width(TranscriptGlyphs.Ascii.Prefix(kind)));
    }

    // ---------------------------------------------------------------------------
    // For(bool) factory
    // ---------------------------------------------------------------------------

    [Fact]
    public void For_true_returns_Unicode_instance() =>
        Assert.Same(TranscriptGlyphs.Unicode, TranscriptGlyphs.For(unicodeOutput: true));

    [Fact]
    public void For_false_returns_Ascii_instance() =>
        Assert.Same(TranscriptGlyphs.Ascii, TranscriptGlyphs.For(unicodeOutput: false));
}
