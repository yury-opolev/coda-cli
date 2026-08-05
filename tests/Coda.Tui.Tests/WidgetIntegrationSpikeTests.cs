using System.Data;
using System.Text;
using Terminal.Gui.Drawing;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;
using TgScheme = Terminal.Gui.Drawing.Scheme;

namespace Coda.Tui.Tests;

/// <summary>
/// Derisking spike for the widget-based browser rework
/// (docs/superpowers/plans/2026-08-05-tui-browser-ux.md, Task 1).
///
/// No overlay in this repo has ever hosted a focusable Terminal.Gui child — every surface is a
/// Label plus a custom-drawn text view. Tasks 2-14 of that plan all assume the toolkit behaves in
/// specific ways inside our existing 80x24 ANSI test harness. These tests pin those assumptions so
/// a wrong one fails here, cheaply, rather than halfway through the migration.
///
/// If one of these fails, STOP and report — do not work around it.
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class WidgetIntegrationSpikeTests : IDisposable
{
    private readonly IApplication application = Application.Create();
    private readonly Window root = new();
    private readonly SessionToken? runState;

    public WidgetIntegrationSpikeTests()
    {
        this.application.AppModel = AppModel.FullScreen;
        this.application.Init(DriverRegistry.Names.ANSI);
        this.application.Driver!.SetScreenSize(80, 24);
        this.runState = this.application.Begin(this.root);
    }

    // -------------------------------------------------------------------------
    // TextField: typing, caret, and the editing behaviours the editor form needs
    // -------------------------------------------------------------------------

    [Fact]
    public void TextField_receives_typed_keys_and_advances_the_caret()
    {
        var field = new TextField { Width = 20 };
        this.root.Add(field);
        field.SetFocus();
        this.application.LayoutAndDraw();

        Assert.True(field.HasFocus);

        field.NewKeyDownEvent(new Key('a'));
        field.NewKeyDownEvent(new Key('b'));
        field.NewKeyDownEvent(new Key('c'));

        Assert.Equal("abc", field.Text);
        Assert.Equal(3, field.InsertionPoint);
    }

    [Fact]
    public void TextField_caret_moves_with_arrows_and_home_end()
    {
        var field = new TextField { Width = 20, Text = "hello" };
        this.root.Add(field);
        field.SetFocus();
        field.InsertionPoint = 5;

        field.NewKeyDownEvent(Key.CursorLeft);
        Assert.Equal(4, field.InsertionPoint);

        field.NewKeyDownEvent(Key.Home);
        Assert.Equal(0, field.InsertionPoint);

        field.NewKeyDownEvent(Key.End);
        Assert.Equal(5, field.InsertionPoint);
    }

    /// <summary>
    /// Pins the ordering that actually works, because the obvious one does not.
    ///
    /// Setting <c>Text</c> in the object initialiser (before the field joins the view tree) leaves
    /// the widget with a selection, so the first typed character replaces a character instead of
    /// inserting: "ac" + 'b' at caret 1 yields "bc". Assigning the value *after* <c>Add</c> avoids
    /// it. <c>Used = true</c> selects insert mode; with <c>Used = false</c> the same keystroke
    /// overwrites ("ab").
    ///
    /// The editor form (Task 7) must therefore add each field to the tree first, then assign its
    /// value from the draft.
    /// </summary>
    [Fact]
    public void TextField_inserts_mid_string_at_the_caret()
    {
        var field = new TextField { Width = 20 };
        this.root.Add(field);
        field.Value = "ac";
        field.SetFocus();
        this.application.LayoutAndDraw();

        field.Used = true;
        field.InsertionPoint = 1;
        Assert.Equal(1, field.InsertionPoint);

        field.NewKeyDownEvent(new Key('b'));

        Assert.Equal("abc", field.Text);
        Assert.Equal(2, field.InsertionPoint);
    }

    [Fact]
    public void TextField_overwrites_when_used_is_false()
    {
        var field = new TextField { Width = 20 };
        this.root.Add(field);
        field.Value = "ac";
        field.SetFocus();
        this.application.LayoutAndDraw();

        field.Used = false;
        field.InsertionPoint = 1;

        field.NewKeyDownEvent(new Key('b'));

        Assert.Equal("ab", field.Text);
    }

    /// <summary>
    /// The current hand-rolled editor clears the whole field on Delete
    /// (McpBrowserController EditorDelete). The widget must not: it deletes one character.
    /// </summary>
    [Fact]
    public void TextField_delete_removes_one_character_not_the_whole_field()
    {
        var field = new TextField { Width = 20, Text = "abc" };
        this.root.Add(field);
        field.SetFocus();
        field.InsertionPoint = 0;

        field.NewKeyDownEvent(Key.Delete);

        Assert.Equal("bc", field.Text);
    }

    /// <summary>
    /// Keys that are browser accelerators in list view (q, k, j, r, /) must be plain text while a
    /// field has focus. This is the regression the consistency pass (Task 11) depends on.
    /// </summary>
    [Fact]
    public void TextField_treats_browser_accelerator_keys_as_text()
    {
        var field = new TextField { Width = 40 };
        this.root.Add(field);
        field.SetFocus();

        foreach (var rune in "qkjr/")
        {
            field.NewKeyDownEvent(new Key(rune));
        }

        Assert.Equal("qkjr/", field.Text);
    }

    [Fact]
    public void TextField_exposes_a_secret_mode_and_a_readonly_mode()
    {
        // Secret is display-only masking; the buffer still holds plaintext, which is exactly why the
        // plan keeps secrets on the modal-prompt path instead of binding them to a field.
        var secret = new TextField { Width = 20, Secret = true, Text = "s3cret" };
        Assert.True(secret.Secret);
        Assert.Equal("s3cret", secret.Text);

        var readOnly = new TextField { Width = 20, ReadOnly = true, Text = "fixed" };
        this.root.Add(readOnly);
        readOnly.SetFocus();
        readOnly.NewKeyDownEvent(new Key('x'));

        Assert.Equal("fixed", readOnly.Text);
    }

    // -------------------------------------------------------------------------
    // Focus traversal: Tab / Shift+Tab is the field navigation the spec asks for
    // -------------------------------------------------------------------------

    [Fact]
    public void Tab_and_shift_tab_move_focus_between_fields()
    {
        var first = new TextField { Width = 20, Y = 0, TabStop = TabBehavior.TabStop };
        var second = new TextField { Width = 20, Y = 1, TabStop = TabBehavior.TabStop };
        this.root.Add(first);
        this.root.Add(second);
        this.application.LayoutAndDraw();

        first.SetFocus();
        Assert.True(first.HasFocus);

        this.root.AdvanceFocus(NavigationDirection.Forward, TabBehavior.TabStop);
        Assert.True(second.HasFocus);
        Assert.False(first.HasFocus);

        this.root.AdvanceFocus(NavigationDirection.Backward, TabBehavior.TabStop);
        Assert.True(first.HasFocus);
        Assert.False(second.HasFocus);
    }

    // -------------------------------------------------------------------------
    // OptionSelector: the v2 replacement for RadioGroup (which no longer exists)
    // -------------------------------------------------------------------------

    [Fact]
    public void OptionSelector_reports_and_changes_its_value()
    {
        var selector = new OptionSelector
        {
            Width = 30,
            Labels = ["stdio", "http"],
        };
        this.root.Add(selector);
        selector.SetFocus();
        this.application.LayoutAndDraw();

        selector.Value = 0;
        Assert.Equal(0, selector.Value);

        var changed = 0;
        selector.ValueChanged += (_, _) => changed++;

        selector.Value = 1;

        Assert.Equal(1, selector.Value);
        Assert.True(changed > 0, "ValueChanged should fire when Value is set");
    }

    [Fact]
    public void OptionSelector_supports_horizontal_orientation()
    {
        var selector = new OptionSelector
        {
            Width = 30,
            Labels = ["project", "user"],
            Orientation = Orientation.Horizontal,
        };
        this.root.Add(selector);
        this.application.LayoutAndDraw();

        Assert.Equal(Orientation.Horizontal, selector.Orientation);
    }

    [Fact]
    public void CheckBox_toggles_its_checked_state()
    {
        var box = new CheckBox { Text = "Enabled" };
        this.root.Add(box);
        box.SetFocus();
        this.application.LayoutAndDraw();

        var initial = box.Value;
        box.NewKeyDownEvent(Key.Space);

        Assert.NotEqual(initial, box.Value);
    }

    // -------------------------------------------------------------------------
    // TableView: the coloured, aligned, scrolling list
    // -------------------------------------------------------------------------

    [Fact]
    public void TableView_renders_rows_and_tracks_the_selected_row()
    {
        var table = BuildTable();
        var view = new TableView
        {
            Width = 60,
            Height = 10,
            Table = new DataTableSource(table),
        };
        this.root.Add(view);
        view.SetFocus();
        this.application.LayoutAndDraw();

        Assert.Equal(0, view.Value!.SelectedCell.Y);

        view.NewKeyDownEvent(Key.CursorDown);
        Assert.Equal(1, view.Value!.SelectedCell.Y);

        var rendered = RenderedDriverText(this.application);
        Assert.Contains("alpha", rendered, StringComparison.Ordinal);
        Assert.Contains("beta", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TableView_row_and_cell_colour_getters_are_invoked()
    {
        var table = BuildTable();
        var view = new TableView
        {
            Width = 60,
            Height = 10,
            Table = new DataTableSource(table),
        };

        var rowScheme = SolidScheme(new TgColor(200, 100, 100));
        var cellScheme = SolidScheme(new TgColor(100, 200, 100));

        var rowCalls = 0;
        var cellCalls = 0;

        view.Style.RowColorGetter = _ =>
        {
            rowCalls++;
            return rowScheme;
        };

        view.Style.ColumnStyles[0] = new ColumnStyle
        {
            ColorGetter = _ =>
            {
                cellCalls++;
                return cellScheme;
            },
        };

        this.root.Add(view);
        this.application.LayoutAndDraw();

        Assert.True(rowCalls > 0, "RowColorGetter should be invoked while drawing");
        Assert.True(cellCalls > 0, "ColumnStyle.ColorGetter should be invoked while drawing");
    }

    [Fact]
    public void TableView_scrolls_a_list_longer_than_its_viewport()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        for (var i = 0; i < 50; i++)
        {
            table.Rows.Add($"server-{i:00}");
        }

        var view = new TableView
        {
            Width = 40,
            Height = 6,
            Table = new DataTableSource(table),
        };
        this.root.Add(view);
        view.SetFocus();
        this.application.LayoutAndDraw();

        for (var i = 0; i < 40; i++)
        {
            view.NewKeyDownEvent(Key.CursorDown);
        }

        this.application.LayoutAndDraw();

        Assert.Equal(40, view.Value!.SelectedCell.Y);

        // The point of the widget: the selected row is brought into view without us computing a window.
        var rendered = RenderedDriverText(this.application);
        Assert.Contains("server-40", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnStyle_truncates_rather_than_ragging_at_narrow_widths()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Rows.Add("a-very-long-server-name-that-will-not-fit");

        var view = new TableView
        {
            Width = 20,
            Height = 5,
            Table = new DataTableSource(table),
        };
        view.Style.ColumnStyles[0] = new ColumnStyle { MaxWidth = 10 };

        this.root.Add(view);
        this.application.LayoutAndDraw();

        var rendered = RenderedDriverText(this.application);
        Assert.DoesNotContain("a-very-long-server-name-that-will-not-fit", rendered, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // The harness itself still works with focusable children present
    // -------------------------------------------------------------------------

    [Fact]
    public void Driver_cell_scrape_still_reads_the_screen_with_widgets_hosted()
    {
        var field = new TextField { Width = 30, Text = "visible-text" };
        this.root.Add(field);
        this.application.LayoutAndDraw();

        var rendered = RenderedDriverText(this.application);

        Assert.Contains("visible-text", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", rendered, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static DataTable BuildTable()
    {
        var table = new DataTable();
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Transport", typeof(string));
        table.Rows.Add("alpha", "stdio");
        table.Rows.Add("beta", "http");
        return table;
    }

    private static TgScheme SolidScheme(TgColor foreground)
    {
        var attribute = new TgAttribute(foreground, new TgColor(0, 0, 0));
        return new TgScheme
        {
            Normal = attribute,
            HotNormal = attribute,
            Focus = attribute,
            HotFocus = attribute,
            Active = attribute,
            HotActive = attribute,
            Highlight = attribute,
            Editable = attribute,
            ReadOnly = attribute,
            Disabled = attribute,
        };
    }

    private static string RenderedDriverText(IApplication application)
    {
        var driver = application.Driver!;
        var lines = new List<string>(driver.Rows);
        for (var row = 0; row < driver.Rows; row++)
        {
            var line = new StringBuilder();
            for (var col = 0; col < driver.Cols; col++)
            {
                line.Append(driver.Contents![row, col].Grapheme);
            }

            lines.Add(line.ToString().TrimEnd());
        }

        return string.Join(Environment.NewLine, lines);
    }

    public void Dispose()
    {
        if (this.runState is not null)
        {
            this.application.End(this.runState);
        }

        this.root.Dispose();
        this.application.Dispose();
    }
}
