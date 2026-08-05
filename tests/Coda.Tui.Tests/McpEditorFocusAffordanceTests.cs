using System.Collections.Immutable;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Mcp;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

/// <summary>
/// Focus affordances driven through the REAL overlay Show path, not by calling ApplyTheme directly.
/// </summary>
/// <remarks>
/// These exist because every affordance the form draws from the theme — the accent field label, the
/// inverted focused button, the selector cursor — silently rendered nothing in the running
/// application while the unit tests passed. Show() themed the detail body but not the editor form,
/// so the form's theme stayed null and its affordance pass returned early. Tests that call
/// ApplyTheme themselves cannot catch that; these drive the same path the user does.
/// </remarks>
[Collection("TerminalGuiInit")]
public sealed class McpEditorFocusAffordanceTests : IDisposable
{
    private readonly IApplication application = Application.Create();
    private readonly Window root = new();
    private readonly McpBrowserController controller;
    private readonly McpBrowserOverlay overlay;
    private readonly SessionToken? runState;

    public McpEditorFocusAffordanceTests()
    {
        this.application.AppModel = AppModel.FullScreen;
        this.application.Init(DriverRegistry.Names.ANSI);
        this.application.Driver!.SetScreenSize(72, 20);
        this.controller = new McpBrowserController(() => null);
        this.overlay = new McpBrowserOverlay(this.application, this.controller);
        this.root.Add(this.overlay);
        this.runState = this.application.Begin(this.root);
    }

    private void OpenEditor()
    {
        this.overlay.Show();
        this.controller.SetStateForTest(this.controller.State.BeginAdd(
            new McpManagementSnapshot(true, ImmutableArray<McpServerSummary>.Empty)));
        this.controller.NotifyChangedForTest();
        this.application.LayoutAndDraw();
    }

    private McpEditorForm Form() => this.overlay.SubViews.OfType<McpEditorForm>().Single();

    /// <summary>
    /// True when <paramref name="row"/> contains cells painted with the theme's selection
    /// background — the exact colour the cursor/focus inversion uses.
    /// </summary>
    /// <remarks>
    /// Asserting the specific colour rather than "some background differs" matters: rows contain
    /// border and gutter cells that differ for unrelated reasons, so a looser check passes even
    /// when no cursor is drawn at all.
    /// </remarks>
    private bool HasSelectionBackgroundOn(int row)
    {
        var driver = this.application.Driver!;
        var expected = TuiTheme.Resolve(
            CodaThemes.Current.Tui.SelectionBackground,
            TuiTheme.SupportsTrueColor(driver));

        for (var col = 0; col < driver.Cols; col++)
        {
            if (driver.Contents![row, col].Attribute?.Background is { } background
                && background.Equals(expected))
            {
                return true;
            }
        }

        return false;
    }

    private int RowOf(string text)
    {
        var driver = this.application.Driver!;
        for (var row = 0; row < driver.Rows; row++)
        {
            var line = new System.Text.StringBuilder();
            for (var col = 0; col < driver.Cols; col++)
            {
                line.Append(driver.Contents![row, col].Grapheme);
            }

            if (line.ToString().Contains(text, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return -1;
    }

    [Fact]
    public void Focused_selector_paints_a_visible_cursor_after_a_plain_show()
    {
        this.OpenEditor();

        var selector = this.Form().SubViews.OfType<OptionSelector>().First();
        selector.SetFocus();
        this.application.LayoutAndDraw();

        var row = this.RowOf("project");
        Assert.True(row >= 0, "the scope row should be rendered");

        // The cursor is a background inversion over the focused option. Without it the user can see
        // WHICH option is selected but not WHERE they are, which is the defect this guards.
        Assert.True(
            this.HasSelectionBackgroundOn(row),
            "the focused selector must paint a visible cursor over its focused option");
    }

    [Fact]
    public void Focused_button_is_visibly_inverted_after_a_plain_show()
    {
        this.OpenEditor();

        var save = this.Form().SubViews.OfType<Button>().First(b => b.Text.Contains("Save", StringComparison.Ordinal));
        save.SetFocus();
        this.application.LayoutAndDraw();

        var row = this.RowOf("Save");
        Assert.True(row >= 0, "the Save row should be rendered");
        Assert.True(
            this.HasSelectionBackgroundOn(row),
            "the focused button must be visibly inverted so it is clear which of Save/Cancel will fire");
    }

    public void Dispose()
    {
        this.overlay.Dispose();
        if (this.runState is not null)
        {
            this.application.End(this.runState);
        }

        this.root.Dispose();
        this.application.Dispose();
    }
}
