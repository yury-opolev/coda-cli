using Coda.Tui.Plugins;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Ui.Plugins;

/// <summary>
/// The <c>/plugin</c> browser overlay: a hidden-by-default, focused full-screen Terminal.Gui view
/// that renders <see cref="PluginBrowserController"/> state (plugin list or a single-plugin detail
/// pane) and routes keys through <see cref="PluginBrowserKeyMap"/>.
///
/// <para><b>Threading.</b> <see cref="PluginBrowserController.Changed"/> may fire on a background
/// pump thread. <see cref="OnControllerChanged"/> marshals every view mutation through
/// <see cref="IApplication.Invoke"/>.</para>
///
/// <para><b>Lifecycle.</b> <see cref="Show"/> and <see cref="Hide"/> are idempotent.</para>
/// </summary>
internal sealed class PluginBrowserOverlay : View, ISelectableOverlay
{
    private const int PageStep = 10;

    private readonly IApplication app;
    private readonly PluginBrowserController controller;
    private TuiTheme theme;
    private readonly Action? onChanged;
    private readonly StatusGlyphs statusGlyphs;

    private readonly Label header;
    private readonly SelectableTextView body;
    private readonly TableView listTable;
    private readonly Label status;
    private readonly Label footer;

    private BrowserSchemes? browserSchemes;

    private CancellationTokenSource? pumpCts;
    private bool active;
    private bool disposed;

    // Filter-mode state: / enters filter mode; keys go to the buffer; Esc exits filter first.
    private bool filterMode;
    private string filterBuffer = string.Empty;

    /// <summary>Creates the overlay bound to <paramref name="controller"/>.</summary>
    public PluginBrowserOverlay(
        IApplication app,
        PluginBrowserController controller,
        TuiTheme? theme = null,
        Action? onChanged = null,
        Action<string, Action>? onCopyRequested = null,
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

        this.header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, CanFocus = false };

