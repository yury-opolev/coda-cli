using Coda.Tui.Plugins;
using Coda.Tui.Ui.Rendering;

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
internal sealed class PluginBrowserOverlay : View
{
    private const int PageStep = 10;

    private readonly IApplication app;
    private readonly PluginBrowserController controller;
    private TuiTheme theme;
    private readonly Action? onChanged;

    private readonly Label header;
    private readonly Label body;
    private readonly Label footer;

    private CancellationTokenSource? pumpCts;
    private bool active;
    private bool disposed;

    /// <summary>Creates the overlay bound to <paramref name="controller"/>.</summary>
    public PluginBrowserOverlay(
        IApplication app,
        PluginBrowserController controller,
        TuiTheme? theme = null,
        Action? onChanged = null)
    {
        this.app = app ?? throw new ArgumentNullException(nameof(app));
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.theme = theme ?? CodaThemes.Current.Tui;
        this.onChanged = onChanged;

        this.Visible = false;
        this.CanFocus = true;
        this.Width = Dim.Fill();
        this.Height = Dim.Fill();
        this.BorderStyle = LineStyle.Rounded;

        this.header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, CanFocus = false };
        this.body = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1), CanFocus = false };
        this.footer = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };
        this.Add(this.header);
        this.Add(this.body);
        this.Add(this.footer);
    }

    /// <summary>Re-applies the surface theme and re-renders (if active).</summary>
    internal void ApplyTheme(TuiTheme theme)
    {
        this.theme = theme ?? throw new ArgumentNullException(nameof(theme));
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

    /// <summary>True while the background change pump is running.</summary>
    internal bool IsPumping => this.pumpCts is not null;

    internal string HeaderText => this.header.Text ?? string.Empty;

    internal string BodyText => this.body.Text ?? string.Empty;

    internal string FooterText => this.footer.Text ?? string.Empty;

    // ── Show / Hide / Teardown ────────────────────────────────────────────────

    /// <summary>Opens the controller, subscribes to changes, starts a fresh pump, focuses, and renders.</summary>
    public void Show()
    {
        this.SetScheme(this.theme.SurfaceScheme(this.app.Driver));

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

    /// <summary>Cancels the pump, unsubscribes, closes the controller, and hides.</summary>
    public void Hide()
    {
        if (!this.active)
        {
            return;
        }

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

        var command = PluginBrowserKeyMap.Map(key, this.controller.State.View);
        switch (command)
        {
            case PluginBrowserCommand.Close:
                this.Hide();
                return true;

            case PluginBrowserCommand.MoveUp:
                this.controller.MoveSelection(-1);
                return true;

            case PluginBrowserCommand.MoveDown:
                this.controller.MoveSelection(1);
                return true;

            case PluginBrowserCommand.PageUp:
                this.controller.MoveSelection(-PageStep);
                return true;

            case PluginBrowserCommand.PageDown:
                this.controller.MoveSelection(PageStep);
                return true;

            case PluginBrowserCommand.MoveToStart:
                this.controller.MoveToStart();
                return true;

            case PluginBrowserCommand.MoveToEnd:
                this.controller.MoveToEnd();
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

            case PluginBrowserCommand.Update:
                this.Observe(this.controller.UpdateSelectedAsync(CancellationToken.None));
                return true;
        }

        return base.OnKeyDown(key);
    }

    // ── Render ────────────────────────────────────────────────────────────────

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
        var count = state.Plugins.Count;
        var title = count == 1 ? "1 plugin" : $"{count} plugins";
        this.header.Text = $" Plugins — {title}";

        if (count == 0)
        {
            this.body.Text = "  (no plugins installed)";
        }
        else
        {
            var lines = new System.Text.StringBuilder();
            foreach (var plugin in state.Plugins)
            {
                var selected = plugin.Name == state.SelectedName;
                var prefix = selected ? "▶ " : "  ";
                var name = TerminalTextSanitizer.SanitizeSingleLine(plugin.Name);
                var version = TerminalTextSanitizer.SanitizeSingleLine(plugin.Version);
                var enabled = plugin.IsEnabled ? "enabled" : "disabled";
                var trusted = this.controller.IsTrusted(plugin) ? "trusted" : "untrusted";
                var external = plugin.IsExternal ? " [external]" : string.Empty;
                lines.AppendLine($"{prefix}{name} v{version}  [{enabled}] [{trusted}]{external}");
            }

            this.body.Text = lines.ToString();
        }

        this.footer.Text = state.StatusMessage is { Length: > 0 } msg
            ? $" {TerminalTextSanitizer.SanitizeSingleLine(msg)}"
            : " ↑/↓ navigate · Enter detail · Space toggle · u update · Esc close";
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

        this.body.Text = sb.ToString();
        this.footer.Text = state.StatusMessage is { Length: > 0 } msg
            ? $" {TerminalTextSanitizer.SanitizeSingleLine(msg)}"
            : " Esc back · Space toggle · u update";
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
