namespace Coda.Tui.Ui.Rendering;

internal sealed record CodaTheme(
    string Name,
    string DisplayName,
    TuiTheme Tui,
    ConsolePalette Console);
