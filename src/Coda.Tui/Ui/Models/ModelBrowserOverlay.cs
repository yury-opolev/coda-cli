using Coda.Sdk;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Ui.Models;

/// <summary>
/// The <c>/model</c> browser overlay: a hidden-by-default, focused full-screen Terminal.Gui view
/// that renders <see cref="ModelBrowserController"/> state (scrollable model list with header showing
/// provenance) and routes keys through <see cref="ModelBrowserKeyMap"/>.
///
/// <para><b>Threading.</b> <see cref="ModelBrowserController.Changed"/> may fire on a background
/// thread. <see cref="OnControllerChanged"/> marshals every view mutation through
/// <see cref="IApplication.Invoke"/> so no Terminal.Gui control is ever touched off the UI thread.</para>
///
/// <para><b>Lifecycle.</b> <see cref="Show"/> and <see cref="Hide"/> are idempotent. <see cref="Dispose"/>
/// mirrors <see cref="Hide"/>'s teardown. The overlay reports its selection result via the
/// <paramref name="onCompleted"/> callback passed to <see cref="Show"/>.</para>
/// </summary>
internal sealed class ModelBrowserOverlay : View
{
    private const int PageStep = 10;

    private readonly IApplication app;
    private readonly ModelBrowserController controller;
    private TuiTheme theme;
    private readonly Action? onChanged;
    private readonly StatusGlyphs statusGlyphs;

    private readonly Label header;
    private readonly TableView listTable;
    private readonly Label status;
    private readonly Label footer;

    private BrowserSchemes? browserSchemes;
    private bool active;
    private bool disposed;

    // Filter-mode state: / enters filter mode; keys go to the buffer; Esc exits filter first.
    private bool filterMode;
    private string filterBuffer = string.Empty;

    // Completion callback set on each Show call; null when no session is active.
    private Action<ModelSelection?>? onCompleted;

    // Reload factory set on each Show call; null when the caller cannot supply fresh data.
    private Func<CancellationToken, Task<ModelListResult>>? reloadFactory;

    /// <summary>Creates the overlay bound to <paramref name="controller"/>.</summary>
    public ModelBrowserOverlay(
        IApplication app,
        ModelBrowserController controller,
        TuiTheme? theme = null,
        Action? onChanged = null,
        StatusGlyphs? statusGlyphs = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.theme = theme ?? CodaThemes.Current.Tui;
        this.onChanged = onChanged;
        this.statusGlyphs = statusGlyphs ?? StatusGlyphs.Unicode;

        this.Visible = false;
        this.CanFocus = true;
        this.Width = Dim.Fill();
        this.Height = Dim.Fill();
        this.BorderStyle = LineStyle.Rounded;

        // One blank column inside each side of the border, so rows never butt up against the box
        // edge. Padding shrinks the Viewport, so Dim.Fill() children adjust with no other change.
        this.Padding.Thickness = new Thickness(1, 0, 1, 0);

        this.header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, CanFocus = false };

