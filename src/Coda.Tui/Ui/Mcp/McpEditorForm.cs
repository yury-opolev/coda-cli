using System.Text;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Ui.Mcp;

/// <summary>
/// Widget-based editor pane for the MCP browser. Replaces the hand-rolled text rendering in
/// <see cref="McpBrowserOverlay.RenderEditor"/> with real Terminal.Gui input controls whose
/// focus traversal, caret editing, and value change events are provided by the toolkit.
/// </summary>
/// <remarks>
/// <para>
/// All widgets are created once in the constructor and added to the view tree immediately. Only
/// <see cref="ApplyState"/> makes any of them visible or invisible. The view itself stays hidden
/// until the overlay decides the editor should be shown.
/// </para>
/// <para>
/// Secrets (BearerToken, env values, header values) are never bound to a <see cref="TextField"/>.
/// They are shown as static labels with <c>"*****"</c>, <c>"(removed)"</c>, or
/// <c>"(unchanged)"</c> and their replacement is handled by the existing modal-prompt path.
/// </para>
/// </remarks>
internal sealed class McpEditorForm : View
{
    private const int LabelWidth = 12;

    private readonly McpBrowserController controller;

    /// <summary>
    /// Guard against re-entrant draft updates: when <c>true</c> the <c>ValueChanged</c> handlers
    /// are suspended while we push controller state back into the widgets.
    /// </summary>
    private bool suppressSync;

    // ── widgets (created once; visibility set by ApplyState) ─────────────────

    internal readonly OptionSelector ScopeSelector;
    internal readonly TextField NameField;
    internal readonly OptionSelector TransportSelector;
    internal readonly TextField CommandField;
    internal readonly Label ArgumentsSummaryLabel;
    internal readonly TextField UrlField;
    internal readonly Label HeadersSummaryLabel;
    internal readonly OptionSelector AuthModeSelector;
    internal readonly TextField ClientIdField;
    internal readonly Label ScopesSummaryLabel;
    internal readonly Label EnvironmentSummaryLabel;
    internal readonly Label BearerTokenLabel;
    internal readonly Button SaveButton;
    internal readonly Button CancelButton;

    /// <summary>Fired when the Save button is activated. The overlay wires this to the save flow.</summary>
    internal event Action? SaveRequested;

    /// <summary>Fired when the Cancel button is activated. The overlay wires this to the cancel flow.</summary>
    internal event Action? CancelRequested;

    /// <summary>
    /// Current scroll offset (in field rows). Persists across renders so focus-tracking scrolls
    /// smoothly as the user tabs through fields. Clamped and updated on every <see cref="ApplyState"/> call.
    /// </summary>
    private int scrollOffset;


