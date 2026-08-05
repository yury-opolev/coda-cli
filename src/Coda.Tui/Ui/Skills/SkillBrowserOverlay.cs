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
    public SkillBrowserOverlay(
        IApplication app,
        SkillBrowserController controller,
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

        // The list table occupies the body area; the detail view reuses the SelectableTextView.
        // Both share the same geometry so toggling Visible between them switches panes.
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
        this.listTable.Style.ColumnStyles[2] = new ColumnStyle { MinWidth = 4, MaxWidth = 8 };
        this.listTable.Style.ColumnStyles[3] = new ColumnStyle { MinWidth = 0, MaxWidth = 40, TruncationIndicator = "…" };
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

        // Status row sits above the footer so a message never evicts the key hints.
        this.status = new Label { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1, CanFocus = false };
        this.footer = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, CanFocus = false };

        this.Add(this.header, this.listTable, this.body, this.status, this.footer);

        // On resize the viewport shrinks underneath a stale row offset, scroll selection back into view.
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
    internal SkillTableSource? ListTableSource { get; private set; }

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

        var command = SkillBrowserKeyMap.Map(key, this.controller.State.View);
        switch (command)
        {
            case SkillBrowserCommand.Close:
                this.Hide();
                return true;

            case SkillBrowserCommand.MoveUp:
                this.controller.MoveSelection(-1);
                this.Render();
                return true;

            case SkillBrowserCommand.MoveDown:
                this.controller.MoveSelection(1);
                this.Render();
                return true;

            case SkillBrowserCommand.PageUp:
                this.controller.MoveSelection(-PageStep);
                this.Render();
                return true;

            case SkillBrowserCommand.PageDown:
                this.controller.MoveSelection(PageStep);
                this.Render();
                return true;

            case SkillBrowserCommand.MoveToStart:
                this.controller.MoveToStart();
                this.Render();
                return true;

            case SkillBrowserCommand.MoveToEnd:
                this.controller.MoveToEnd();
                this.Render();
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
                // Skills are frontmatter-driven; there is no runtime toggle. Report this instead of
                // silently swallowing the key so users know why nothing happened.
                this.SetStatusMessage("skills are frontmatter-driven — edit the SKILL.md file to change behavior");
                return true;

            case SkillBrowserCommand.Filter:
                this.filterMode = true;
                this.filterBuffer = string.Empty;
                this.Render();
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
        var schemes = this.EnsureSchemes();
        var skills = ApplyFilter(state.Skills, this.filterBuffer);
        var count = state.Skills.Count;
        var title = count == 1 ? "1 skill" : $"{count} skills";
        this.header.Text = $" Skills — {title}";

        // Build / update the table source.
        var source = new SkillTableSource(skills, this.statusGlyphs);
        this.ListTableSource = source;
        this.listTable.Table = source;

        // Sync table selection to controller state.
        if (skills.Count > 0)
        {
            var selIdx = skills.ToList().FindIndex(s => s.Name == state.SelectedName);
            if (selIdx >= 0)
            {
                this.listTable.SetSelection(0, selIdx, false);
                this.listTable.EnsureCursorIsVisible();
            }
        }

        this.listTable.Visible = true;
        this.body.Visible = false;

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

        this.footer.Text = " ↑/↓ k/j move · Enter detail · r reload · Space (frontmatter) · / filter · Esc q close";
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

        this.body.Visible = true;
        this.listTable.Visible = false;

        this.status.Text = string.Empty;
        this.footer.Text = " Esc q back · ↑/↓ k/j · r reload";
    }

    // ── Colour getters for the table ──────────────────────────────────────────

    private Scheme? GetRowScheme(RowColorGetterArgs args)
    {
        if (this.ListTableSource is null || args.RowIndex >= this.ListTableSource.Rows)
        {
            return null;
        }

        var skill = this.ListTableSource.SkillAt(args.RowIndex);
        return this.EnsureSchemes().ForRow(SkillTableSource.GetState(skill));
    }

    private Scheme? GetStatusCellScheme(CellColorGetterArgs args)
    {
        if (this.ListTableSource is null || args.RowIndex >= this.ListTableSource.Rows)
        {
            return null;
        }

        var skill = this.ListTableSource.SkillAt(args.RowIndex);
        return this.EnsureSchemes().For(SkillTableSource.GetState(skill));
    }

    private BrowserSchemes EnsureSchemes() =>
        this.browserSchemes ??= new BrowserSchemes(this.theme, this.app.Driver);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetStatusMessage(string msg)
    {
        this.status.Text = $" {TerminalTextSanitizer.SanitizeSingleLine(msg)}";
        this.SetNeedsDraw();
    }

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

    private static IReadOnlyList<SkillDefinition> ApplyFilter(IReadOnlyList<SkillDefinition> skills, string filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return skills;
        }

        return skills.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
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
