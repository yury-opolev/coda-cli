using System.Collections.Immutable;
using System.Text;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Ui.Mcp;

/// <summary>
/// Full-screen Terminal.Gui view for the interactive MCP manager. The controller owns all state and
/// asynchronous work; this view only renders sanitized state and dispatches input.
/// </summary>
internal sealed class McpBrowserOverlay : View, ISelectableOverlay
{
    private readonly IApplication app;
    private readonly McpBrowserController controller;
    private TuiTheme theme;
    private readonly Action? onChanged;
    private readonly Label header;
    private readonly SelectableTextView body;
    private readonly TableView listTable;
    private readonly McpEditorForm editorForm;
    private readonly Label status;
    private readonly Label footer;

    private BrowserSchemes? browserSchemes;

    private CancellationTokenSource? lifetime;
    private bool active;
    private bool subscribed;
    private bool disposed;
    private int detailOffset;

    /// <summary>
    /// The glyph set for status cells, chosen once from the terminal's Unicode capability so a
    /// terminal that cannot draw geometric shapes still gets a legible status column.
    /// </summary>
    private readonly StatusGlyphs statusGlyphs;

    internal McpBrowserOverlay(
        IApplication app,
        McpBrowserController controller,
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

        this.header = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false,
        };
        this.body = new SelectableTextView(app)
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
        };
        if (onCopyRequested is not null)
        {
            this.body.CopyRequested += text => onCopyRequested(text, this.body.ClearSelection);
        }

        this.listTable = new TableView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            CanFocus = false,
            Visible = false,
        };

        // No column headers. The overlay body is only a handful of rows tall — at 24x8 a header plus
        // its underline consumed the entire viewport and the list rendered zero servers. The columns
        // are self-describing (a status glyph, a name, a transport tag), so the two rows are better
        // spent on data.
        this.listTable.Style.ShowHeaders = false;
        this.listTable.Style.ShowHorizontalHeaderUnderline = false;
        this.listTable.Style.ShowHorizontalHeaderOverline = false;
        this.listTable.Style.ShowVerticalCellLines = false;
        this.listTable.Style.ColumnStyles[0] = new ColumnStyle
        {
            MinWidth = 1,
            MaxWidth = 1,
            ColorGetter = this.GetStatusCellScheme,
        };
        this.listTable.Style.ColumnStyles[1] = new ColumnStyle { MinWidth = 6, MaxWidth = 25 };
        this.listTable.Style.ColumnStyles[2] = new ColumnStyle
        {
            MinWidth = 4,
            MaxWidth = 5,
            ColorGetter = this.GetTransportCellScheme,
        };
        this.listTable.Style.ColumnStyles[3] = new ColumnStyle { MinWidth = 4, MaxWidth = 7 };
        this.listTable.Style.ColumnStyles[4] = new ColumnStyle { MinWidth = 0, MaxWidth = 4 };
        this.listTable.Style.ColumnStyles[5] = new ColumnStyle { MinWidth = 0, MaxWidth = 30, TruncationIndicator = "…" };
        this.listTable.Style.RowColorGetter = this.GetRowScheme;

        this.status = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false,
        };
        this.footer = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false,
        };
        this.editorForm = new McpEditorForm(controller)
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Visible = false,
        };
        this.editorForm.SaveRequested += () =>
            this.Observe(this.controller.ExecuteAsync(
                McpBrowserCommand.EditorApply, null, this.lifetime?.Token ?? CancellationToken.None));
        this.editorForm.CancelRequested += () =>
            this.Observe(this.controller.ExecuteAsync(
                McpBrowserCommand.EditorCancel, null, this.lifetime?.Token ?? CancellationToken.None));

        this.Add(this.header, this.body, this.listTable, this.editorForm, this.status, this.footer);
        this.FrameChanged += (_, _) =>
        {
            if (this.active)
            {
                this.Render();
            }
        };
        this.body.FrameChanged += (_, _) =>
        {
            if (this.active)
            {
                this.Render();
            }
        };

        // The table's own frame settles after Render has already run, so scrolling the selection
        // into view has to happen here as well: on a resize the viewport shrinks underneath a
        // row offset that was computed for the old height, leaving the selected server off screen.
        this.listTable.FrameChanged += (_, _) =>
        {
            if (this.active && this.listTable.Visible)
            {
                this.listTable.EnsureCursorIsVisible();
            }
        };
    }

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

    internal string HeaderText => this.header.Text ?? string.Empty;

    internal string BodyText => this.body.AllText;

    internal string StatusText => this.status.Text ?? string.Empty;

    internal string FooterText => this.footer.Text ?? string.Empty;

    /// <summary>Render-only test seam containing exactly the sanitized strings assigned to visible labels.</summary>
    internal string VisibleTextForTest { get; private set; } = string.Empty;

    // ── ISelectableOverlay ────────────────────────────────────────────────────
    SelectableTextView ISelectableOverlay.Body => this.body;

    internal void Show()
    {
        if (this.disposed || this.active)
        {
            if (this.active)
            {
                this.Visible = true;
                this.SetFocus();
                this.Render();
            }

            return;
        }

        this.lifetime = new CancellationTokenSource();
        this.SetScheme(this.theme.SurfaceScheme(this.app.Driver));
        this.body.ApplyTheme(this.theme, this.app.Driver);
        this.active = true;
        this.Visible = true;
        this.controller.Changed += this.OnControllerChanged;
        this.subscribed = true;

        try
        {
            this.controller.Open();
            this.SetFocus();
            this.Render();
        }
        catch
        {
            this.Hide();
            throw;
        }
    }

    internal void Hide()
    {
        if (!this.active && !this.subscribed && this.lifetime is null)
        {
            this.Visible = false;
            return;
        }

        this.body.CancelMouseInteraction();
        this.active = false;
        this.Visible = false;

        if (this.subscribed)
        {
            this.controller.Changed -= this.OnControllerChanged;
            this.subscribed = false;
        }

        this.lifetime?.Cancel();
        this.lifetime?.Dispose();
        this.lifetime = null;
        this.controller.Close();
        this.onChanged?.Invoke();
    }

    protected override bool OnKeyDown(Key key)
    {
        if (!this.Visible)
        {
            return false;
        }

        // Copy an active body selection before anything else claims the chord.
        if (key == Key.C.WithCtrl && this.body.TryCopySelection())
        {
            return true;
        }

        var command = McpBrowserKeyMap.Map(key, this.controller.State.View);
        if (command == McpBrowserCommand.None)
        {
            if (this.controller.State.View == McpBrowserView.Detail && this.TryScrollDetail(key))
            {
                this.Render();
                return true;
            }

            // Not one of ours: leave it unhandled so a focused child view can act on it. Returning
            // true unconditionally here would make every child widget deaf. The shell already
            // declines keys while a browser overlay is visible, so an unclaimed key is simply
            // dropped rather than leaking to the composer.
            return false;
        }

        var token = this.lifetime?.Token ?? CancellationToken.None;
        this.Observe(this.controller.ExecuteAsync(command, key, token));
        if (command == McpBrowserCommand.Close)
        {
            this.Hide();
        }

        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Terminal.Gui hit-tests and delivers the event straight to the child under the pointer, so the
    /// <see cref="SelectableTextView"/> body gets its drag selections and right-click copies, and the
    /// table and editor widgets get their clicks. Anything no child claimed is left unhandled — the
    /// overlay covers the shell, so there is nothing behind it to protect.
    /// </remarks>
    protected override bool OnMouseEvent(Mouse mouse) => false;

    private void OnControllerChanged()
    {
        if (!this.active || this.disposed)
        {
            return;
        }

        try
        {
            this.app.Invoke(() =>
            {
                if (!this.active || this.disposed)
                {
                    return;
                }

                try
                {
                    this.Render();
                }
                catch
                {
                    // A late UI callback must never escape into the Terminal.Gui loop.
                }
            });
        }
        catch
        {
            // The application may be ending while a controller notification is in flight.
        }
    }

    private void Render()
    {
        var state = this.controller.State;
        string bodyText;
        switch (state.View)
        {
            case McpBrowserView.Detail:
                this.RenderDetail(state);
                bodyText = this.body.AllText;
                break;
            case McpBrowserView.Editor:
                this.RenderEditor(state);
                bodyText = this.editorForm.VisibleTextForTest;
                break;
            default:
                bodyText = this.RenderList(state);
                break;
        }

        this.VisibleTextForTest = string.Join(
            Environment.NewLine,
            this.header.Text ?? string.Empty,
            bodyText,
            this.status.Text ?? string.Empty,
            this.footer.Text ?? string.Empty);
        this.SetNeedsDraw();
    }

    private string RenderList(McpBrowserState state)
    {
        this.listTable.Visible = true;
        this.body.Visible = false;

        var source = new McpServerTableSource(state.Servers, this.statusGlyphs, null);
        this.listTable.Table = source;

        // Sync the table's selection from controller state, then scroll it into view. Without the
        // second step the table renders from row 0 regardless of the selection, so on a short
        // terminal the selected server is simply not on screen — which is exactly the case the
        // narrow-terminal tests cover.
        if (!state.Servers.IsDefaultOrEmpty && state.SelectedKey is { } key)
        {
            var idx = 0;
            for (var i = 0; i < state.Servers.Length; i++)
            {
                if (state.Servers[i].Key == key)
                {
                    idx = i;
                    break;
                }
            }

            this.listTable.SetSelection(0, idx, false);
            this.listTable.EnsureCursorIsVisible();
        }

        this.header.Text = SafeSingle("MCP manager");
        this.status.Text = SafeSingle(state.StatusMessage);
        this.footer.Text = SafeSingle(
            this.FooterForWidth(
                "↑/↓ move · PgUp/PgDn · Home/End · Enter detail · a add · e edit · Space enable · u reauth · Delete remove · Esc close",
                "↑/↓ · PgUp/PgDn · Home/End · Enter · Esc"));

        return BuildListText(state, source);
    }

    /// <summary>
    /// Builds a plain-text representation of the list for <see cref="VisibleTextForTest"/>. The
    /// text is not rendered to the screen; it is used only for test assertions that need to verify
    /// state without driver-scraping.
    /// </summary>
    private static string BuildListText(McpBrowserState state, McpServerTableSource source)
    {
        if (state.Servers.IsDefaultOrEmpty)
        {
            return "(no configured servers)";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < state.Servers.Length; i++)
        {
            var server = state.Servers[i];
            var itemState = McpServerTableSource.GetState(server);
            sb.Append(SafeSingle(server.Key.Name))
              .Append(' ')
              .Append(itemState.ToString().ToLowerInvariant())
              .Append(" connection=")
              .Append(server.Connection)
              .AppendLine();
        }

        return sb.ToString();
    }

    private void RenderDetail(McpBrowserState state)
    {
        this.listTable.Visible = false;
        this.body.Visible = true;
        var lines = new List<string>();
        var detail = state.Detail;
        if (detail is null)
        {
            lines.Add("(no server selected)");
        }
        else
        {
            var summary = detail.Summary;
            lines.Add("Name:       " + SafeSingle(summary.Key.Name));
            lines.Add("Scope:      " + Scope(summary.Key.Scope));
            lines.Add("Source:     " + SafeSingle(summary.SourceFile));
            lines.Add("Transport:  " + Transport(summary.Transport));
            lines.Add(
                $"State:      {summary.Connection} / {(summary.Enabled ? "enabled" : "disabled")} / " +
                $"{(summary.IsEffective ? "effective" : "overridden")}");
            if (!string.IsNullOrWhiteSpace(summary.LastError))
            {
                lines.Add("Error:      " + SafeSingle(summary.LastError));
            }

            lines.Add("Configuration:");
            if (summary.Transport == McpTransportKind.Stdio)
            {
                lines.Add("  Command:  " + SafeSingle(detail.Command));
                AppendValues(lines, "  Args", EffectiveArgs(detail.Args));
                AppendSecrets(lines, "  Environment", detail.Environment);
            }
            else
            {
                lines.Add("  URL:      " + SafeSingle(detail.Url));
                lines.Add("  Auth:     " + detail.AuthMode);
                lines.Add("  ClientId: " + SafeSingle(detail.ClientId));
                AppendValues(lines, "  Scopes", detail.Scopes);
                AppendSecrets(lines, "  Environment", detail.Environment);
                AppendSecrets(lines, "  Headers", detail.Headers);
                if (detail.BearerToken is { } bearer)
                {
                    lines.Add("  Bearer:   " + MaskedSecret(bearer.DisplayValue));
                }
            }

            lines.Add("Capabilities:");
            AppendCapabilities(lines, "Tools", detail.Tools);
            AppendCapabilities(lines, "Prompts", detail.Prompts);
            AppendCapabilities(lines, "Resources", detail.Resources);
        }

        this.header.Text = SafeSingle($"MCP detail — {state.SelectedKey?.Name ?? "none"}");
        this.body.SetText(Window(lines, ref this.detailOffset, this.BodyViewportRows()));
        this.status.Text = SafeSingle(state.StatusMessage);
        this.footer.Text = SafeSingle(
            this.FooterForWidth(
                "↑/↓ scroll · PgUp/PgDn · Home/End · e edit · Space enable · u reauth · Delete remove · Esc back",
                "↑/↓ · PgUp/PgDn · Home/End · Esc back"));
    }

    private void RenderEditor(McpBrowserState state)
    {
        this.listTable.Visible = false;
        this.body.Visible = false;
        this.editorForm.Visible = true;

        this.header.Text = SafeSingle($"MCP editor — {state.Editor?.Mode.ToString() ?? "unavailable"}");
        this.status.Text = SafeSingle(state.StatusMessage);
        this.footer.Text = SafeSingle(
            this.FooterForWidth(
                "Tab/↑/↓ field · Enter save · Ctrl+N add · Ctrl+R remove · Ctrl+↑/↓ item · Ctrl+←/→ part · Esc cancel",
                "Tab field · Enter save · Esc cancel"));

        if (state.Editor is { } editor)
        {
            this.editorForm.ApplyState(editor);
        }
    }

    private static void AppendValues(List<string> lines, string label, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            lines.Add(label + ": (none)");
            return;
        }

        lines.Add(label + ":");
        foreach (var value in values)
        {
            lines.Add("    " + SafeSingle(value));
        }
    }

    private static void AppendSecrets(
        List<string> lines,
        string label,
        IReadOnlyList<McpSecretDescriptor> values)
    {
        if (values.Count == 0)
        {
            lines.Add(label + ": (none)");
            return;
        }

        lines.Add(label + ":");
        foreach (var value in values)
        {
            lines.Add("    " + SafeSingle(value.Name) + ": " + MaskedSecret(value.DisplayValue));
        }
    }

    private static void AppendCapabilities(
        List<string> lines,
        string label,
        IReadOnlyList<McpCapabilitySummary> values)
    {
        if (values.Count == 0)
        {
            lines.Add(label + ": (none)");
            return;
        }

        lines.Add(label + ":");
        foreach (var value in values)
        {
            var line = new StringBuilder("    ").Append(SafeSingle(value.Name));
            if (!string.IsNullOrWhiteSpace(value.Description))
            {
                line.Append(" — ").Append(SafeSingle(value.Description));
            }

            lines.Add(line.ToString());
        }
    }

    private int BodyViewportRows()
    {
        var height = this.body.Viewport.Height;
        if (height <= 0)
        {
            height = this.body.Frame.Height;
        }

        if (height <= 0)
        {
            height = this.Frame.Height - 3;
        }

        return Math.Max(1, height);
    }

    private static string Window(
        IReadOnlyList<string> lines,
        ref int offset,
        int rows,
        int keepVisibleLine = -1)
    {
        if (lines.Count == 0)
        {
            offset = 0;
            return string.Empty;
        }

        var height = Math.Max(1, rows);
        var maxOffset = Math.Max(0, lines.Count - height);
        offset = Math.Clamp(offset, 0, maxOffset);
        if (keepVisibleLine >= 0)
        {
            if (keepVisibleLine < offset)
            {
                offset = keepVisibleLine;
            }
            else if (keepVisibleLine >= offset + height)
            {
                offset = keepVisibleLine - height + 1;
            }

            offset = Math.Clamp(offset, 0, maxOffset);
        }

        var count = Math.Min(height, lines.Count - offset);
        return string.Join(Environment.NewLine, lines.Skip(offset).Take(count));
    }

    private bool TryScrollDetail(Key key)
    {
        if (key == Key.CursorUp)
        {
            this.detailOffset = Math.Max(0, this.detailOffset - 1);
            return true;
        }

        if (key == Key.CursorDown)
        {
            this.detailOffset = (int)Math.Min(int.MaxValue, (long)this.detailOffset + 1);
            return true;
        }

        if (key == Key.PageUp)
        {
            this.detailOffset = Math.Max(0, this.detailOffset - this.BodyViewportRows());
            return true;
        }

        if (key == Key.PageDown)
        {
            this.detailOffset = (int)Math.Min(
                int.MaxValue,
                (long)this.detailOffset + this.BodyViewportRows());
            return true;
        }

        if (key == Key.Home)
        {
            this.detailOffset = 0;
            return true;
        }

        if (key == Key.End)
        {
            this.detailOffset = int.MaxValue;
            return true;
        }

        return false;
    }

    private string FooterForWidth(string full, string compact) =>
        this.Frame.Width > 40 ? full : compact;

    private static IReadOnlyList<string> EffectiveArgs(ImmutableArray<string> args) =>
        args.IsDefault ? [] : args.ToArray();

    private static string MaskedSecret(string? value) => "*****";

    private static string Scope(McpConfigScope scope) => scope == McpConfigScope.User ? "user" : "project";

    private static string Transport(McpTransportKind transport) => transport == McpTransportKind.Http ? "http" : "stdio";

    private static string SafeSingle(string? value) => TerminalTextSanitizer.SanitizeSingleLine(value ?? string.Empty);

    private BrowserSchemes EnsureSchemes() =>
        this.browserSchemes ??= new BrowserSchemes(this.theme, this.app.Driver);

    private Scheme GetRowScheme(RowColorGetterArgs args)
    {
        var schemes = this.EnsureSchemes();
        if (args.Table is not McpServerTableSource source || args.RowIndex < 0 || args.RowIndex >= source.Rows)
        {
            return schemes.Normal;
        }

        return schemes.ForRow(McpServerTableSource.GetState(source.SummaryAt(args.RowIndex)));
    }

    private Scheme GetStatusCellScheme(CellColorGetterArgs args)
    {
        var schemes = this.EnsureSchemes();
        if (args.Table is not McpServerTableSource source || args.RowIndex < 0 || args.RowIndex >= source.Rows)
        {
            return schemes.Normal;
        }

        return schemes.For(McpServerTableSource.GetState(source.SummaryAt(args.RowIndex)));
    }

    private Scheme GetTransportCellScheme(CellColorGetterArgs args) => this.EnsureSchemes().Accent;

    private void Observe(Task task) =>
        task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.disposed)
        {
            this.disposed = true;
            this.body.CancelMouseInteraction();
            this.Hide();
        }

        base.Dispose(disposing);
    }
}