    internal McpEditorForm(McpBrowserController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));

        this.CanFocus = true;
        this.Width = Dim.Fill();
        this.Height = Dim.Fill();

        // ── selectors ────────────────────────────────────────────────────────
        this.ScopeSelector = new OptionSelector
        {
            Width = Dim.Fill(),
            Height = 1,
            Labels = ["project", "user"],
            Orientation = Orientation.Horizontal,
            TabStop = TabBehavior.TabStop,
            Visible = false,
        };

        this.TransportSelector = new OptionSelector
        {
            Width = Dim.Fill(),
            Height = 1,
            Labels = ["stdio", "http"],
            Orientation = Orientation.Horizontal,
            TabStop = TabBehavior.TabStop,
            Visible = false,
        };

        this.AuthModeSelector = new OptionSelector
        {
            Width = Dim.Fill(),
            Height = 1,
            Labels = ["none", "bearer", "oauth"],
            Orientation = Orientation.Horizontal,
            TabStop = TabBehavior.TabStop,
            Visible = false,
        };

        // ── text fields ───────────────────────────────────────────────────────
        this.NameField = new TextField
        {
            Id = "Name",
            Width = Dim.Fill(),
            Height = 1,
            TabStop = TabBehavior.TabStop,
            Used = true,
            Visible = false,
        };

        this.CommandField = new TextField
        {
            Id = "Command",
            Width = Dim.Fill(),
            Height = 1,
            TabStop = TabBehavior.TabStop,
            Used = true,
            Visible = false,
        };

        this.UrlField = new TextField
        {
            Id = "Url",
            Width = Dim.Fill(),
            Height = 1,
            TabStop = TabBehavior.TabStop,
            Used = true,
            Visible = false,
        };

        this.ClientIdField = new TextField
        {
            Id = "ClientId",
            Width = Dim.Fill(),
            Height = 1,
            TabStop = TabBehavior.TabStop,
            Used = true,
            Visible = false,
        };

        // ── summary labels (full list editing is Task 8) ──────────────────────
        this.ArgumentsSummaryLabel = new Label { Width = Dim.Fill(), Height = 1, Visible = false };
        this.HeadersSummaryLabel = new Label { Width = Dim.Fill(), Height = 1, Visible = false };
        this.ScopesSummaryLabel = new Label { Width = Dim.Fill(), Height = 1, Visible = false };
        this.EnvironmentSummaryLabel = new Label { Width = Dim.Fill(), Height = 1, Visible = false };

        // BearerToken is always a read-only label — never bound to a TextField.
        this.BearerTokenLabel = new Label { Width = Dim.Fill(), Height = 1, Visible = false };

        // ── buttons ───────────────────────────────────────────────────────────
        this.SaveButton = new Button
        {
            Text = "Save",
            TabStop = TabBehavior.TabStop,
            Visible = false,
        };

        this.CancelButton = new Button
        {
            Text = "Cancel",
            TabStop = TabBehavior.TabStop,
            Visible = false,
        };

        // All widgets go into the view tree now, before any value is assigned.
        // Assigning Text/Value after Add avoids the "first keystroke replaces a character" trap
        // documented in WidgetIntegrationSpikeTests.TextField_inserts_mid_string_at_the_caret.
        this.Add(
            this.ScopeSelector,
            this.NameField,
            this.TransportSelector,
            this.CommandField,
            this.ArgumentsSummaryLabel,
            this.UrlField,
            this.HeadersSummaryLabel,
            this.AuthModeSelector,
            this.ClientIdField,
            this.ScopesSummaryLabel,
            this.EnvironmentSummaryLabel,
            this.BearerTokenLabel,
            this.SaveButton,
            this.CancelButton);

        this.WireValueChanged();
    }

    /// <summary>
    /// A plain-text snapshot of all visible labels and field values for test assertions.
    /// Never contains ANSI or markup.
    /// </summary>
    internal string VisibleTextForTest
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var view in this.SubViews)
            {
                if (!view.Visible) continue;
                var text = view switch
                {
                    TextField tf => TerminalTextSanitizer.SanitizeSingleLine(tf.Text ?? string.Empty),
                    Label lbl => TerminalTextSanitizer.SanitizeSingleLine(lbl.Text ?? string.Empty),
                    Button btn => TerminalTextSanitizer.SanitizeSingleLine(btn.Text ?? string.Empty),
                    _ => string.Empty,
                };
                if (!string.IsNullOrEmpty(text)) sb.AppendLine(text);
            }

            return sb.ToString();
        }
    }

    // ── public surface ────────────────────────────────────────────────────────

    /// <summary>
    /// Applies controller state to the form: positions visible widgets, sets their values from
    /// the draft, and applies any mode-specific constraints (e.g. read-only Scope in Edit mode).
    /// Must be called from the UI thread.
    /// </summary>
    /// <remarks>
    /// Two-pass layout prevents the <see cref="InvalidOperationException"/> that Terminal.Gui
    /// raises when you hide the focused view and no visible focusable sibling remains. By showing
    /// the views that belong in the visible window first, the focused view is already visible
    /// before any previously-visible-but-now-out-of-window views are hidden.
    /// </remarks>
    internal void ApplyState(McpEditorState editor)
    {
        var fields = McpEditorFieldSet.For(editor.Draft);

        // Walk up the view tree to find a reliable non-zero height. Individual view frames can
        // transiently read as 1 (or stale) during re-layout triggered by Visible/Y mutations on
        // children, so we walk up until we find a height >= the field set length or reach the screen.
        var height = this.Frame.Height;
        for (var sv = this.SuperView; height <= 1 && sv is not null; sv = sv.SuperView)
        {
            // Subtract rows consumed by the overlay header, status and footer chrome (typically 2).
            height = sv.Frame.Height - 2;
        }

        height = Math.Max(1, height);

        // Compute which field is focused so we can keep it within the scroll window.
        var focusedIndex = 0;
        for (var fi = 0; fi < fields.Count; fi++)
        {
            if (fields[fi] == editor.FocusedField) { focusedIndex = fi; break; }
        }

        // Compute scroll offset: always derive it from the focused field index and height so
        // that back-to-back renders with different heights (from FrameChanged callbacks during
        // layout passes) produce consistent results. This avoids the accumulated-state trap where
        // a render with a stale height sets scrollOffset to a value that hides the focused field
        // in subsequent renders.
        var maxOffset = Math.Max(0, fields.Count - height);
        this.scrollOffset = focusedIndex >= height
            ? Math.Clamp(focusedIndex - height + 1, 0, maxOffset)
            : 0;

        // Build a lookup of which subviews should be visible after layout.
        var visibleViews = new HashSet<View>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < fields.Count; i++)
        {
            if (i < this.scrollOffset || i >= this.scrollOffset + height) continue;
            var v = this.ViewForField(fields[i]);
            if (v is not null) visibleViews.Add(v);
        }

        // Suspend ValueChanged handlers while we push state into widgets.
        this.suppressSync = true;
        try
        {
            // Pass 1: position and show every view that belongs in the viewport. This ensures the
            // focused view is visible before we hide anything, so Terminal.Gui never loses its
            // focus target.
            this.LayoutFields(fields, editor, this.scrollOffset, height);

            // Pass 2: hide every subview that is NOT in the visible set.
            foreach (var view in this.SubViews)
            {
                if (!visibleViews.Contains(view))
                {
                    view.Visible = false;
                }
            }
        }
        finally
        {
            this.suppressSync = false;
        }
    }
    // Returns the viewport height used by ApplyState; exposed for unit tests.
    internal int ViewportHeightForTest()
    {
        var height = this.Viewport.Height;
        if (height <= 0) height = this.Frame.Height;
        if (height <= 0) height = this.SuperView?.Frame.Height ?? 0;
        return Math.Max(1, height);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Tab/Shift+Tab and Up/Down move focus between fields by calling
    /// <see cref="View.AdvanceFocus"/>. The terminal harness does not handle a bare Tab on the
    /// parent automatically (verified in WidgetIntegrationSpikeTests), so we intercept it here.
    /// </remarks>
    protected override bool OnKeyDown(Key key)
    {
        if (key == Key.Tab)
        {
            this.AdvanceFocus(NavigationDirection.Forward, TabBehavior.TabStop);
            return true;
        }

        if (key == Key.Tab.WithShift)
        {
            this.AdvanceFocus(NavigationDirection.Backward, TabBehavior.TabStop);
            return true;
        }

        if (key == Key.CursorDown)
        {
            this.AdvanceFocus(NavigationDirection.Forward, TabBehavior.TabStop);
            return true;
        }

        if (key == Key.CursorUp)
        {
            this.AdvanceFocus(NavigationDirection.Backward, TabBehavior.TabStop);
            return true;
        }

        return base.OnKeyDown(key);
    }

    // ── private helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the subview that corresponds to <paramref name="field"/>, or <c>null</c> if the
    /// field has no dedicated subview.
    /// </summary>
    private View? ViewForField(McpEditorField field) => field switch
    {
        McpEditorField.Scope => this.ScopeSelector,
        McpEditorField.Name => this.NameField,
        McpEditorField.Transport => this.TransportSelector,
        McpEditorField.Command => this.CommandField,
        McpEditorField.Arguments => this.ArgumentsSummaryLabel,
        McpEditorField.Url => this.UrlField,
        McpEditorField.Headers => this.HeadersSummaryLabel,
        McpEditorField.AuthMode => this.AuthModeSelector,
        McpEditorField.ClientId => this.ClientIdField,
        McpEditorField.Scopes => this.ScopesSummaryLabel,
        McpEditorField.Environment => this.EnvironmentSummaryLabel,
        McpEditorField.BearerToken => this.BearerTokenLabel,
        McpEditorField.Save => this.SaveButton,
        McpEditorField.Cancel => this.CancelButton,
        _ => null,
    };

    /// <summary>
    /// Pass 1: position and make visible every field whose list index falls within the scroll
    /// window [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="height"/>).
    /// Pass 2 (hiding out-of-window views) is the caller's responsibility.
    /// </summary>
    private void LayoutFields(
        IReadOnlyList<McpEditorField> fields,
        McpEditorState editor,
        int offset,
        int height)
    {
        var draft = editor.Draft;

        for (var i = 0; i < fields.Count; i++)
        {
            if (i < offset || i >= offset + height) continue;

            var row = i - offset;
            var field = fields[i];

            switch (field)
            {
                case McpEditorField.Scope:
                    this.ScopeSelector.Y = row;
                    this.ScopeSelector.Value = draft.Scope == McpConfigScope.User ? 1 : 0;
                    // Scope is read-only in Edit mode (existing rule: cannot change scope of an
                    // existing server entry).
                    this.ScopeSelector.Enabled = editor.Mode == McpEditorMode.Add;
                    this.ScopeSelector.Visible = true;
                    break;

                case McpEditorField.Name:
                    this.NameField.Y = row;
                    this.NameField.Value = draft.Name ?? string.Empty;
                    this.NameField.InsertionPoint = this.NameField.Text?.Length ?? 0;
                    this.NameField.Visible = true;
                    break;

                case McpEditorField.Transport:
                    this.TransportSelector.Y = row;
                    this.TransportSelector.Value = draft.Transport == McpTransportKind.Http ? 1 : 0;
                    this.TransportSelector.Visible = true;
                    break;

                case McpEditorField.Command:
                    this.CommandField.Y = row;
                    this.CommandField.Value = draft.Command ?? string.Empty;
                    this.CommandField.InsertionPoint = this.CommandField.Text?.Length ?? 0;
                    this.CommandField.Visible = true;
                    break;

                case McpEditorField.Arguments:
                    this.ArgumentsSummaryLabel.Y = row;
                    this.ArgumentsSummaryLabel.Text = FormatCount("Args", draft.Args.IsDefault ? 0 : draft.Args.Length);
                    this.ArgumentsSummaryLabel.Visible = true;
                    break;

                case McpEditorField.Url:
                    this.UrlField.Y = row;
                    this.UrlField.Value = draft.Url ?? string.Empty;
                    this.UrlField.InsertionPoint = this.UrlField.Text?.Length ?? 0;
                    this.UrlField.Visible = true;
                    break;

                case McpEditorField.Headers:
                    this.HeadersSummaryLabel.Y = row;
                    this.HeadersSummaryLabel.Text = FormatCount("Headers", draft.Headers.Length);
                    this.HeadersSummaryLabel.Visible = true;
                    break;

                case McpEditorField.AuthMode:
                    this.AuthModeSelector.Y = row;
                    this.AuthModeSelector.Value = draft.AuthMode switch
                    {
                        McpAuthMode.Bearer => 1,
                        McpAuthMode.OAuth => 2,
                        _ => 0,
                    };
                    this.AuthModeSelector.Visible = true;
                    break;

                case McpEditorField.ClientId:
                    this.ClientIdField.Y = row;
                    this.ClientIdField.Value = draft.ClientId ?? string.Empty;
                    this.ClientIdField.InsertionPoint = this.ClientIdField.Text?.Length ?? 0;
                    this.ClientIdField.Visible = true;
                    break;

                case McpEditorField.Scopes:
                    this.ScopesSummaryLabel.Y = row;
                    this.ScopesSummaryLabel.Text = FormatCount("Scopes", draft.Scopes.Length);
                    this.ScopesSummaryLabel.Visible = true;
                    break;

                case McpEditorField.Environment:
                    this.EnvironmentSummaryLabel.Y = row;
                    this.EnvironmentSummaryLabel.Text = FormatCount("Env", draft.Environment.Length);
                    this.EnvironmentSummaryLabel.Visible = true;
                    break;

                case McpEditorField.BearerToken:
                    this.BearerTokenLabel.Y = row;
                    this.BearerTokenLabel.Text = FormatSecret(draft.BearerToken);
                    this.BearerTokenLabel.Visible = true;
                    break;

                case McpEditorField.Save:
                    this.SaveButton.Y = row;
                    this.SaveButton.Visible = true;
                    break;

                case McpEditorField.Cancel:
                    this.CancelButton.Y = row;
                    this.CancelButton.Visible = true;
                    break;
            }
        }
    }
    private void WireValueChanged()
    {
        this.NameField.ValueChanged += (_, _) =>
        {
            if (this.suppressSync) return;
            var v = this.NameField.Text ?? string.Empty;
            this.controller.UpdateEditorDraft(d => d with { Name = v });
        };

        this.CommandField.ValueChanged += (_, _) =>
        {
            if (this.suppressSync) return;
            var v = this.CommandField.Text ?? string.Empty;
            this.controller.UpdateEditorDraft(d => d with { Command = v });
        };

        this.UrlField.ValueChanged += (_, _) =>
        {
            if (this.suppressSync) return;
            var v = this.UrlField.Text ?? string.Empty;
            this.controller.UpdateEditorDraft(d => d with { Url = v, UrlChanged = true });
        };

        this.ClientIdField.ValueChanged += (_, _) =>
        {
            if (this.suppressSync) return;
            var v = this.ClientIdField.Text ?? string.Empty;
            this.controller.UpdateEditorDraft(d => d with { ClientId = v });
        };

        this.ScopeSelector.ValueChanged += (_, _) =>
        {
            if (this.suppressSync) return;
            if (!this.ScopeSelector.Enabled) return; // read-only in Edit mode
            var scope = this.ScopeSelector.Value == 1 ? McpConfigScope.User : McpConfigScope.Project;
            this.controller.UpdateEditorDraft(d => d with { Scope = scope });
        };

        this.TransportSelector.ValueChanged += (_, _) =>
        {
            if (this.suppressSync) return;
            var transport = this.TransportSelector.Value == 1
                ? McpTransportKind.Http
                : McpTransportKind.Stdio;
            this.controller.UpdateEditorDraft(d => d with { Transport = transport });
        };

        this.AuthModeSelector.ValueChanged += (_, _) =>
        {
            if (this.suppressSync) return;
            var authMode = this.AuthModeSelector.Value switch
            {
                1 => McpAuthMode.Bearer,
                2 => McpAuthMode.OAuth,
                _ => McpAuthMode.None,
            };
            this.controller.UpdateEditorDraft(d => d with { AuthMode = authMode });
        };

        this.SaveButton.Accepted += (_, _) => this.SaveRequested?.Invoke();
        this.CancelButton.Accepted += (_, _) => this.CancelRequested?.Invoke();

        // Sync FocusedField in controller state when a widget gains focus, so that
        // ApplyEditorAsync (called on Enter at the overlay level) checks the right field.
        // suppressSync prevents focus-change events fired by Terminal.Gui internally while we
        // are repositioning and hiding views from triggering a spurious re-render that would
        // overwrite the correct scroll offset we just computed.
        this.NameField.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.Name); };
        this.CommandField.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.Command); };
        this.UrlField.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.Url); };
        this.ClientIdField.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.ClientId); };
        this.ScopeSelector.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.Scope); };
        this.TransportSelector.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.Transport); };
        this.AuthModeSelector.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.AuthMode); };
        this.BearerTokenLabel.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.BearerToken); };
        this.SaveButton.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.Save); };
        this.CancelButton.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.Cancel); };
    }
    private static string FormatCount(string label, int count) =>
        count == 0 ? $"{label}: (none)" : $"{label}: {count} item(s)";

    private static string FormatSecret(McpSecretChange change) =>
        change.Kind switch
        {
            McpSecretChangeKind.Replace => "*****",
            McpSecretChangeKind.Remove => "(removed)",
            _ => "(unchanged)",
        };
}
