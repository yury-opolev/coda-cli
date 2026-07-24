using System.Collections.Immutable;
using System.Text;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Mcp;

/// <summary>
/// Full-screen Terminal.Gui view for the interactive MCP manager. The controller owns all state and
/// asynchronous work; this view only renders sanitized state and dispatches input.
/// </summary>
internal sealed class McpBrowserOverlay : View
{
    private readonly IApplication app;
    private readonly McpBrowserController controller;
    private readonly Label header;
    private readonly Label body;
    private readonly Label status;
    private readonly Label footer;

    private CancellationTokenSource? lifetime;
    private bool active;
    private bool subscribed;
    private bool disposed;
    private int listOffset;
    private int detailOffset;
    private int editorOffset;

    internal McpBrowserOverlay(IApplication app, McpBrowserController controller)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));

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
        this.body = new Label
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            CanFocus = false,
        };
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
        this.Add(this.header, this.body, this.status, this.footer);
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
    }

    internal string HeaderText => this.header.Text ?? string.Empty;

    internal string BodyText => this.body.Text ?? string.Empty;

    internal string StatusText => this.status.Text ?? string.Empty;

    internal string FooterText => this.footer.Text ?? string.Empty;

    /// <summary>Render-only test seam containing exactly the sanitized strings assigned to visible labels.</summary>
    internal string VisibleTextForTest { get; private set; } = string.Empty;

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
        try
        {
            this.SuperView?.SetFocus();
        }
        catch
        {
            // Focus restoration is best effort while the parent/application is shutting down.
        }
    }

    protected override bool OnKeyDown(Key key)
    {
        if (!this.Visible)
        {
            return false;
        }

        var command = McpBrowserKeyMap.Map(key, this.controller.State.View);
        if (command == McpBrowserCommand.None &&
            this.controller.State.View == McpBrowserView.Detail &&
            this.TryScrollDetail(key))
        {
            this.Render();
            return true;
        }

        var token = this.lifetime?.Token ?? CancellationToken.None;
        this.Observe(this.controller.ExecuteAsync(command, key, token));
        if (command == McpBrowserCommand.Close)
        {
            this.Hide();
        }

        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse) => this.Visible;

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
        switch (state.View)
        {
            case McpBrowserView.Detail:
                this.RenderDetail(state);
                break;
            case McpBrowserView.Editor:
                this.RenderEditor(state);
                break;
            default:
                this.RenderList(state);
                break;
        }

        this.VisibleTextForTest = string.Join(
            Environment.NewLine,
            this.header.Text ?? string.Empty,
            this.body.Text ?? string.Empty,
            this.status.Text ?? string.Empty,
            this.footer.Text ?? string.Empty);
        this.SetNeedsDraw();
    }

    private void RenderList(McpBrowserState state)
    {
        var lines = new List<string> { $"MCP servers ({state.Servers.Length})" };
        if (state.Servers.IsDefaultOrEmpty)
        {
            lines.Add("(no configured servers)");
        }
        else
        {
            for (var index = 0; index < state.Servers.Length; index++)
            {
                var server = state.Servers[index];
                var selected = state.SelectedKey == server.Key ? ">" : " ";
                var enabled = server.Enabled ? "enabled" : "disabled";
                var effective = server.IsEffective ? "effective" : "overridden";
                var error = string.IsNullOrWhiteSpace(server.LastError)
                    ? string.Empty
                    : $" error={SafeSingle(server.LastError)}";
                lines.Add(new StringBuilder()
                    .Append(selected).Append(' ')
                    .Append(SafeSingle(server.Key.Name)).Append(" [")
                    .Append(Scope(server.Key.Scope)).Append("] ")
                    .Append(Transport(server.Transport)).Append(' ')
                    .Append(enabled).Append(' ')
                    .Append(effective).Append(" connection=")
                    .Append(server.Connection).Append(error).ToString());
            }
        }

        this.header.Text = SafeSingle("MCP manager");
        var selectedLine = state.SelectedKey is not null
            ? lines.FindIndex(line => line.StartsWith("> ", StringComparison.Ordinal))
            : -1;
        this.body.Text = Window(lines, ref this.listOffset, this.BodyViewportRows(), selectedLine);
        this.status.Text = SafeSingle(state.StatusMessage);
        this.footer.Text = SafeSingle(
            this.FooterForWidth(
                "↑/↓ move · PgUp/PgDn · Home/End · Enter detail · a add · e edit · Space enable · u reauth · Delete remove · Esc close",
                "↑/↓ · PgUp/PgDn · Home/End · Enter · Esc"));
    }

    private void RenderDetail(McpBrowserState state)
    {
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
        this.body.Text = Window(lines, ref this.detailOffset, this.BodyViewportRows());
        this.status.Text = SafeSingle(state.StatusMessage);
        this.footer.Text = SafeSingle(
            this.FooterForWidth(
                "↑/↓ scroll · PgUp/PgDn · Home/End · e edit · Space enable · u reauth · Delete remove · Esc back",
                "↑/↓ · PgUp/PgDn · Home/End · Esc back"));
    }

    private void RenderEditor(McpBrowserState state)
    {
        var lines = new List<string>();
        var focusedLine = -1;
        if (state.Editor is not { } editor)
        {
            lines.Add("(editor unavailable)");
        }
        else
        {
            var draft = editor.Draft;
            focusedLine = AppendEditorField(lines, editor, McpEditorField.Scope, Scope(draft.Scope), focusedLine);
            focusedLine = AppendEditorField(lines, editor, McpEditorField.Name, draft.Name, focusedLine);
            focusedLine = AppendEditorField(lines, editor, McpEditorField.Transport, Transport(draft.Transport), focusedLine);
            focusedLine = AppendEditorField(lines, editor, McpEditorField.Command, draft.Command, focusedLine);
            focusedLine = AppendEditorCollection(lines, editor, McpEditorField.Arguments, DraftArgs(draft), focusedLine);
            focusedLine = AppendEditorField(lines, editor, McpEditorField.Url, draft.Url, focusedLine);
            focusedLine = AppendEditorNamedSecrets(lines, editor, McpEditorField.Environment, draft.Environment, focusedLine);
            focusedLine = AppendEditorNamedSecrets(lines, editor, McpEditorField.Headers, draft.Headers, focusedLine);
            focusedLine = AppendEditorField(lines, editor, McpEditorField.AuthMode, draft.AuthMode.ToString(), focusedLine);
            focusedLine = AppendEditorField(lines, editor, McpEditorField.ClientId, draft.ClientId, focusedLine);
            focusedLine = AppendEditorCollection(lines, editor, McpEditorField.Scopes, DraftScopes(draft), focusedLine);
            focusedLine = AppendEditorField(lines, editor, McpEditorField.BearerToken, DraftSecret(draft.BearerToken), focusedLine);
            focusedLine = AppendEditorField(lines, editor, McpEditorField.Save, "apply", focusedLine);
            focusedLine = AppendEditorField(lines, editor, McpEditorField.Cancel, "cancel", focusedLine);
        }

        lines.Add($"Busy: turn={(state.TurnBusy ? "yes" : "no")} action={(state.ActionBusy ? "yes" : "no")}");
        this.header.Text = SafeSingle($"MCP editor — {state.Editor?.Mode.ToString() ?? "unavailable"}");
        this.body.Text = Window(lines, ref this.editorOffset, this.BodyViewportRows(), focusedLine);
        this.status.Text = SafeSingle(state.StatusMessage);
        this.footer.Text = SafeSingle(
            this.FooterForWidth(
                "Tab/Shift+Tab field · Enter apply/next · Ctrl+N add · Ctrl+R remove · Ctrl+↑/↓ item · Ctrl+←/→ part · Esc cancel",
                "Enter Save · Esc Cancel"));
    }

    private static int AppendEditorField(
        List<string> lines,
        McpEditorState editor,
        McpEditorField field,
        string? value,
        int focusedLine)
    {
        var line = lines.Count;
        var marker = editor.FocusedField == field ? ">" : " ";
        lines.Add(marker + " " + field + ": " + SafeSingle(value));
        return focusedLine >= 0 || editor.FocusedField != field ? focusedLine : line;
    }

    private static int AppendEditorCollection(
        List<string> lines,
        McpEditorState editor,
        McpEditorField field,
        IReadOnlyList<string> values,
        int focusedLine)
    {
        focusedLine = AppendEditorField(
            lines,
            editor,
            field,
            values.Count == 0 ? "(none)" : $"{values.Count} item(s)",
            focusedLine);
        for (var index = 0; index < values.Count; index++)
        {
            var marker = editor.FocusedField == field && editor.SelectedItem == index ? ">" : " ";
            lines.Add($"  {marker} {index + 1}: {SafeSingle(values[index])}");
        }

        return focusedLine;
    }

    private static int AppendEditorNamedSecrets(
        List<string> lines,
        McpEditorState editor,
        McpEditorField field,
        IReadOnlyList<McpNamedSecretDraft> values,
        int focusedLine)
    {
        focusedLine = AppendEditorField(
            lines,
            editor,
            field,
            values.Count == 0 ? "(none)" : $"{values.Count} item(s)",
            focusedLine);
        for (var index = 0; index < values.Count; index++)
        {
            var item = values[index];
            var marker = editor.FocusedField == field && editor.SelectedItem == index ? ">" : " ";
            var part = editor.FocusedField == field && editor.SelectedItem == index
                ? editor.SelectedItemPart
                : McpEditorItemPart.Value;
            var value = part == McpEditorItemPart.Name
                ? SafeSingle(item.Name)
                : DraftSecret(item.Change);
            lines.Add($"  {marker} {index + 1}: {value} ({item.ExistingSource})");
        }

        return focusedLine;
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

    private static IReadOnlyList<string> DraftArgs(McpServerDraft draft) =>
        draft.ArgumentItems.IsDefault
            ? draft.Args
            : draft.ArgumentItems.Select(item => item.Value).ToArray();

    private static IReadOnlyList<string> DraftScopes(McpServerDraft draft) =>
        draft.ScopeItems.IsDefault
            ? draft.Scopes
            : draft.ScopeItems.Select(item => item.Value).ToArray();

    private static string DraftSecret(McpSecretChange change) =>
        change.Kind switch
        {
            McpSecretChangeKind.Replace => "*****",
            McpSecretChangeKind.Remove => "(removed)",
            _ => "(unchanged)",
        };

    private static string MaskedSecret(string? value) => "*****";

    private static string Scope(McpConfigScope scope) => scope == McpConfigScope.User ? "user" : "project";

    private static string Transport(McpTransportKind transport) => transport == McpTransportKind.Http ? "http" : "stdio";

    private static string SafeSingle(string? value) => TerminalTextSanitizer.SanitizeSingleLine(value ?? string.Empty);

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
            this.Hide();
        }

        base.Dispose(disposing);
    }
}
