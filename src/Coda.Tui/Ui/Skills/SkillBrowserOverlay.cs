using Coda.Tui.Skills;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;

namespace Coda.Tui.Ui.Skills;

/// <summary>
/// The <c>/skills</c> browser overlay: a hidden-by-default, focused full-screen Terminal.Gui view
/// that renders <see cref="SkillBrowserController"/> state (skill list or a single-skill detail
/// pane) and routes keys through <see cref="SkillBrowserKeyMap"/>.
///
/// <para><b>Threading.</b> <see cref="SkillBrowserController.Changed"/> may fire on a background
/// pump thread. <see cref="OnControllerChanged"/> marshals every view mutation through
/// <see cref="IApplication.Invoke"/> so no Terminal.Gui control is ever touched off the UI thread.</para>
///
/// <para><b>Lifecycle.</b> <see cref="Show"/> and <see cref="Hide"/> are idempotent. <see cref="Dispose"/>
/// mirrors <see cref="Hide"/>'s teardown.</para>
/// </summary>
internal sealed class SkillBrowserOverlay : View, ISelectableOverlay
{
    private const int PageStep = 10;

    private readonly IApplication app;
    private readonly SkillBrowserController controller;
    private TuiTheme theme;
    private readonly Action? onChanged;

    private readonly Label header;
    private readonly SelectableTextView body;
    private readonly Label footer;

    private CancellationTokenSource? pumpCts;
    private bool active;
    private bool disposed;

    /// <summary>Creates the overlay bound to <paramref name="controller"/>.</summary>
    public SkillBrowserOverlay(
        IApplication app,
        SkillBrowserController controller,
        TuiTheme? theme = null,
        Action? onChanged = null,
        Action<string, Action>? onCopyRequested = null)
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
        this.body = new SelectableTextView(app) { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1) };
        this.footer = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };
        if (onCopyRequested is not null)
        {
            this.body.CopyRequested += text => onCopyRequested(text, this.body.ClearSelection);
        }

        this.Add(this.header);
        this.Add(this.body);
        this.Add(this.footer);
    }

    /// <summary>Re-applies the surface theme and re-renders (if active).</summary>
    internal void ApplyTheme(TuiTheme theme)
    {
        this.theme = theme ?? throw new ArgumentNullException(nameof(theme));
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

    internal string BodyText => this.body.AllText;

    internal string FooterText => this.footer.Text ?? string.Empty;

    // ── ISelectableOverlay ────────────────────────────────────────────────────
    SelectableTextView ISelectableOverlay.Body => this.body;

    // ── Show / Hide / Teardown ────────────────────────────────────────────────

    /// <summary>Opens the controller, subscribes to changes, starts a fresh pump, focuses, and renders.</summary>
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

    /// <summary>Cancels the pump, unsubscribes, closes the controller, and hides.</summary>
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

        var command = SkillBrowserKeyMap.Map(key, this.controller.State.View);
        switch (command)
        {
            case SkillBrowserCommand.Close:
                this.Hide();
                return true;

            case SkillBrowserCommand.MoveUp:
                this.controller.MoveSelection(-1);
                return true;

            case SkillBrowserCommand.MoveDown:
                this.controller.MoveSelection(1);
                return true;

            case SkillBrowserCommand.PageUp:
                this.controller.MoveSelection(-PageStep);
                return true;

            case SkillBrowserCommand.PageDown:
                this.controller.MoveSelection(PageStep);
                return true;

            case SkillBrowserCommand.MoveToStart:
                this.controller.MoveToStart();
                return true;

            case SkillBrowserCommand.MoveToEnd:
                this.controller.MoveToEnd();
                return true;

            case SkillBrowserCommand.OpenDetail:
                this.controller.OpenDetail();
                return true;

            case SkillBrowserCommand.ReturnToList:
                this.controller.ReturnToList();
                return true;

            case SkillBrowserCommand.Reload:
                this.controller.Reload();
                return true;

            case SkillBrowserCommand.ToggleEnabled:
                // Skills are frontmatter-driven; there is no runtime toggle. No-op.
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
        if (state.View == SkillBrowserView.Detail && state.Detail is not null)
        {
            this.RenderDetail(state.Detail);
        }
        else
        {
            this.RenderList(state);
        }

        this.SetNeedsDraw();
    }

    private void RenderList(SkillBrowserState state)
    {
        var count = state.Skills.Count;
        var title = count == 1 ? "1 skill" : $"{count} skills";
        this.header.Text = $" Skills — {title}";

        if (count == 0)
        {
            this.body.SetText("  (no skills discovered)");
        }
        else
        {
            var lines = new System.Text.StringBuilder();
            foreach (var skill in state.Skills)
            {
                var selected = skill.Name == state.SelectedName;
                var prefix = selected ? "▶ " : "  ";
                var name = TerminalTextSanitizer.SanitizeSingleLine(skill.Name);
                var origin = skill.Origin.ToString().ToLowerInvariant();
                var enabled = skill.DisableModelInvocation ? "model-off" : "enabled";
                var desc = TerminalTextSanitizer.SanitizeSingleLine(skill.Description);
                lines.AppendLine($"{prefix}{name}  [{origin}] [{enabled}]  {desc}");
            }

            this.body.SetText(lines.ToString());
        }

        this.footer.Text = state.StatusMessage is { Length: > 0 } msg
            ? $" {TerminalTextSanitizer.SanitizeSingleLine(msg)}"
            : " ↑/↓ navigate · Enter detail · r reload · Esc close";
    }

    private void RenderDetail(SkillDefinition skill)
    {
        this.header.Text = $" Skill — {TerminalTextSanitizer.SanitizeSingleLine(skill.Name)}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  name           {TerminalTextSanitizer.SanitizeSingleLine(skill.Name)}");
        sb.AppendLine($"  description    {TerminalTextSanitizer.SanitizeSingleLine(skill.Description)}");
        sb.AppendLine($"  origin         {skill.Origin.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  user-invocable {(skill.UserInvocable ? "yes" : "no")}");
        sb.AppendLine($"  model-invoke   {(skill.DisableModelInvocation ? "disabled" : "enabled")}");
        if (skill.ArgumentHint is { Length: > 0 } hint)
        {
            sb.AppendLine($"  argument-hint  {TerminalTextSanitizer.SanitizeSingleLine(hint)}");
        }

        if (skill.WhenToUse is { Length: > 0 } wtu)
        {
            sb.AppendLine($"  when-to-use    {TerminalTextSanitizer.SanitizeSingleLine(wtu)}");
        }

        if (skill.Model is { Length: > 0 } model)
        {
            sb.AppendLine($"  model          {TerminalTextSanitizer.SanitizeSingleLine(model)}");
        }

        if (skill.SourcePath is { Length: > 0 } src)
        {
            sb.AppendLine($"  source         {TerminalTextSanitizer.SanitizeSingleLine(src)}");
        }

        this.body.SetText(sb.ToString());
        this.footer.Text = " Esc back · r reload";
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
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
