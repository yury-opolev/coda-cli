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

    /// <summary>
    /// Width of the gutter column (shows <c>❯</c> on the focused row). Kept separate from
    /// <see cref="LabelWidth"/> so the column boundaries are explicit and testable.
    /// </summary>
    private const int GutterWidth = 2;

    /// <summary>X coordinate at which value widgets start (gutter + label).</summary>
    private const int ValueX = GutterWidth + LabelWidth;

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

    // ── prefix labels (one per scalar field; always created, shown/hidden by ApplyState) ──────
    // Each prefix label renders the field name in the label column so the value column is
    // self-explanatory even without context. The gutter indicator (❯) marks the focused row.

    private readonly Label GutterIndicator;
    private readonly Label ScopePrefixLabel;
    private readonly Label NamePrefixLabel;
    private readonly Label TransportPrefixLabel;
    private readonly Label CommandPrefixLabel;
    private readonly Label ArgumentsPrefixLabel;
    private readonly Label UrlPrefixLabel;
    private readonly Label HeadersPrefixLabel;
    private readonly Label AuthModePrefixLabel;
    private readonly Label ClientIdPrefixLabel;
    private readonly Label ScopesPrefixLabel;
    private readonly Label EnvironmentPrefixLabel;
    private readonly Label BearerTokenPrefixLabel;

    /// <summary>Fired when the Save button is activated. The overlay wires this to the save flow.</summary>
    internal event Action? SaveRequested;

    /// <summary>Fired when the Cancel button is activated. The overlay wires this to the cancel flow.</summary>
    internal event Action? CancelRequested;

    /// <summary>
    /// Current scroll offset (in field rows). Persists across renders so focus-tracking scrolls
    /// smoothly as the user tabs through fields. Clamped and updated on every <see cref="ApplyState"/> call.
    /// </summary>
    private int scrollOffset;

    /// <summary>
    /// The theme and driver last applied, retained so focus changes can recolour labels without the
    /// caller re-supplying them. Null until <see cref="ApplyTheme"/> runs.
    /// </summary>
    private TuiTheme? theme;
    private Terminal.Gui.Drivers.IDriver? driver;

    /// <summary>Cached label schemes; invalidated when the theme changes.</summary>
    private Terminal.Gui.Drawing.Scheme? normalLabelScheme;
    private Terminal.Gui.Drawing.Scheme? focusedLabelScheme;

    /// <summary>Fully inverted scheme, applied wholesale to the focused Save/Cancel button.</summary>
    private Terminal.Gui.Drawing.Scheme? selectionScheme;

    /// <summary>
    /// Scheme for the option selectors: normal text for the options at rest, the inverted
    /// selection attribute for the option under the cursor. Keeping the two roles apart is what
    /// makes the SELECTED option (marked <c>◉</c>) readable as a different thing from the FOCUSED
    /// option (inverted) inside the same widget.
    /// </summary>
    private Terminal.Gui.Drawing.Scheme? optionScheme;

    /// <summary>The field whose label currently carries the accent colour.</summary>
    private McpEditorField focusedField;

    // ── per-item widget pools (Task 8) ───────────────────────────────────────
    // List/map fields expand into one editable row per item. The pools grow on demand and are
    // reused across renders; pool index N always represents item index N of its field. Argument and
    // scope values are TextFields; environment and header VALUES are never TextFields (they stay on
    // the modal secret-prompt path), only their names are editable.
    private readonly List<TextField> argItemFields = [];
    private readonly List<TextField> scopeItemFields = [];
    private readonly List<MapItemRow> envItemRows = [];
    private readonly List<MapItemRow> headerItemRows = [];

    /// <summary>
    /// A single laid-out row in the editor. <see cref="ItemIndex"/> is <c>-1</c> for a scalar field
    /// (or the placeholder row of an empty list); otherwise it is the zero-based index of an item
    /// within a list/map field.
    /// </summary>
    private readonly record struct EditorRow(McpEditorField Field, int ItemIndex);


    internal McpEditorForm(McpBrowserController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));

        this.CanFocus = true;
        this.Width = Dim.Fill();
        this.Height = Dim.Fill();

        // ── gutter indicator and prefix labels ────────────────────────────────
        // Created before input widgets so they can be passed to this.Add in the right order.
        this.GutterIndicator = new Label { X = 0, Width = GutterWidth, Height = 1, Text = "❯ ", Visible = false };
        this.ScopePrefixLabel       = MakePrefixLabel("Scope:      ");
        this.NamePrefixLabel        = MakePrefixLabel("Name:       ");
        this.TransportPrefixLabel   = MakePrefixLabel("Transport:  ");
        this.CommandPrefixLabel     = MakePrefixLabel("Command:    ");
        this.ArgumentsPrefixLabel   = MakePrefixLabel("Arguments:  ");
        this.UrlPrefixLabel         = MakePrefixLabel("URL:        ");
        this.HeadersPrefixLabel     = MakePrefixLabel("Headers:    ");
        this.AuthModePrefixLabel    = MakePrefixLabel("Auth:       ");
        this.ClientIdPrefixLabel    = MakePrefixLabel("Client ID:  ");
        this.ScopesPrefixLabel      = MakePrefixLabel("Scopes:     ");
        this.EnvironmentPrefixLabel = MakePrefixLabel("Env:        ");
        this.BearerTokenPrefixLabel = MakePrefixLabel("Token:      ");

        // ── selectors ────────────────────────────────────────────────────────
        this.ScopeSelector = new OptionSelector
        {
            X = ValueX,
            Width = Dim.Fill(),
            Height = 1,
            Labels = ["project", "user"],
            Orientation = Orientation.Horizontal,
            TabStop = TabBehavior.TabStop,
            Visible = false,
        };

        this.TransportSelector = new OptionSelector
        {
            X = ValueX,
            Width = Dim.Fill(),
            Height = 1,
            Labels = ["stdio", "http"],
            Orientation = Orientation.Horizontal,
            TabStop = TabBehavior.TabStop,
            Visible = false,
        };

        this.AuthModeSelector = new OptionSelector
        {
            X = ValueX,
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
            X = ValueX,
            Width = Dim.Fill(),
            Height = 1,
            TabStop = TabBehavior.TabStop,
            Used = true,
            Visible = false,
        };

        this.CommandField = new TextField
        {
            Id = "Command",
            X = ValueX,
            Width = Dim.Fill(),
            Height = 1,
            TabStop = TabBehavior.TabStop,
            Used = true,
            Visible = false,
        };

        this.UrlField = new TextField
        {
            Id = "Url",
            X = ValueX,
            Width = Dim.Fill(),
            Height = 1,
            TabStop = TabBehavior.TabStop,
            Used = true,
            Visible = false,
        };

        this.ClientIdField = new TextField
        {
            Id = "ClientId",
            X = ValueX,
            Width = Dim.Fill(),
            Height = 1,
            TabStop = TabBehavior.TabStop,
            Used = true,
            Visible = false,
        };

        // ── summary labels (placeholder rows for empty lists, and bearer token) ─────────────────
        // These must be focusable so Tab traversal reaches them and Ctrl+N / Ctrl+R / Alt+Up/Down
        // can operate on the right field when the focus is on a placeholder. The bearer-token row
        // also needs focus so Enter triggers the modal secret-replacement prompt.
        this.ArgumentsSummaryLabel  = new Label { X = ValueX, Width = Dim.Fill(), Height = 1, Visible = false, CanFocus = true, TabStop = TabBehavior.TabStop };
        this.HeadersSummaryLabel    = new Label { X = ValueX, Width = Dim.Fill(), Height = 1, Visible = false, CanFocus = true, TabStop = TabBehavior.TabStop };
        this.ScopesSummaryLabel     = new Label { X = ValueX, Width = Dim.Fill(), Height = 1, Visible = false, CanFocus = true, TabStop = TabBehavior.TabStop };
        this.EnvironmentSummaryLabel = new Label { X = ValueX, Width = Dim.Fill(), Height = 1, Visible = false, CanFocus = true, TabStop = TabBehavior.TabStop };

        // BearerToken is always a read-only label — never bound to a TextField.
        this.BearerTokenLabel = new Label { X = ValueX, Width = Dim.Fill(), Height = 1, Visible = false, CanFocus = true, TabStop = TabBehavior.TabStop };

        // ── buttons ───────────────────────────────────────────────────────────
        // ShadowStyle.None: the default drop shadow renders a stray half-block glyph after the
        // button ("⟦ Save ⟧▖") which reads as corruption in a dense form rather than as depth.
        // X = GutterWidth aligns them with the label column so the ❯ marker sits directly beside
        // them; at X = 0 they painted over the marker and the active action row was unmarked.
        this.SaveButton = new Button
        {
            X = GutterWidth,
            Text = "Save",
            TabStop = TabBehavior.TabStop,
            ShadowStyle = ShadowStyles.None,
            Visible = false,
        };

        this.CancelButton = new Button
        {
            X = GutterWidth,
            Text = "Cancel",
            TabStop = TabBehavior.TabStop,
            ShadowStyle = ShadowStyles.None,
            Visible = false,
        };

        // All widgets go into the view tree now, before any value is assigned.
        // Assigning Text/Value after Add avoids the "first keystroke replaces a character" trap
        // documented in WidgetIntegrationSpikeTests.TextField_inserts_mid_string_at_the_caret.
        this.Add(
            // Gutter and prefix labels first so they paint under/beside their paired input widget.
            this.GutterIndicator,
            this.ScopePrefixLabel,
            this.NamePrefixLabel,
            this.TransportPrefixLabel,
            this.CommandPrefixLabel,
            this.ArgumentsPrefixLabel,
            this.UrlPrefixLabel,
            this.HeadersPrefixLabel,
            this.AuthModePrefixLabel,
            this.ClientIdPrefixLabel,
            this.ScopesPrefixLabel,
            this.EnvironmentPrefixLabel,
            this.BearerTokenPrefixLabel,
            // Input widgets.
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
    /// Applies the theme to the form. The focused field's label is drawn in the accent colour so
    /// the active row is identifiable by colour as well as by the gutter marker — a marker alone is
    /// easy to miss on a dense form, and colour is what the rest of the TUI already uses to mean
    /// "this is the thing you are on".
    /// </summary>
    internal void ApplyTheme(TuiTheme theme, Terminal.Gui.Drivers.IDriver? driver)
    {
        ArgumentNullException.ThrowIfNull(theme);

        this.theme = theme;
        this.driver = driver;
        this.normalLabelScheme = null;
        this.focusedLabelScheme = null;
        this.selectionScheme = null;
        this.optionScheme = null;
        this.RefreshFocusAffordances();
    }

    /// <summary>
    /// Repaints every focus affordance so exactly the focused field is marked: its prefix label
    /// carries the accent scheme, the Save/Cancel button (if that is the focused field) is
    /// inverted, and the selectors get a scheme whose cursor role is unmistakably different from
    /// its resting role. Cheap enough to run on every focus change: the widget count is fixed and
    /// small.
    /// </summary>
    private void RefreshFocusAffordances()
    {
        // Fall back to the current theme rather than returning: an unthemed form used to render NO
        // focus affordance at all, so a missed ApplyTheme call was invisible instead of merely
        // mis-coloured. Failing soft here keeps the cursor visible whatever the caller forgot.
        var t = this.theme ?? CodaThemes.Current.Tui;
        var normal = t.Attribute(t.TranscriptAssistant, t.Background, this.driver);
        var selection = t.Attribute(t.SelectionText, t.SelectionBackground, this.driver);

        this.normalLabelScheme ??= SolidScheme(normal);
        this.focusedLabelScheme ??= SolidScheme(t.Attribute(t.Palette.Accent, t.Background, this.driver));
        this.selectionScheme ??= SolidScheme(selection);
        this.optionScheme ??= CursorScheme(normal, selection);

        var focusedLabel = this.PrefixLabelForField(this.focusedField);
        foreach (var label in this.AllPrefixLabels())
        {
            label.SetScheme(ReferenceEquals(label, focusedLabel)
                ? this.focusedLabelScheme
                : this.normalLabelScheme);
        }

        this.GutterIndicator.SetScheme(this.focusedLabelScheme);

        // A focused button is inverted wholesale. Terminal.Gui expresses Button focus only through
        // the scheme, and the surface scheme's focus role is too close to its normal role to answer
        // "is Save or Cancel about to fire?" at a glance.
        this.SaveButton.SetScheme(this.focusedField == McpEditorField.Save
            ? this.selectionScheme
            : this.normalLabelScheme);
        this.CancelButton.SetScheme(this.focusedField == McpEditorField.Cancel
            ? this.selectionScheme
            : this.normalLabelScheme);

        this.ScopeSelector.SetScheme(this.optionScheme);
        this.TransportSelector.SetScheme(this.optionScheme);
        this.AuthModeSelector.SetScheme(this.optionScheme);
    }

    /// <summary>
    /// A scheme that reads as "resting" at <paramref name="normal"/> and as "the cursor is here" at
    /// <paramref name="cursor"/>. Both the focus and the active roles carry the cursor attribute so
    /// the mark survives whether or not the widget's container currently owns the keyboard.
    /// </summary>
    private static Terminal.Gui.Drawing.Scheme CursorScheme(
        Terminal.Gui.Drawing.Attribute normal,
        Terminal.Gui.Drawing.Attribute cursor) => new()
    {
        Normal = normal,
        HotNormal = normal,
        Focus = cursor,
        HotFocus = cursor,
        Active = cursor,
        HotActive = cursor,
        Highlight = cursor,
        Editable = normal,
        ReadOnly = normal,
        Disabled = normal,
    };

    private static Terminal.Gui.Drawing.Scheme SolidScheme(Terminal.Gui.Drawing.Attribute attribute) => new()
    {
        Normal = attribute,
        HotNormal = attribute,
        Focus = attribute,
        HotFocus = attribute,
        Active = attribute,
        HotActive = attribute,
        Highlight = attribute,
        Editable = attribute,
        ReadOnly = attribute,
        Disabled = attribute,
    };

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
        var draft = editor.Draft;

        // Expand list/map fields into one row per item so long lists take part in the same
        // scroll-offset logic as scalar fields (spec: "a long argument list stays reachable").
        var rows = BuildRows(fields, draft);

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

        // Compute which row is focused so we can keep it within the scroll window. For a list field
        // the focused row is the one whose item index matches the editor's SelectedItem.
        var focusedIndex = 0;
        for (var ri = 0; ri < rows.Count; ri++)
        {
            if (rows[ri].Field != editor.FocusedField)
            {
                continue;
            }

            focusedIndex = ri;
            if (rows[ri].ItemIndex < 0 || rows[ri].ItemIndex == editor.SelectedItem)
            {
                break;
            }
        }

        // Compute scroll offset: always derive it from the focused row index and height so that
        // back-to-back renders with different heights (from FrameChanged callbacks during layout
        // passes) produce consistent results.
        var maxOffset = Math.Max(0, rows.Count - height);
        this.scrollOffset = focusedIndex >= height
            ? Math.Clamp(focusedIndex - height + 1, 0, maxOffset)
            : 0;

        // Build a lookup of which subviews should be visible after layout.
        var visibleViews = new HashSet<View>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < this.scrollOffset || i >= this.scrollOffset + height) continue;
            if (rows[i].ItemIndex == int.MinValue) continue; // separator — no widget
            var v = this.ViewForRow(rows[i]);
            if (v is not null) visibleViews.Add(v);

            // Prefix label: shown on a scalar row, and on the FIRST row of a multi-row field
            // group so the group is still labelled. Later item rows align underneath it.
            if (rows[i].ItemIndex < 0 || rows[i].ItemIndex == 0)
            {
                var pl = this.PrefixLabelForField(rows[i].Field);
                if (pl is not null) visibleViews.Add(pl);
            }
        }

        // The gutter indicator is always present when any field is visible.
        visibleViews.Add(this.GutterIndicator);

        // Suspend ValueChanged handlers while we push state into widgets.
        this.suppressSync = true;
        try
        {
            // Pass 1: position and show every view that belongs in the viewport. This ensures the
            // focused view is visible before we hide anything, so Terminal.Gui never loses its
            // focus target.
            this.LayoutRows(rows, editor, this.scrollOffset, height, focusedIndex);

            // Pass 2: hide every subview that is NOT in the visible set.
            foreach (var view in this.SubViews)
            {
                if (!visibleViews.Contains(view))
                {
                    view.Visible = false;
                }
            }

            // Pass 3: give the focused field real widget focus. Focus previously only ever flowed
            // widget → controller, so opening the editor left everything unfocused: no option in a
            // selector was marked as the one under the cursor and neither button looked active
            // until the user pressed Tab.
            this.FocusField(editor.FocusedField);
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

    /// <summary>
    /// Hides every widget in the form. The overlay calls this when it leaves the editor, so a row
    /// the user has walked away from cannot paint over the list or detail view it returned to.
    /// </summary>
    internal void HideAllFields()
    {
        foreach (var view in this.SubViews)
        {
            view.Visible = false;
        }
    }

    /// <summary>
    /// Gives real widget focus to the field the controller considers focused, when that field has
    /// a focusable widget currently on screen. Runs under <see cref="suppressSync"/> so the
    /// resulting <c>HasFocusChanged</c> events do not write the focus straight back into the
    /// controller and re-enter this method.
    /// </summary>
    private void FocusField(McpEditorField field)
    {
        if (this.ViewForField(field) is not { Visible: true, CanFocus: true, Enabled: true } view)
        {
            return;
        }

        if (!view.HasFocus)
        {
            view.SetFocus();
        }
    }

    /// <summary>
    /// Moves field focus one step in <paramref name="direction"/>; exposed for unit tests that
    /// cannot drive the key event through the full application focus chain.
    /// This is the same function that <see cref="OnKeyDown"/> calls for CursorDown/CursorUp.
    /// </summary>
    internal void MoveFocusForTest(NavigationDirection direction) =>
        this.MoveFieldFocus(direction);

    /// <inheritdoc/>
    /// <remarks>
    /// Tab/Shift+Tab move focus between fields. CursorDown/Up use <see cref="MoveFieldFocus"/>
    /// instead of <see cref="View.AdvanceFocus"/> to guarantee single-step field navigation:
    /// AdvanceFocus walks all descendant tab stops including the internal CheckBoxes of an
    /// OptionSelector, which causes two presses to skip past a 2-option selector. MoveFieldFocus
    /// moves between the form's own direct children only.
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
            this.MoveFieldFocus(NavigationDirection.Forward);
            return true;
        }

        if (key == Key.CursorUp)
        {
            this.MoveFieldFocus(NavigationDirection.Backward);
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
    /// Returns the prefix label for a scalar field, or <c>null</c> for fields that do not have
    /// one (Save, Cancel, or unrecognised values).
    /// </summary>
    private Label? PrefixLabelForField(McpEditorField field) => field switch    {
        McpEditorField.Scope       => this.ScopePrefixLabel,
        McpEditorField.Name        => this.NamePrefixLabel,
        McpEditorField.Transport   => this.TransportPrefixLabel,
        McpEditorField.Command     => this.CommandPrefixLabel,
        McpEditorField.Arguments   => this.ArgumentsPrefixLabel,
        McpEditorField.Url         => this.UrlPrefixLabel,
        McpEditorField.Headers     => this.HeadersPrefixLabel,
        McpEditorField.AuthMode    => this.AuthModePrefixLabel,
        McpEditorField.ClientId    => this.ClientIdPrefixLabel,
        McpEditorField.Scopes      => this.ScopesPrefixLabel,
        McpEditorField.Environment => this.EnvironmentPrefixLabel,
        McpEditorField.BearerToken => this.BearerTokenPrefixLabel,
        _ => null,
    };

    /// <summary>
    /// Moves focus to the next or previous DIRECT focusable child of this form without descending
    /// into any child's internal sub-views (e.g. the CheckBoxes inside an OptionSelector).
    /// </summary>
    /// <remarks>
    /// <see cref="View.AdvanceFocus"/> walks the entire descendant tab-stop tree, so pressing
    /// CursorDown once while an OptionSelector is focused advances to its next internal CheckBox
    /// rather than to the next field. This method instead works exclusively with the form's own
    /// SubViews list, skipping non-focusable and invisible children.
    /// </remarks>
    private void MoveFieldFocus(NavigationDirection direction)
    {
        var children = this.SubViews.Where(v => v.CanFocus && v.Visible).ToList();
        if (children.Count == 0) return;

        var currentIdx = children.FindIndex(HasFocusInSubtree);
        if (currentIdx < 0) { children[0].SetFocus(); return; }

        var nextIdx = direction == NavigationDirection.Forward
            ? (currentIdx + 1) % children.Count
            : (currentIdx - 1 + children.Count) % children.Count;

        children[nextIdx].SetFocus();
    }

    /// <summary>Returns true if <paramref name="v"/> has focus or has a focused descendant.</summary>
    /// <remarks>
    /// Uses <see cref="View.MostFocused"/> to detect focus inside composite widgets such as
    /// <see cref="OptionSelector"/>, whose internal CheckBoxes may live in a nested container
    /// that is not directly exposed via <see cref="View.SubViews"/>.
    /// </remarks>
    private static bool HasFocusInSubtree(View v) =>
        v.HasFocus || v.MostFocused is not null;

    /// <summary>
    /// Pass 1: position and make visible every field whose list index falls within the scroll
    /// window [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="height"/>).
    /// Positions the gutter indicator at the focused row's viewport Y.
    /// Pass 2 (hiding out-of-window views) is the caller's responsibility.
    /// </summary>
    private void LayoutRows(
        IReadOnlyList<EditorRow> rows,
        McpEditorState editor,
        int offset,
        int height,
        int focusedIndex)
    {
        var draft = editor.Draft;

        // Position gutter indicator at the focused field's viewport row.
        var focusedViewportY = focusedIndex - offset;
        this.GutterIndicator.Y = focusedViewportY;
        this.GutterIndicator.Visible = focusedViewportY >= 0 && focusedViewportY < height;

        // Keep the accent in step with the gutter: both mark the same row, and a stale colour on a
        // row the marker has left is worse than no colour at all.
        this.focusedField = editor.FocusedField;
        this.RefreshFocusAffordances();

        for (var i = 0; i < rows.Count; i++)
        {
            if (i < offset || i >= offset + height) continue;

            var row = i - offset;
            var descriptor = rows[i];
            if (descriptor.ItemIndex == int.MinValue) continue; // separator — blank row, no widget

            if (descriptor.ItemIndex < 0)
            {
                this.LayoutScalarField(descriptor.Field, editor, row);
            }
            else
            {
                this.LayoutItemRow(descriptor, draft, row);
            }
        }
    }

    /// <summary>
    /// Expands the ordered scalar field set into concrete rows, splitting each non-empty list/map
    /// field into one row per item and inserting a blank separator row after each field group.
    /// An empty list keeps a single placeholder row so the user still has somewhere to press Ctrl+N.
    /// </summary>
    private static IReadOnlyList<EditorRow> BuildRows(
        IReadOnlyList<McpEditorField> fields,
        McpServerDraft draft)
    {
        // Pre-size: each field gets its items (or 1 placeholder) plus 1 separator (except Cancel).
        var rows = new List<EditorRow>(fields.Count * 2);
        foreach (var field in fields)
        {
            var count = ItemCount(field, draft);
            if (IsListField(field) && count > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    rows.Add(new EditorRow(field, i));
                }
            }
            else
            {
                rows.Add(new EditorRow(field, -1));
            }

            // Blank separator after each field group, but NOT between Save and Cancel (they
            // are action buttons that go together, and a separator would expose the button's
            // bottom-shadow artifact row in the viewport).
            if (field != McpEditorField.Cancel && field != McpEditorField.Save)
            {
                rows.Add(new EditorRow(field, int.MinValue));
            }
        }

        return rows;
    }

    private static bool IsListField(McpEditorField field) => field is
        McpEditorField.Arguments or
        McpEditorField.Scopes or
        McpEditorField.Environment or
        McpEditorField.Headers;

    private static int ItemCount(McpEditorField field, McpServerDraft draft) => field switch
    {
        McpEditorField.Arguments => draft.Args.IsDefault ? 0 : draft.Args.Length,
        McpEditorField.Scopes => draft.Scopes.IsDefault ? 0 : draft.Scopes.Length,
        McpEditorField.Environment => draft.Environment.IsDefault ? 0 : draft.Environment.Length,
        McpEditorField.Headers => draft.Headers.IsDefault ? 0 : draft.Headers.Length,
        _ => 0,
    };

    /// <summary>Returns the subview for a row, creating pooled per-item widgets on demand.</summary>
    private View? ViewForRow(EditorRow row)
    {
        if (row.ItemIndex == int.MinValue) return null; // separator
        if (row.ItemIndex < 0) return this.ViewForField(row.Field);
        return row.Field switch
        {
            McpEditorField.Arguments => this.EnsureListField(this.argItemFields, row.ItemIndex, McpEditorField.Arguments),
            McpEditorField.Scopes => this.EnsureListField(this.scopeItemFields, row.ItemIndex, McpEditorField.Scopes),
            McpEditorField.Environment => this.EnsureMapRow(this.envItemRows, row.ItemIndex, McpEditorField.Environment),
            McpEditorField.Headers => this.EnsureMapRow(this.headerItemRows, row.ItemIndex, McpEditorField.Headers),
            _ => this.ViewForField(row.Field),
        };
    }

    private void LayoutItemRow(EditorRow descriptor, McpServerDraft draft, int row)
    {
        // A non-empty list/map field is split into one row per item, so the field's own label has
        // no scalar row to live on. Put it on the first item row — without this an expanded field
        // renders as an unlabelled orphan row and the user cannot tell what it belongs to.
        if (descriptor.ItemIndex == 0 && this.PrefixLabelForField(descriptor.Field) is { } groupLabel)
        {
            groupLabel.Y = row;
            groupLabel.Visible = true;
        }

        switch (descriptor.Field)
        {
            case McpEditorField.Arguments:
                LayoutListField(
                    this.EnsureListField(this.argItemFields, descriptor.ItemIndex, McpEditorField.Arguments),
                    draft.Args,
                    descriptor.ItemIndex,
                    row);
                break;

            case McpEditorField.Scopes:
                LayoutListField(
                    this.EnsureListField(this.scopeItemFields, descriptor.ItemIndex, McpEditorField.Scopes),
                    draft.Scopes,
                    descriptor.ItemIndex,
                    row);
                break;

            case McpEditorField.Environment:
                LayoutMapRow(
                    this.EnsureMapRow(this.envItemRows, descriptor.ItemIndex, McpEditorField.Environment),
                    draft.Environment,
                    descriptor.ItemIndex,
                    row);
                break;

            case McpEditorField.Headers:
                LayoutMapRow(
                    this.EnsureMapRow(this.headerItemRows, descriptor.ItemIndex, McpEditorField.Headers),
                    draft.Headers,
                    descriptor.ItemIndex,
                    row);
                break;
        }
    }

    private static void LayoutListField(
        TextField field,
        System.Collections.Immutable.ImmutableArray<string> values,
        int index,
        int row)
    {
        field.Y = row;
        field.Value = !values.IsDefault && index < values.Length ? values[index] : string.Empty;
        field.InsertionPoint = field.Text?.Length ?? 0;
        field.Visible = true;
    }

    private static void LayoutMapRow(
        MapItemRow mapRow,
        System.Collections.Immutable.ImmutableArray<McpNamedSecretDraft> values,
        int index,
        int row)
    {
        mapRow.X = ValueX;
        mapRow.Y = row;
        var named = !values.IsDefault && index < values.Length ? values[index] : null;
        mapRow.Name.Value = named?.Name ?? string.Empty;
        mapRow.Name.InsertionPoint = mapRow.Name.Text?.Length ?? 0;

        // Invariant: the map VALUE is never an editable field. It is a read-only label showing the
        // secret placeholder; the real value flows through the modal secret-prompt path.
        mapRow.Value.Text = named is null ? "(unchanged)" : FormatSecret(named.Change);
        mapRow.Visible = true;
    }

    /// <summary>
    /// Lays out one scalar field (or the placeholder row of an empty list) at <paramref name="row"/>.
    /// Also positions and shows the corresponding prefix label.
    /// </summary>
    private void LayoutScalarField(
        McpEditorField field,
        McpEditorState editor,
        int row)
    {
        var draft = editor.Draft;

        // Show the prefix label for this field (non-null for all input fields; null for Save/Cancel).
        var prefixLabel = this.PrefixLabelForField(field);
        if (prefixLabel is not null)
        {
            prefixLabel.Y = row;
            prefixLabel.Visible = true;
        }

        {
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
                    this.ArgumentsSummaryLabel.Text = FormatCount(draft.Args.IsDefault ? 0 : draft.Args.Length);
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
                    this.HeadersSummaryLabel.Text = FormatCount(draft.Headers.Length);
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
                    this.ScopesSummaryLabel.Text = FormatCount(draft.Scopes.Length);
                    this.ScopesSummaryLabel.Visible = true;
                    break;

                case McpEditorField.Environment:
                    this.EnvironmentSummaryLabel.Y = row;
                    this.EnvironmentSummaryLabel.Text = FormatCount(draft.Environment.Length);
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

    /// <summary>
    /// Returns the pooled argument/scope <see cref="TextField"/> for <paramref name="index"/>,
    /// creating and wiring pooled widgets on demand. Pool index N always maps to item index N, so
    /// closures can safely capture the index.
    /// </summary>
    private TextField EnsureListField(List<TextField> pool, int index, McpEditorField field)
    {
        while (pool.Count <= index)
        {
            var itemIndex = pool.Count;
            var textField = new TextField
            {
                Id = $"{field}Item{itemIndex}",
                X = ValueX,
                Width = Dim.Fill(),
                Height = 1,
                TabStop = TabBehavior.TabStop,
                Used = true,
                Visible = false,
            };
            this.Add(textField);
            pool.Add(textField);

            textField.ValueChanged += (_, _) =>
            {
                if (this.suppressSync) return;
                var value = textField.Text ?? string.Empty;
                this.controller.UpdateEditorDraft(d => SetListValue(d, field, itemIndex, value));
            };
            textField.HasFocusChanged += (sender, _) =>
            {
                if (!this.suppressSync && sender is View v && v.HasFocus)
                {
                    this.controller.UpdateEditorFocusItem(field, itemIndex, McpEditorItemPart.Value);
                }
            };
        }

        return pool[index];
    }

    /// <summary>
    /// Returns the pooled environment/header row for <paramref name="index"/>, creating and wiring
    /// pooled widgets on demand. The name is an editable field; the value is a read-only label
    /// (never a TextField — see <see cref="MapItemRow"/>).
    /// </summary>
    private MapItemRow EnsureMapRow(List<MapItemRow> pool, int index, McpEditorField field)
    {
        while (pool.Count <= index)
        {
            var itemIndex = pool.Count;
            var mapRow = new MapItemRow($"{field}Item{itemIndex}");
            this.Add(mapRow);
            pool.Add(mapRow);

            mapRow.Name.ValueChanged += (_, _) =>
            {
                if (this.suppressSync) return;
                var value = mapRow.Name.Text ?? string.Empty;
                this.controller.UpdateEditorDraft(d => SetNamedName(d, field, itemIndex, value));
            };
            mapRow.Name.HasFocusChanged += (sender, _) =>
            {
                if (!this.suppressSync && sender is View v && v.HasFocus)
                {
                    this.controller.UpdateEditorFocusItem(field, itemIndex, McpEditorItemPart.Name);
                }
            };
            mapRow.Value.HasFocusChanged += (sender, _) =>
            {
                if (!this.suppressSync && sender is View v && v.HasFocus)
                {
                    this.controller.UpdateEditorFocusItem(field, itemIndex, McpEditorItemPart.Value);
                }
            };
        }

        return pool[index];
    }

    /// <summary>
    /// Writes an argument or scope display value back into the draft, keeping the parallel identity
    /// array (<c>ArgumentItems</c>/<c>ScopeItems</c>) aligned so the commit path can still recover
    /// redacted raw values by Guid.
    /// </summary>
    private static McpServerDraft SetListValue(McpServerDraft draft, McpEditorField field, int index, string value)
    {
        if (field == McpEditorField.Arguments)
        {
            if (draft.Args.IsDefault || index >= draft.Args.Length) return draft;
            var items = draft.ArgumentItems.IsDefault || index >= draft.ArgumentItems.Length
                ? draft.ArgumentItems
                : draft.ArgumentItems.SetItem(index, draft.ArgumentItems[index] with { Value = value });
            return draft with { Args = draft.Args.SetItem(index, value), ArgumentItems = items };
        }

        if (draft.Scopes.IsDefault || index >= draft.Scopes.Length) return draft;
        var scopeItems = draft.ScopeItems.IsDefault || index >= draft.ScopeItems.Length
            ? draft.ScopeItems
            : draft.ScopeItems.SetItem(index, draft.ScopeItems[index] with { Value = value });
        return draft with { Scopes = draft.Scopes.SetItem(index, value), ScopeItems = scopeItems };
    }

    private static McpServerDraft SetNamedName(McpServerDraft draft, McpEditorField field, int index, string value)
    {
        if (field == McpEditorField.Environment)
        {
            if (draft.Environment.IsDefault || index >= draft.Environment.Length) return draft;
            return draft with { Environment = draft.Environment.SetItem(index, draft.Environment[index] with { Name = value }) };
        }

        if (draft.Headers.IsDefault || index >= draft.Headers.Length) return draft;
        return draft with { Headers = draft.Headers.SetItem(index, draft.Headers[index] with { Name = value }) };
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
        // Placeholder summary rows for empty lists: must update FocusedField so Ctrl+N, Ctrl+R,
        // and Alt+Up/Down can operate on the right collection when focus is on the placeholder.
        this.ArgumentsSummaryLabel.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.Arguments); };
        this.EnvironmentSummaryLabel.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.Environment); };
        this.HeadersSummaryLabel.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.Headers); };
        this.ScopesSummaryLabel.HasFocusChanged += (s, _) => { if (!this.suppressSync && s is View v && v.HasFocus) this.controller.UpdateEditorFocus(McpEditorField.Scopes); };
    }
    private static string FormatCount(int count) =>
        count == 0 ? "(none)" : $"{count} item(s)";

    /// <summary>
    /// Creates a non-focusable label for the label column (X=<see cref="GutterWidth"/>,
    /// Width=<see cref="LabelWidth"/>). All prefix labels are built with this factory so the
    /// column geometry is consistent and easy to change in one place.
    /// </summary>
    private Label[] AllPrefixLabels() =>
    [
        this.ScopePrefixLabel,
        this.NamePrefixLabel,
        this.TransportPrefixLabel,
        this.CommandPrefixLabel,
        this.ArgumentsPrefixLabel,
        this.UrlPrefixLabel,
        this.HeadersPrefixLabel,
        this.AuthModePrefixLabel,
        this.ClientIdPrefixLabel,
        this.ScopesPrefixLabel,
        this.EnvironmentPrefixLabel,
        this.BearerTokenPrefixLabel,
    ];

    private static Label MakePrefixLabel(string text) => new Label
    {
        X = GutterWidth,
        Width = LabelWidth,
        Height = 1,
        Text = text,
        Visible = false,
        CanFocus = false,
    };

    private static string FormatSecret(McpSecretChange change) =>
        change.Kind switch
        {
            McpSecretChangeKind.Replace => "*****",
            McpSecretChangeKind.Remove => "(removed)",
            _ => "(unchanged)",
        };

    /// <summary>
    /// A single environment/header row: an editable name <see cref="TextField"/> next to a
    /// read-only value <see cref="Label"/>. The value is DELIBERATELY a label and never a
    /// <see cref="TextField"/> — this is the hard secret-safety invariant (spec §7.3): map values
    /// only ever render as <c>"*****"</c>/<c>"(removed)"</c>/<c>"(unchanged)"</c> and their real
    /// replacement flows through the modal secret-prompt path.
    /// </summary>
    private sealed class MapItemRow : View
    {
        internal readonly TextField Name;
        internal readonly Label Value;

        internal MapItemRow(string id)
        {
            this.Id = id;
            this.Width = Dim.Fill();
            this.Height = 1;
            this.Visible = false;

            this.Name = new TextField
            {
                Id = id + "Name",
                Width = Dim.Percent(55),
                Height = 1,
                TabStop = TabBehavior.TabStop,
                Used = true,
            };

            this.Value = new Label
            {
                Id = id + "Value",
                X = Pos.Right(this.Name) + 1,
                Width = Dim.Fill(),
                Height = 1,
                CanFocus = true,
                TabStop = TabBehavior.TabStop,
            };

            this.Add(this.Name, this.Value);
        }
    }
}
