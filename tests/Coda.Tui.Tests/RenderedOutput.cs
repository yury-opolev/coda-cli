using System.Text;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace Coda.Tui.Tests;

/// <summary>
/// Scrapes what the driver actually painted, so tests can assert on what a user SEES rather than on
/// widget state. Widget state has repeatedly shipped green while the screen showed nothing useful
/// (an unfocused <c>TableView</c> tracks a selected cell but draws no highlight, a hidden pane keeps
/// its last pixels), so every visibility regression must be pinned to the cell buffer.
/// </summary>
public static class RenderedOutput
{
    /// <summary>Every painted row, top to bottom, as plain text.</summary>
    public static IReadOnlyList<string> Lines(IApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var driver = application.Driver ?? throw new InvalidOperationException("Driver is not initialised.");
        var lines = new List<string>(driver.Rows);
        for (var row = 0; row < driver.Rows; row++)
        {
            var builder = new StringBuilder(driver.Cols);
            for (var col = 0; col < driver.Cols; col++)
            {
                builder.Append(driver.Contents![row, col].Grapheme);
            }

            lines.Add(builder.ToString());
        }

        return lines;
    }

    /// <summary>The whole screen as one newline-joined string, for <c>Assert.DoesNotContain</c> checks.</summary>
    public static string Text(IApplication application) => string.Join("\n", Lines(application));

    /// <summary>Index of the first painted row containing <paramref name="needle"/>, or -1.</summary>
    public static int RowContaining(IApplication application, string needle)
    {
        var lines = Lines(application);
        for (var row = 0; row < lines.Count; row++)
        {
            if (lines[row].Contains(needle, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return -1;
    }

    /// <summary>
    /// The colour attribute painted on the first cell of <paramref name="needle"/>. Sampling the
    /// text itself (rather than a fixed column) keeps the assertion valid when column widths shift.
    /// </summary>
    public static TgAttribute AttributeOf(IApplication application, string needle)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrEmpty(needle);

        var lines = Lines(application);
        for (var row = 0; row < lines.Count; row++)
        {
            var col = lines[row].IndexOf(needle, StringComparison.Ordinal);
            if (col >= 0)
            {
                return application.Driver!.Contents![row, col].Attribute ?? default;
            }
        }

        throw new InvalidOperationException($"'{needle}' was never painted. Screen was:\n{Text(application)}");
    }

    /// <summary>Attributes of every cell in a painted row, left to right.</summary>
    public static IReadOnlyList<TgAttribute> RowAttributes(IApplication application, int row)
    {
        ArgumentNullException.ThrowIfNull(application);
        var driver = application.Driver ?? throw new InvalidOperationException("Driver is not initialised.");
        var attributes = new List<TgAttribute>(driver.Cols);
        for (var col = 0; col < driver.Cols; col++)
        {
            attributes.Add(driver.Contents![row, col].Attribute ?? default);
        }

        return attributes;
    }

    /// <summary>
    /// Asserts a list browser paints <paramref name="selectedLabel"/>'s row differently from
    /// <paramref name="unselectedLabel"/>'s row — the rendered proof that the selection highlight
    /// exists at all, which is exactly what a widget-state assertion cannot show.
    /// </summary>
    public static void AssertSelectionHighlightVisible(
        IApplication application,
        string selectedLabel,
        string unselectedLabel)
    {
        var selectedRow = RowContaining(application, selectedLabel);
        var unselectedRow = RowContaining(application, unselectedLabel);
        Assert.True(selectedRow >= 0, $"'{selectedLabel}' was never painted. Screen was:\n{Text(application)}");
        Assert.True(unselectedRow >= 0, $"'{unselectedLabel}' was never painted. Screen was:\n{Text(application)}");
        Assert.NotEqual(selectedRow, unselectedRow);

        var selected = AttributeOf(application, selectedLabel);
        var unselected = AttributeOf(application, unselectedLabel);
        Assert.NotEqual(unselected, selected);

        // A highlight that only tints one glyph is still invisible in practice: require the
        // inverted attribute to span the row (FullRowSelect), not just the label cell.
        var rowAttributes = RowAttributes(application, selectedRow);
        Assert.True(
            rowAttributes.Count(attribute => attribute == selected) > selectedLabel.Length,
            $"Selection attribute did not span row {selectedRow}. Screen was:\n{Text(application)}");
    }
}
