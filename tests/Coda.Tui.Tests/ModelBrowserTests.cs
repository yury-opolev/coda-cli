using System.Threading;
using Coda.Sdk;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Models;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

/// <summary>
/// ANSI-driver render/interaction coverage for <see cref="ModelBrowserOverlay"/> and
/// <see cref="ModelTableSource"/>. All tests that touch Terminal.Gui are isolated by the
/// <c>TerminalGuiInit</c> collection to avoid races on the process-global application state.
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class ModelBrowserTests : IDisposable
{
    private readonly IApplication _app;
    private readonly ModelBrowserController _controller;
    private readonly ModelBrowserOverlay _overlay;
    private readonly Window _host;
    private readonly SessionToken? _token;

    public ModelBrowserTests()
    {
        this._app = Application.Create();
        this._app.AppModel = AppModel.FullScreen;
        this._app.Init(DriverRegistry.Names.ANSI);
        this._app.Driver!.SetScreenSize(80, 30);

        this._controller = new ModelBrowserController();
        this._overlay = new ModelBrowserOverlay(
            this._app,
            this._controller,
            TuiTheme.WarmEmber,
            statusGlyphs: StatusGlyphs.Ascii);

        this._host = new Window();
        this._host.Add(this._overlay);
        this._token = this._app.Begin(this._host);
    }

    public void Dispose()
    {
        if (this._token is { } t)
        {
            this._app.End(t);
        }

        this._overlay.Dispose();
        this._host.Dispose();
        this._app.Dispose();
    }

    private static ModelListResult MakeResult(
        ModelSource source = ModelSource.Live,
        int count = 3,
        string? currentModel = null,
        IReadOnlyList<string>? reasoningLevels = null)
    {
        var models = Enumerable.Range(1, count)
            .Select(i => new ModelListEntry(
                $"model-{i:D2}",
                $"Model {i}",
                i * 100_000,
                i == 1 ? reasoningLevels : null))
            .ToList();
        return new ModelListResult("provider", source, models);
    }

    private void ShowWith(ModelListResult result, string? currentModel = null)
    {
        this._overlay.Show(result, currentModel, _ => { });
        this._app.LayoutAndDraw();
    }

    // ── Test 1: Scrolling ──────────────────────────────────────────────────────

    [Fact]
    public void List_longer_than_viewport_can_reach_last_entry_via_End()
    {
        // 25 models in a 30-row screen: the list body height is well under 25 rows, so scrolling is needed.
        var result = MakeResult(count: 25);
        this.ShowWith(result);

        // Press End; the controller should select the last model.
        this._overlay.NewKeyDownEvent(Key.End);

        Assert.Equal("model-25", this._controller.State.SelectedId);
    }

    [Fact]
    public void PageDown_advances_selection_by_a_page_and_stays_in_bounds()
    {
        var result = MakeResult(count: 25);
        this.ShowWith(result);

        // Start at first row; PageDown advances by 10 (PageStep).
        this._overlay.NewKeyDownEvent(Key.PageDown);
        Assert.Equal("model-11", this._controller.State.SelectedId);

        // Multiple PageDowns should not go past the last row.
        for (var i = 0; i < 5; i++)
        {
            this._overlay.NewKeyDownEvent(Key.PageDown);
        }

        Assert.Equal("model-25", this._controller.State.SelectedId);
    }

    // ── Test 2: Filter ────────────────────────────────────────────────────────

    [Fact]
    public void Filter_narrows_rows_and_first_Esc_exits_filter_without_closing()
    {
        var result = MakeResult(count: 5);
        this.ShowWith(result);

        // Enter filter mode with '/'.
        this._overlay.NewKeyDownEvent(new Key('/'));
        Assert.True(this._overlay.StatusText.Contains("filter:", StringComparison.Ordinal));

        // Type "model-03" (only one should match).
        foreach (var ch in "model-03")
        {
            this._overlay.NewKeyDownEvent(new Key(ch));
        }

        this._app.LayoutAndDraw();
        Assert.NotNull(this._overlay.ListTableSource);
        Assert.Equal(1, this._overlay.ListTableSource!.Rows);

        // First Esc exits filter (overlay stays open).
        this._overlay.NewKeyDownEvent(Key.Esc);
        Assert.False(this._overlay.StatusText.Contains("filter:", StringComparison.Ordinal));
        Assert.True(this._overlay.Visible);
    }

    [Fact]
    public void Second_Esc_after_exiting_filter_closes_the_overlay()
    {
        var result = MakeResult(count: 3);
        this.ShowWith(result);

        // Enter and exit filter mode.
        this._overlay.NewKeyDownEvent(new Key('/'));
        this._overlay.NewKeyDownEvent(Key.Esc);
        Assert.True(this._overlay.Visible);

        // Second Esc closes.
        this._overlay.NewKeyDownEvent(Key.Esc);
        Assert.False(this._overlay.Visible);
    }

    // ── Test 3: Current model is marked ───────────────────────────────────────

    [Fact]
    public void Current_model_row_uses_Healthy_state_and_others_use_Idle()
    {
        var result = MakeResult(count: 3, currentModel: "model-02");
        this.ShowWith(result, currentModel: "model-02");

        Assert.NotNull(this._overlay.ListTableSource);
        var source = this._overlay.ListTableSource!;

        // Row index 1 is model-02 (0-based).
        Assert.Equal(BrowserItemState.Healthy, ModelTableSource.GetState(source.ModelAt(1), "model-02"));
        Assert.Equal(BrowserItemState.Idle, ModelTableSource.GetState(source.ModelAt(0), "model-02"));
        Assert.Equal(BrowserItemState.Idle, ModelTableSource.GetState(source.ModelAt(2), "model-02"));
    }

    [Fact]
    public void Current_model_glyph_appears_in_table_source_for_marked_row()
    {
        var glyphs = StatusGlyphs.Ascii;
        var entries = new List<ModelListEntry>
        {
            new("alpha", "Alpha", 100_000),
            new("beta", "Beta", 200_000),
        };
        var source = new ModelTableSource(entries, "beta", glyphs);

        // Column 0 is the status glyph column.
        Assert.Equal(glyphs[BrowserItemState.Idle], source[0, 0]);    // alpha — not current
        Assert.Equal(glyphs[BrowserItemState.Healthy], source[1, 0]); // beta  — current
    }

    // ── Test 4: Provenance in header ──────────────────────────────────────────

    [Fact]
    public void Header_shows_live_source_label()
    {
        this.ShowWith(MakeResult(ModelSource.Live));
        Assert.Contains("[live]", this._overlay.HeaderText, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", this._overlay.HeaderText, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_shows_catalog_source_label()
    {
        this.ShowWith(MakeResult(ModelSource.Catalog));
        Assert.Contains("[models.dev catalog]", this._overlay.HeaderText, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", this._overlay.HeaderText, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_shows_builtin_source_label_and_fallback_warning()
    {
        this.ShowWith(MakeResult(ModelSource.BuiltIn));
        Assert.Contains("[built-in fallback]", this._overlay.HeaderText, StringComparison.Ordinal);
        // The warning is shown only for BuiltIn.
        Assert.Contains("unavailable", this._overlay.HeaderText, StringComparison.Ordinal);
    }

    // ── Test 5: Reasoning levels are rendered ─────────────────────────────────

    [Fact]
    public void Reasoning_levels_appear_in_table_source()
    {
        var levels = (IReadOnlyList<string>)["low", "medium", "high"];
        var entries = new List<ModelListEntry>
        {
            new("smart-model", "Smart", 200_000, levels),
        };
        var source = new ModelTableSource(entries, null, StatusGlyphs.Ascii);

        // Column 4 is the Reasoning column.
        var rendered = source[0, 4].ToString()!;
        Assert.Contains("low", rendered, StringComparison.Ordinal);
        Assert.Contains("medium", rendered, StringComparison.Ordinal);
        Assert.Contains("high", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Reasoning_levels_column_is_empty_when_not_set()
    {
        var entries = new List<ModelListEntry>
        {
            new("basic-model", "Basic"),
        };
        var source = new ModelTableSource(entries, null, StatusGlyphs.Ascii);

        Assert.Equal(string.Empty, source[0, 4]);
    }

    // ── Test 6: Enter applies the selected model ───────────────────────────────

    [Fact]
    public void Enter_invokes_completion_callback_with_selected_id_and_closes_overlay()
    {
        var result = MakeResult(count: 3);
        string? chosen = null;
        this._overlay.Show(result, null, id => chosen = id);
        this._app.LayoutAndDraw();

        // Move to second row (model-02) then press Enter.
        this._overlay.NewKeyDownEvent(Key.CursorDown);
        this._overlay.NewKeyDownEvent(Key.Enter);

        Assert.Equal("model-02", chosen);
        Assert.False(this._overlay.Visible);
    }

    [Fact]
    public void Esc_invokes_completion_callback_with_null_and_closes_overlay()
    {
        var result = MakeResult(count: 3);
        var callbackInvoked = false;
        string? chosen = "initial";
        this._overlay.Show(result, null, id =>
        {
            callbackInvoked = true;
            chosen = id;
        });
        this._app.LayoutAndDraw();

        this._overlay.NewKeyDownEvent(Key.Esc);

        Assert.True(callbackInvoked);
        Assert.Null(chosen);
        Assert.False(this._overlay.Visible);
    }

    // ── Test 7: /model <id> bypasses the browser ──────────────────────────────

    [Fact]
    public async Task ModelCommand_with_id_arg_sets_model_without_opening_browser()
    {
        var (app, context, _, _) = TestAppBuilder.BuildApp();

        // Wire a model browser service that would fail if called.
        var browserCalled = false;
        context.ModelBrowserService = new RecordingModelBrowserService(() => browserCalled = true);

        await app.DispatchAsync(ParsedInput.Slash("model", ["claude-opus-4-8"]), CancellationToken.None);

        Assert.Equal("claude-opus-4-8", context.Session.Model);
        Assert.False(browserCalled, "/model <id> must not open the browser");
    }

    // ── Test 8: No ANSI escape codes in rendered output ───────────────────────

    [Fact]
    public void No_ANSI_escape_in_header_status_or_footer_text()
    {
        this.ShowWith(MakeResult(ModelSource.Live, currentModel: "model-01"), currentModel: "model-01");

        Assert.DoesNotContain('\u001b', this._overlay.HeaderText);
        Assert.DoesNotContain('\u001b', this._overlay.StatusText);
        Assert.DoesNotContain('\u001b', this._overlay.FooterText);
    }

    [Fact]
    public void No_ANSI_escape_in_synthesized_list_text()
    {
        this.ShowWith(MakeResult(ModelSource.Catalog, count: 3));
        var text = this._overlay.SynthesizeListText();
        Assert.DoesNotContain('\u001b', text);
    }

    // ── Test 9: r reloads the model list via the factory ─────────────────────

    /// <summary>
    /// Pressing <c>r</c> when a reload factory is wired must re-resolve the list.
    /// The controller's result changes to the factory's return value, proving the reload
    /// goes through a real re-fetch rather than being a no-op.
    /// </summary>
    [Fact]
    public void R_triggers_reload_factory_and_updates_the_model_list()
    {
        var fetchCount = 0;
        var original = MakeResult(count: 3);
        var refreshed = MakeResult(count: 5); // different entry count so we can detect the swap

        var result = MakeResult(count: 3);
        string? chosen = null;
        this._overlay.Show(result, null, id => chosen = id, onReload: async ct =>
        {
            fetchCount++;
            await Task.Yield(); // genuine async hop
            return refreshed;
        });
        this._app.LayoutAndDraw();

        // Press r; the reload fires asynchronously on a background Task.
        this._overlay.NewKeyDownEvent(new Key('r'));

        // Spin until the controller's Models list updates (up to 5 s).
        // controller.UpdateResult is called directly on the Task thread (thread-safe via lock),
        // so no app.Invoke draining is needed — we just wait for the state to change.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (this._controller.State.Models.Count != 5 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        // The factory must have been called exactly once.
        Assert.Equal(1, fetchCount);

        // After the reload the controller must hold the fresh result (5 entries, not 3).
        Assert.Equal(5, this._controller.State.Models.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A minimal <see cref="IModelBrowserService"/> that records whether it was called, used to verify
    /// that direct <c>/model id</c> invocations never open the interactive browser.
    /// </summary>
    private sealed class RecordingModelBrowserService : IModelBrowserService
    {
        private readonly Action onCalled;

        public RecordingModelBrowserService(Action onCalled)
        {
            this.onCalled = onCalled;
        }

        public Task<string?> SelectModelAsync(
            ModelListResult result,
            string? currentModelId,
            CancellationToken cancellationToken = default)
        {
            this.onCalled();
            return Task.FromResult<string?>(null);
        }
    }
}

/// <summary>
/// Unit tests for <see cref="ModelTableSource"/> that do not require an initialized Terminal.Gui
/// application. These can run without the <c>TerminalGuiInit</c> collection constraint.
/// </summary>
public sealed class ModelTableSourceTests
{
    [Fact]
    public void Context_format_renders_megabytes_and_kilobytes_correctly()
    {
        Assert.Equal("1M", ModelTableSource.FormatContext(1_000_000));
        Assert.Equal("1.5M", ModelTableSource.FormatContext(1_500_000));
        Assert.Equal("200K", ModelTableSource.FormatContext(200_000));
        Assert.Equal("1K", ModelTableSource.FormatContext(1_000));
        Assert.Equal("512", ModelTableSource.FormatContext(512));
    }

    [Fact]
    public void Source_returns_Healthy_for_current_and_Idle_for_others()
    {
        var model = new ModelListEntry("my-model");
        Assert.Equal(BrowserItemState.Healthy, ModelTableSource.GetState(model, "my-model"));
        Assert.Equal(BrowserItemState.Healthy, ModelTableSource.GetState(model, "MY-MODEL")); // case-insensitive
        Assert.Equal(BrowserItemState.Idle, ModelTableSource.GetState(model, "other-model"));
        Assert.Equal(BrowserItemState.Idle, ModelTableSource.GetState(model, null));
    }

    [Fact]
    public void Table_source_columns_and_rows_are_consistent()
    {
        var entries = new List<ModelListEntry>
        {
            new("id-1", "Name 1", 100_000, ["low", "high"]),
            new("id-2"),
        };
        var source = new ModelTableSource(entries, "id-1", StatusGlyphs.Unicode);

        Assert.Equal(5, source.Columns);
        Assert.Equal(2, source.Rows);

        // Row 0 is id-1 (current).
        Assert.Equal(StatusGlyphs.Unicode[BrowserItemState.Healthy], source[0, 0]);
        Assert.Equal("id-1", source[0, 1]);
        Assert.Equal("Name 1", source[0, 2]);
        Assert.Equal("100K", source[0, 3]);
        Assert.Contains("low", source[0, 4].ToString()!, StringComparison.Ordinal);

        // Row 1 is id-2 (not current, no display name, no context, no reasoning).
        Assert.Equal(StatusGlyphs.Unicode[BrowserItemState.Idle], source[1, 0]);
        Assert.Equal("id-2", source[1, 1]);
        Assert.Equal(string.Empty, source[1, 2]);
        Assert.Equal(string.Empty, source[1, 3]);
        Assert.Equal(string.Empty, source[1, 4]);
    }
}