        this.listTable = new TableView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            CanFocus = false,
            Visible = false,
        };
        this.listTable.Style.ShowHeaders = false;
        this.listTable.Style.ShowHorizontalHeaderUnderline = false;
        this.listTable.Style.ShowHorizontalHeaderOverline = false;
        this.listTable.Style.ShowVerticalCellLines = false;
        this.listTable.Style.ColumnStyles[0] = new ColumnStyle { MinWidth = 1, MaxWidth = 1, ColorGetter = this.GetStatusCellScheme };
        this.listTable.Style.ColumnStyles[1] = new ColumnStyle { MinWidth = 6, MaxWidth = 24 };
        this.listTable.Style.ColumnStyles[2] = new ColumnStyle { MinWidth = 3, MaxWidth = 10 };
        this.listTable.Style.ColumnStyles[3] = new ColumnStyle { MinWidth = 4, MaxWidth = 9 };
        this.listTable.Style.ColumnStyles[4] = new ColumnStyle { MinWidth = 0, MaxWidth = 4 };
        this.listTable.Style.RowColorGetter = this.GetRowScheme;

        this.body = new SelectableTextView(app)
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Visible = false,
        };
        if (onCopyRequested is not null)
        {
            this.body.CopyRequested += text => onCopyRequested(text, this.body.ClearSelection);
        }

        this.status = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, CanFocus = false };
        this.footer = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };

        this.Add(this.header, this.listTable, this.body, this.status, this.footer);

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
        this.body.ApplyTheme(this.theme, this.app.Driver);
        if (this.active)
        {
            this.Render();
        }
        else
        {
            this.SetNeedsDraw();
        }
    }

    /// <summary>True while the background change pump is running.</summary>
    internal bool IsPumping => this.pumpCts is not null;

    internal string HeaderText => this.header.Text ?? string.Empty;

    /// <summary>Returns body content (detail pane) or synthesized table row text (list pane).</summary>
    internal string BodyText => this.body.Visible ? this.body.AllText : this.SynthesizeListText();

    internal string StatusText => this.status.Text ?? string.Empty;

    internal string FooterText => this.footer.Text ?? string.Empty;

    /// <summary>The current table source (null when in detail view). Test seam for direct row inspection.</summary>
    internal PluginTableSource? ListTableSource { get; private set; }

    // -- ISelectableOverlay ---
    SelectableTextView ISelectableOverlay.Body => this.body;

    public void Show()
    {
        this.SetScheme(this.theme.SurfaceScheme(this.app.Driver));
        this.body.ApplyTheme(this.theme, this.app.Driver);

        if (this.active)
        {
            this.Visible = true;
            this.SetFocus();
            this.Render();
            return;
        }

        this.controller.Open();
        this.controller.Changed += this.OnControllerChanged;

        this.pumpCts = new CancellationTokenSource();
        this.Observe(this.controller.PumpAsync(this.pumpCts.Token));

        this.active = true;
        this.Visible = true;
        this.SetFocus();
        this.Render();
    }

    public void Hide()
    {
        if (!this.active)
        {
            return;
        }

        this.body.CancelMouseInteraction();
        this.Teardown();
        this.Visible = false;
        this.onChanged?.Invoke();
    }

    private void Teardown()
    {
        this.active = false;

        this.pumpCts?.Cancel();
        this.pumpCts?.Dispose();
        this.pumpCts = null;

        this.controller.Changed -= this.OnControllerChanged;
        this.controller.Close();
    }

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

            return true;
        }

        var command = PluginBrowserKeyMap.Map(key, this.controller.State.View);
        switch (command)
        {
            case PluginBrowserCommand.Close:
                this.Hide();
                return true;

            case PluginBrowserCommand.MoveUp:
                this.controller.MoveSelection(-1);
                this.Render();
                return true;

            case PluginBrowserCommand.MoveDown:
                this.controller.MoveSelection(1);
                this.Render();
                return true;

            case PluginBrowserCommand.PageUp:
                this.controller.MoveSelection(-PageStep);
                this.Render();
                return true;

            case PluginBrowserCommand.PageDown:
                this.controller.MoveSelection(PageStep);
                this.Render();
                return true;

            case PluginBrowserCommand.MoveToStart:
                this.controller.MoveToStart();
                this.Render();
                return true;

            case PluginBrowserCommand.MoveToEnd:
                this.controller.MoveToEnd();
                this.Render();
                return true;

            case PluginBrowserCommand.OpenDetail:
                this.controller.OpenDetail();
                return true;

            case PluginBrowserCommand.ReturnToList:
                this.controller.ReturnToList();
                return true;

            case PluginBrowserCommand.ToggleEnabled:
                this.controller.ToggleSelectedEnabled();
                return true;

            case PluginBrowserCommand.Reload:
                this.controller.Reload();
                return true;

            case PluginBrowserCommand.Update:
                this.Observe(this.controller.UpdateSelectedAsync(CancellationToken.None));
                return true;

            case PluginBrowserCommand.Filter:
                this.filterMode = true;
                this.filterBuffer = string.Empty;
                this.Render();
                return true;
        }

        return base.OnKeyDown(key);
    }

    private void OnControllerChanged()
    {
        try
        {
            this.app.Invoke(this.SafeRender);
        }
        catch (ObjectDisposedException)
        {
            // Swallow: application may be tearing down.
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
        if (state.View == PluginBrowserView.Detail && state.Detail is not null)
        {
            this.RenderDetail(state.Detail, state);
        }
        else
        {
            this.RenderList(state);
        }

        this.SetNeedsDraw();
    }

    private void RenderList(PluginBrowserState state)
    {
        var plugins = ApplyFilter(state.Plugins, this.filterBuffer);
        var count = state.Plugins.Count;
        var title = count == 1 ? "1 plugin" : $"{count} plugins";
        this.header.Text = $" Plugins — {title}";

        var source = new PluginTableSource(plugins, this.statusGlyphs, this.controller.IsTrusted);
        this.ListTableSource = source;
        this.listTable.Table = source;

        if (plugins.Count > 0)
        {
            var selIdx = plugins.ToList().FindIndex(p => p.Name == state.SelectedName);
            if (selIdx >= 0)
            {
                this.listTable.SetSelection(0, selIdx, false);
                this.listTable.EnsureCursorIsVisible();
            }
        }

        this.listTable.Visible = true;
        this.body.Visible = false;

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

        this.footer.Text = " ↑/↓ k/j move · Enter detail · Space toggle · r reload · u update · / filter · Esc q close";
    }

    private void RenderDetail(PluginInfo plugin, PluginBrowserState state)
    {
        this.header.Text = $" Plugin — {TerminalTextSanitizer.SanitizeSingleLine(plugin.Name)}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  name        {TerminalTextSanitizer.SanitizeSingleLine(plugin.Name)}");
        sb.AppendLine($"  version     {TerminalTextSanitizer.SanitizeSingleLine(plugin.Version)}");
        sb.AppendLine($"  description {TerminalTextSanitizer.SanitizeSingleLine(plugin.Description)}");
        sb.AppendLine($"  enabled     {(plugin.IsEnabled ? "yes" : "no")}");
        sb.AppendLine($"  trusted     {(this.controller.IsTrusted(plugin) ? "yes" : "no")}");
        sb.AppendLine($"  external    {(plugin.IsExternal ? "yes" : "no")}");
        sb.AppendLine($"  directory   {TerminalTextSanitizer.SanitizeSingleLine(plugin.Directory)}");

        this.body.SetText(sb.ToString());

        this.body.Visible = true;
        this.listTable.Visible = false;

        this.status.Text = state.StatusMessage is { Length: > 0 } m
            ? $" {TerminalTextSanitizer.SanitizeSingleLine(m)}"
            : string.Empty;
        this.footer.Text = " Esc q back · ↑/↓ k/j · Space toggle · r reload · u update";
    }

    private Scheme? GetRowScheme(RowColorGetterArgs args)
    {
        if (this.ListTableSource is null || args.RowIndex >= this.ListTableSource.Rows)
        {
            return null;
        }

        var plugin = this.ListTableSource.PluginAt(args.RowIndex);
        return this.EnsureSchemes().ForRow(PluginTableSource.GetState(plugin, this.controller.IsTrusted(plugin)));
    }

    private Scheme? GetStatusCellScheme(CellColorGetterArgs args)
    {
        if (this.ListTableSource is null || args.RowIndex >= this.ListTableSource.Rows)
        {
            return null;
        }

        var plugin = this.ListTableSource.PluginAt(args.RowIndex);
        return this.EnsureSchemes().For(PluginTableSource.GetState(plugin, this.controller.IsTrusted(plugin)));
    }

    private BrowserSchemes EnsureSchemes() =>
        this.browserSchemes ??= new BrowserSchemes(this.theme, this.app.Driver);

    private string SynthesizeListText()
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

    private static IReadOnlyList<PluginInfo> ApplyFilter(IReadOnlyList<PluginInfo> plugins, string filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return plugins;
        }

        return plugins.Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
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

    protected override void Dispose(bool disposing)
    {
        if (!this.disposed && disposing)
        {
            this.disposed = true;
            this.body.CancelMouseInteraction();
            if (this.active)
            {
                this.Teardown();
            }
        }

        base.Dispose(disposing);
    }

    private void Observe(Task task) =>
        task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
