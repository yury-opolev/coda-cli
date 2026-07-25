namespace Coda.Tui.Ui.Rendering;

internal readonly record struct ThemeResolution(
    CodaTheme Theme,
    bool IsValid,
    string? RawValue);

internal static class ThemeResolver
{
    public static CodaTheme Resolve(string? rawValue, out bool wasInvalid)
    {
        var resolution = Resolve(rawValue);
        wasInvalid = !resolution.IsValid;
        return resolution.Theme;
    }

    internal static string InvalidValueWarning(string? rawValue) =>
        $"Invalid theme '{rawValue}'; using default.";

    public static ThemeResolution Resolve(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new(CodaThemes.Default, true, rawValue);
        }

        return CodaThemes.TryGet(rawValue, out var theme)
            ? new(theme, true, rawValue)
            : new(CodaThemes.Default, false, rawValue);
    }
}