        this.listTable = new TableView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            CanFocus = false,
            Visible = true,
        };
        this.listTable.Style.ShowHeaders = false;
        this.listTable.Style.ShowHorizontalHeaderUnderline = false;
        this.listTable.Style.ShowHorizontalHeaderOverline = false;
        this.listTable.Style.ShowVerticalCellLines = false;
        this.listTable.Style.ColumnStyles[0] = new ColumnStyle { MinWidth = 1, MaxWidth = 1, ColorGetter = this.GetStatusCellScheme };
        this.listTable.Style.ColumnStyles[1] = new ColumnStyle { MinWidth = 8, MaxWidth = 40 };
        this.listTable.Style.ColumnStyles[2] = new ColumnStyle { MinWidth = 4, MaxWidth = 30, TruncationIndicator = "…" };
        this.listTable.Style.ColumnStyles[3] = new ColumnStyle { MinWidth = 3, MaxWidth = 6 };
        this.listTable.Style.ColumnStyles[4] = new ColumnStyle { MinWidth = 0, MaxWidth = 24, TruncationIndicator = "…" };
        this.listTable.Style.RowColorGetter = this.GetRowScheme;
        this.listTable.FullRowSelect = true;

        this.status = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, CanFocus = false };
        this.footer = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };

        this.Add(this.header, this.listTable, this.status, this.footer);

        // On resize the viewport shrinks underneath a stale row offset; scroll selection back into view.
        this.listTable.FrameChanged += (_, _) =>
        {
            if (this.active && this.listTable.Visible)
            {
                this.listTable.EnsureCursorIsVisible();
            }
        };
    }

    /// <summary>Re-applies the surface theme and re-renders (if active).</summary>
    internal void ApplyTheme(TuiTheme theme)
    {
        this.theme = theme ?? throw new ArgumentNullException(nameof(theme));
        this.browserSchemes = null;
        this.SetScheme(this.theme.SurfaceScheme(this.app.Driver));
        if (this.active)
        {
            this.Render();
        }
        else
        {
            this.SetNeedsDraw();
        }
    }

    internal string HeaderText => this.header.Text ?? string.Empty;
    internal string StatusText => this.status.Text ?? string.Empty;
    internal string FooterText => this.footer.Text ?? string.Empty;

    /// <summary>The current table source (null when inactive). Test seam for direct row inspection.</summary>
    internal ModelTableSource? ListTableSource { get; private set; }

    // ── Show / Hide / Teardown ────────────────────────────────────────────────

    /// <summary>
    /// <summary>
    /// Opens the controller with the given <paramref name="result"/>, subscribes to changes, focuses,
    /// renders, and wires <paramref name="onCompleted"/> to be called with the chosen model id
    /// (or <c>null</c> when the user dismisses) exactly once.
    /// </summary>
    /// <param name="onReload">
    /// Optional factory that re-fetches the model list (e.g. by clearing the provider cache and
    /// calling the live endpoint). When non-null the overlay fires a real re-resolve when the user
    /// presses <c>r</c>. When null, <c>r</c> is ignored.
    /// </param>
    public void Show(
        ModelListResult result,
        string? currentModelId,
        Action<ModelSelection?> onCompleted,
        Func<CancellationToken, Task<ModelListResult>>? onReload = null,
        IReadOnlyDictionary<string, string>? initialEffortByModel = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onCompleted);

        this.SetScheme(this.theme.SurfaceScheme(this.app.Driver));
        this.onCompleted = onCompleted;
        this.reloadFactory = onReload;
        this.filterMode = false;
        this.filterBuffer = string.Empty;

        if (!this.active)
        {
            this.controller.Changed += this.OnControllerChanged;
            this.active = true;
        }

        this.controller.Open(result, currentModelId, initialEffortByModel);
        this.Visible = true;
        this.SetFocus();
        this.Render();
    }

    /// <summary>Completes with <c>null</c>, unsubscribes, and hides.</summary>
    public void Hide()
    {
        if (!this.active)
        {
            return;
        }

        this.Teardown(null);
        this.Visible = false;
        this.onChanged?.Invoke();
    }

    private void Teardown(ModelSelection? result)
    {
        this.active = false;
        this.controller.Changed -= this.OnControllerChanged;
        this.controller.Close();

        var cb = this.onCompleted;
        this.onCompleted = null;
        this.reloadFactory = null;

        // Invoke the callback outside the overlay's own lock (none here, but outside the view mutation path)
        // so callers can safely set another Show from within the callback.
        try
        {
            cb?.Invoke(result);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ── Key routing ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override bool OnKeyDown(Key key)
    {
        if (key is null)
        {
            return false;
        }

        if (!this.Visible)
        {
            return base.OnKeyDown(key);
        }

        // Filter mode: keys go to the filter buffer; Esc exits filter (does not close browser).
        if (this.filterMode)
        {
            if (key == Key.Esc)
            {
                this.filterMode = false;
                this.filterBuffer = string.Empty;
                this.Render();
                return true;
            }

            if (key == Key.Backspace && this.filterBuffer.Length > 0)
            {
                this.filterBuffer = this.filterBuffer[..^1];
                this.Render();
                return true;
            }

            if (TryGetPrintable(key, out var ch))
            {
                this.filterBuffer += ch;
                this.Render();
                return true;
            }

            return true; // swallow all other keys in filter mode
        }

        var command = ModelBrowserKeyMap.Map(key);
        switch (command)
        {
            case ModelBrowserCommand.Close:
                var resultOnClose = this.controller.State.SelectedId;
                this.Teardown(null);
                this.Visible = false;
                this.onChanged?.Invoke();
                _ = resultOnClose; // not used; Close returns null
                return true;

            case ModelBrowserCommand.MoveUp:
                this.controller.MoveSelection(-1);
                this.SyncTableSelection();
                return true;

            case ModelBrowserCommand.MoveDown:
                this.controller.MoveSelection(1);
                this.SyncTableSelection();
                return true;

            case ModelBrowserCommand.PageUp:
                this.controller.MoveSelection(-PageStep);
                this.SyncTableSelection();
                return true;

            case ModelBrowserCommand.PageDown:
                this.controller.MoveSelection(PageStep);
                this.SyncTableSelection();
                return true;

            case ModelBrowserCommand.MoveToStart:
                this.controller.MoveToStart();
                this.SyncTableSelection();
                return true;

            case ModelBrowserCommand.MoveToEnd:
                this.controller.MoveToEnd();
                this.SyncTableSelection();
                return true;

            case ModelBrowserCommand.Select:
            {
                var state = this.controller.State;
                var selected = state.SelectedId;
                if (selected is not null)
                {
                    // Convert "auto" (the display sentinel) to null so callers can distinguish
                    // "user explicitly chose auto" from "user made no choice at all".
                    var rawEffort = state.EffortFor(selected);
                    var effort = string.Equals(rawEffort, "auto", StringComparison.OrdinalIgnoreCase)
                        ? null : rawEffort;
                    this.Teardown(new ModelSelection(selected, effort) { EffortChosen = true });
                    this.Visible = false;
                    this.onChanged?.Invoke();
                }

                return true;
            }

            case ModelBrowserCommand.EffortLeft:
                this.controller.CycleEffort(-1);
                return true;

            case ModelBrowserCommand.EffortRight:
                this.controller.CycleEffort(+1);
                return true;

            case ModelBrowserCommand.Reload:
                this.StartReload();
                return true;

            case ModelBrowserCommand.Filter:
                this.filterMode = true;
                this.filterBuffer = string.Empty;
                this.Render();
                return true;
        }

        return base.OnKeyDown(key);
    }

    // ── Render ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fires an async re-resolve of the model list when <see cref="reloadFactory"/> is available.
    /// Updates the status to "reloading…" immediately, then calls
    /// <see cref="ModelBrowserController.UpdateResult"/> on completion so the table re-renders with
    /// the fresh list.
    /// </summary>
    private void StartReload()
    {
        if (this.reloadFactory is null)
        {
            // No reload factory was provided; silently ignore (no footer entry to mislead the user
            // because the footer is only rendered when the factory is set — see RenderList).
            return;
        }

        this.status.Text = " reloading…";
        this.SetNeedsDraw();

        var factory = this.reloadFactory;
        _ = Task.Run(async () =>
        {
            try
            {
                var fresh = await factory(CancellationToken.None).ConfigureAwait(false);
                // UpdateResult is thread-safe (uses lock) and fires Changed, which marshals the
                // render through app.Invoke via OnControllerChanged — no extra Invoke needed here.
                if (this.active && !this.disposed)
                {
                    this.controller.UpdateResult(fresh);
                }
            }
            catch
            {
                this.app.Invoke(() =>
                {
                    if (this.active && !this.disposed)
                    {
                        this.status.Text = " reload failed";
                        this.SetNeedsDraw();
                    }
                });
            }
        });
    }

    private void OnControllerChanged()
    {
        try
        {
            this.app.Invoke(this.SafeRender);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SafeRender()
    {
        if (!this.disposed && this.active)
        {
            this.Render();
        }
    }

    private void Render()
    {
        var state = this.controller.State;
        this.RenderList(state);
        this.SetNeedsDraw();
    }

    private void RenderList(ModelBrowserState state)
    {
        var result = state.Result;
        var models = result?.Models ?? [];
        var filtered = ApplyFilter(models, this.filterBuffer);

        // Header: current model + provenance + built-in warning.
        var source = result?.Source switch
        {
            ModelSource.Live => "live",
            ModelSource.Catalog => "models.dev catalog",
            ModelSource.BuiltIn => "built-in fallback",
            _ => string.Empty,
        };

        var currentLabel = state.CurrentModelId is { Length: > 0 } id
            ? $" Current: {TerminalTextSanitizer.SanitizeSingleLine(id)}"
            : string.Empty;

        var sourceLabel = source.Length > 0 ? $" [{source}]" : string.Empty;
        var warning = result?.Source == ModelSource.BuiltIn
            ? " ⚠ live list and catalog unavailable — try r to refresh"
            : string.Empty;

        this.header.Text = $" Models{currentLabel}{sourceLabel}{warning}";

        // Build / update the table source.
        var source2 = new ModelTableSource(filtered, state.CurrentModelId, this.statusGlyphs,
            state.SelectedId, state.EffortByModel);
        this.ListTableSource = source2;
        this.listTable.Table = source2;

        // Sync table selection to controller state.
        if (filtered.Count > 0)
        {
            var selIdx = filtered.ToList().FindIndex(m =>
                string.Equals(m.Id, state.SelectedId, StringComparison.OrdinalIgnoreCase));
            if (selIdx >= 0)
            {
                this.listTable.SetSelection(0, selIdx, false);
                this.listTable.EnsureCursorIsVisible();
            }
        }

        // Status row: filter indicator, controller status, or empty.
        if (this.filterMode)
        {
            this.status.Text = $" filter: {this.filterBuffer}▏";
        }
        else if (state.StatusMessage is { Length: > 0 } msg)
        {
            this.status.Text = $" {TerminalTextSanitizer.SanitizeSingleLine(msg)}";
        }
        else
        {
            this.status.Text = string.Empty;
        }

        this.footer.Text = " ↑/↓ k/j move · ←/→ effort · Enter select · r reload · / filter · Esc q close";
    }

    // Sync the table's visual selection without re-building the source (called after navigation).
    private void SyncTableSelection()
    {
        var state = this.controller.State;
        if (this.ListTableSource is null)
        {
            return;
        }

        var filtered = ApplyFilter(state.Models, this.filterBuffer);
        var selIdx = filtered.ToList().FindIndex(m =>
            string.Equals(m.Id, state.SelectedId, StringComparison.OrdinalIgnoreCase));
        if (selIdx >= 0)
        {
            this.listTable.SetSelection(0, selIdx, false);
            this.listTable.EnsureCursorIsVisible();
        }

        this.SetNeedsDraw();
    }

    // ── Colour getters for the table ──────────────────────────────────────────

    private Scheme? GetRowScheme(RowColorGetterArgs args)
    {
        if (this.IsSelectedRow(args.RowIndex))
        {
            return this.EnsureSchemes().Selection;
        }

        if (this.ListTableSource is null || args.RowIndex >= this.ListTableSource.Rows)
        {
            return null;
        }

        var model = this.ListTableSource.ModelAt(args.RowIndex);
        return this.EnsureSchemes().ForRow(ModelTableSource.GetState(model, this.controller.State.CurrentModelId));
    }

    private Scheme? GetStatusCellScheme(CellColorGetterArgs args)
    {
        if (this.IsSelectedRow(args.RowIndex))
        {
            return this.EnsureSchemes().Selection;
        }

        if (this.ListTableSource is null || args.RowIndex >= this.ListTableSource.Rows)
        {
            return null;
        }

        var model = this.ListTableSource.ModelAt(args.RowIndex);
        return this.EnsureSchemes().For(ModelTableSource.GetState(model, this.controller.State.CurrentModelId));
    }

    /// <summary>
    /// Whether <paramref name="rowIndex"/> is the row the table cursor is on, which the colour
    /// getters paint with the inverted selection scheme. Read from the table rather than from
    /// controller state so a filtered list, whose row indices no longer match the unfiltered
    /// state, still highlights the row the user is actually on.
    /// </summary>
    private bool IsSelectedRow(int rowIndex) =>
        this.listTable.Value is { } selection && selection.SelectedCell.Y == rowIndex;

    private BrowserSchemes EnsureSchemes() =>
        this.browserSchemes ??= new BrowserSchemes(this.theme, this.app.Driver);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Synthesizes the table content as plain text (test seam for rendered rows).</summary>
    internal string SynthesizeListText()
    {
        if (this.ListTableSource is null)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        for (var r = 0; r < this.ListTableSource.Rows; r++)
        {
            for (var c = 0; c < this.ListTableSource.Columns; c++)
            {
                if (c > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(this.ListTableSource[r, c]);
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static IReadOnlyList<ModelListEntry> ApplyFilter(IReadOnlyList<ModelListEntry> models, string filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return models;
        }

        return models.Where(m =>
            m.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            (m.DisplayName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)).ToList();
    }

    private static bool TryGetPrintable(Key key, out string text)
    {
        var rune = key.AsRune;
        if (rune.Value > 0x1F && !char.IsControl((char)rune.Value))
        {
            text = rune.ToString();
            return true;
        }

        text = string.Empty;
        return false;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!this.disposed && disposing)
        {
            this.disposed = true;
            if (this.active)
            {
                this.Teardown(null);
            }
        }

        base.Dispose(disposing);
    }
}
