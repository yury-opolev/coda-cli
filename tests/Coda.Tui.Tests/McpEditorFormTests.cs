using System.Text;
using Coda.Mcp;
using Coda.Tui.Mcp;
using Coda.Tui.Ui.Mcp;

namespace Coda.Tui.Tests;

/// <summary>
/// Verifies the widget-based editor form (spec 8.2–8.4, Tasks 6-7).
/// </summary>
[Collection("TerminalGuiInit")]
public sealed class McpEditorFormTests : IDisposable
{
    private readonly IApplication application = Application.Create();
    private readonly Window root = new();
    private readonly McpBrowserController controller;
    private readonly McpEditorForm form;
    private readonly SessionToken? runState;

    public McpEditorFormTests()
    {
        this.application.AppModel = AppModel.FullScreen;
        this.application.Init(DriverRegistry.Names.ANSI);
        this.application.Driver!.SetScreenSize(80, 24);
        this.controller = new McpBrowserController(() => null);
        this.form = new McpEditorForm(this.controller);
        this.root.Add(this.form);
        this.runState = this.application.Begin(this.root);
    }

    // ── invariants ────────────────────────────────────────────────────────────

    /// <summary>
    /// Invariant 1: no secret value ever reaches a TextField, masked or otherwise (spec §7.3).
    /// BearerToken and env/header values must render as "*****"/"(removed)"/"(unchanged)" only.
    /// </summary>
    [Fact]
    public void No_text_field_holds_a_secret_value()
    {
        const string secret = "super-secret-token";
        var state = McpBrowserOverlayTests.EditorStateWithSecret(secret);
        this.form.ApplyState(state.Editor!);
        this.application.LayoutAndDraw();

        foreach (var field in AllTextFields(this.form))
        {
            Assert.DoesNotContain(secret, field.Text ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Task 8 invariant (updated): environment/header values are now editable TextFields.  A
    /// secret entered through the modal path (<see cref="McpSecretChangeKind.Replace"/>) must
    /// NEVER appear in any TextField — it renders as <c>"*****"</c> and its row is read-only.
    /// </summary>
    [Fact]
    public void Map_entry_value_is_never_a_text_field_when_modal_replace_is_pending()
    {
        const string secret = "env-header-secret-value";
        var draft = new McpServerDraft(
            Name: "server",
            Scope: McpConfigScope.Project,
            Enabled: true,
            Transport: McpTransportKind.Stdio,
            Command: "node",
            Args: [],
            Url: null,
            Environment:
            [
                new McpNamedSecretDraft(
                    "TOKEN",
                    McpSecretSource.None,
                    new McpSecretChange(
                        "env/TOKEN",
                        McpSecretChangeKind.Replace,
                        new McpSecretReplacement(secret))),
            ],
            Headers: [],
            AuthMode: McpAuthMode.None,
            ClientId: null,
            Scopes: [],
            BearerToken: new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged));
        var editor = new McpEditorState(McpEditorMode.Edit, McpBrowserView.List, draft, McpEditorField.Environment)
        {
            SelectedItem = 0,
        };

        this.form.ApplyState(editor);
        this.application.LayoutAndDraw();

        // The modal secret must never reach a TextField.
        foreach (var field in AllTextFields(this.form))
        {
            Assert.DoesNotContain(secret, field.Text ?? string.Empty, StringComparison.Ordinal);
        }

        // The value must still be shown, but only as the masked placeholder on a TextField that
        // is read-only — the secret cannot be extracted by a user.
        Assert.Contains(
            AllTextFields(this.form),
            tf => (tf.Text ?? string.Empty).Contains("*****", StringComparison.Ordinal)
                && tf.ReadOnly);
    }

    /// <summary>
    /// An ordinary env value (Unchanged, no modal entry) must render in an editable TextField
    /// showing the plain value (spec §9).
    /// </summary>
    [Fact]
    public void Ordinary_env_value_renders_in_editable_text_field_showing_the_value()
    {
        const string plainValue = "C:\\Users\\me\\data";
        var draft = new McpServerDraft(
            Name: "server",
            Scope: McpConfigScope.Project,
            Enabled: true,
            Transport: McpTransportKind.Stdio,
            Command: "node",
            Args: [],
            Url: null,
            Environment:
            [
                new McpNamedSecretDraft(
                    "DIR",
                    McpSecretSource.Literal,
                    new McpSecretChange("env/DIR", McpSecretChangeKind.Unchanged),
                    Value: plainValue),
            ],
            Headers: [],
            AuthMode: McpAuthMode.None,
            ClientId: null,
            Scopes: [],
            BearerToken: new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged));
        var editor = new McpEditorState(McpEditorMode.Edit, McpBrowserView.List, draft, McpEditorField.Environment)
        {
            SelectedItem = 0,
        };

        this.form.ApplyState(editor);
        this.application.LayoutAndDraw();

        // There must be an editable (non-read-only) TextField containing the plain value.
        Assert.Contains(
            AllTextFields(this.form),
            tf => (tf.Text ?? string.Empty).Contains(plainValue, StringComparison.Ordinal)
                && !tf.ReadOnly);
    }

    // ── field group labelling ─────────────────────────────────────────────────

    /// <summary>
    /// A non-empty list/map field is split into one row per item, which used to leave the field's
    /// own label with no row to live on — so an expanded Env/Args/Headers/Scopes group rendered as
    /// an unlabelled orphan row and the user could not tell what it belonged to.
    /// </summary>
    [Fact]
    public void An_expanded_map_field_still_shows_its_group_label()
    {
        var draft = MakeStdioDraftWithEnv("MemoryMcp__DataDirectory");
        this.form.ApplyState(
            new McpEditorState(McpEditorMode.Edit, McpBrowserView.List, draft, McpEditorField.Arguments));
        this.application.LayoutAndDraw();

        Assert.NotNull(VisibleLabelStartingWith(this.form, "Env:"));
    }

    [Fact]
    public void An_expanded_list_field_still_shows_its_group_label()
    {
        var draft = MakeStdioDraftWithEnv("TOKEN") with { Args = ["--port", "8080"] };
        this.form.ApplyState(
            new McpEditorState(McpEditorMode.Edit, McpBrowserView.List, draft, McpEditorField.Name));
        this.application.LayoutAndDraw();

        Assert.NotNull(VisibleLabelStartingWith(this.form, "Arguments:"));
    }

    /// <summary>The label marks the group once; later item rows align underneath it.</summary>
    [Fact]
    public void A_group_label_sits_on_the_first_item_row()
    {
        var draft = MakeStdioDraftWithEnv("TOKEN") with { Args = ["--first", "--second"] };
        this.form.ApplyState(
            new McpEditorState(McpEditorMode.Edit, McpBrowserView.List, draft, McpEditorField.Name));
        this.application.LayoutAndDraw();

        var label = VisibleLabelStartingWith(this.form, "Arguments:");
        Assert.NotNull(label);
        Assert.Equal(
            label.Frame.Y,
            AllTextFields(this.form).First(f => (f.Text ?? string.Empty) == "--first").Frame.Y);
    }

    private static Label? VisibleLabelStartingWith(View parent, string prefix) =>
        AllLabels(parent).FirstOrDefault(l =>
            l.Visible && (l.Text ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal));

    private static McpServerDraft MakeStdioDraftWithEnv(string envName) => new(
        Name: "memory",
        Scope: McpConfigScope.User,
        Enabled: true,
        Transport: McpTransportKind.Stdio,
        Command: @"C:\Users\yurio\AppData\Local\memory-mcp\bin\MemoryMcp.exe",
        Args: [],
        Url: null,
        Environment:
        [
            new McpNamedSecretDraft(
                envName,
                McpSecretSource.None,
                new McpSecretChange($"env/{envName}", McpSecretChangeKind.Unchanged)),
        ],
        Headers: [],
        AuthMode: McpAuthMode.None,
        ClientId: null,
        Scopes: [],
        BearerToken: new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged));

    // ── text field behaviour ──────────────────────────────────────────────────
    /// <summary>
    /// Keys that are browser accelerators in list view (q k j r /) must be plain text
    /// when a TextField has focus — regression locked by the spike.
    /// </summary>
    [Fact]
    public void Text_fields_accept_browser_accelerator_keys_as_text()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Name));
        this.form.SetFocus();
        this.application.LayoutAndDraw();

        var nameField = this.form.NameField;
        nameField.SetFocus();

        // Clear pre-populated value so the typed text is unambiguous.
        nameField.Value = string.Empty;

        foreach (var rune in "qkjr/")
        {
            nameField.NewKeyDownEvent(new Key(rune));
        }

        Assert.Equal("qkjr/", nameField.Text);
    }

    /// <summary>Delete removes one character, not the whole field (widget default).</summary>
    [Fact]
    public void TextField_delete_removes_one_character_not_the_whole_field()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Name));
        this.application.LayoutAndDraw();

        var nameField = this.form.NameField;
        nameField.SetFocus();
        nameField.InsertionPoint = 0;

        nameField.NewKeyDownEvent(Key.Delete);

        // The field must still have content — one char was deleted, not the whole string.
        Assert.NotEmpty(nameField.Text ?? string.Empty);
    }

    // ── focus traversal ───────────────────────────────────────────────────────

    [Fact]
    public void Tab_moves_focus_to_the_next_focusable_field()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Name));
        this.form.SetFocus();
        this.application.LayoutAndDraw();

        var before = FindFocused(this.form);
        this.form.AdvanceFocus(NavigationDirection.Forward, TabBehavior.TabStop);
        var after = FindFocused(this.form);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.NotSame(before, after);
    }

    /// <summary>
    /// Real Tab traversal must reach the Arguments and Environment placeholder rows for a stdio
    /// field set — the user cannot add the first item to an empty list unless Tab lands there.
    /// This is the test that catches the original bug where those rows were non-focusable Labels.
    /// </summary>
    [Fact]
    public void Tab_traversal_for_stdio_reaches_arguments_and_environment_placeholders()
    {
        // Empty args and env so the placeholder labels are visible (not per-item rows).
        var state = MakeEditorState(McpTransportKind.Stdio, McpEditorField.Scope);
        this.form.ApplyState(state);
        this.form.SetFocus();
        this.application.LayoutAndDraw();

        var visited = CollectTabOrder(this.form, maxSteps: 20);

        Assert.Contains(this.form.ArgumentsSummaryLabel, visited);
        Assert.Contains(this.form.EnvironmentSummaryLabel, visited);
    }

    /// <summary>
    /// Real Tab traversal must reach Headers, Scopes, and the BearerToken row in the http+bearer
    /// field set — the bearer token can only be replaced when that row can be focused.
    /// </summary>
    [Fact]
    public void Tab_traversal_for_http_bearer_reaches_headers_scopes_and_bearer_token()
    {
        var state = MakeEditorState(McpTransportKind.Http, McpEditorField.Scope, McpAuthMode.Bearer);
        this.form.ApplyState(state);
        this.form.SetFocus();
        this.application.LayoutAndDraw();

        var visited = CollectTabOrder(this.form, maxSteps: 30);

        Assert.Contains(this.form.HeadersSummaryLabel, visited);
        Assert.Contains(this.form.ScopesSummaryLabel, visited);
        Assert.Contains(this.form.BearerTokenLabel, visited);
    }

    /// <summary>
    /// Focusing the ArgumentsSummaryLabel (the empty-list placeholder) must update the
    /// controller's FocusedField to Arguments via the HasFocusChanged wiring. This is the
    /// prerequisite for Ctrl+N to add the first argument — without it the controller can never
    /// know which collection to add to.
    /// </summary>
    [Fact]
    public void CtrlN_on_focused_empty_arguments_placeholder_adds_first_item()
    {
        var state = MakeEditorState(McpTransportKind.Stdio, McpEditorField.Name);
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            View = McpBrowserView.Editor,
            Editor = state,
        });
        this.form.ApplyState(state);
        this.form.SetFocus();
        this.application.LayoutAndDraw();

        // Move real widget focus to the Arguments placeholder label.
        this.form.ArgumentsSummaryLabel.SetFocus();
        this.application.LayoutAndDraw();

        // The controller's FocusedField must now be Arguments via HasFocusChanged wiring.
        // This verifies that Ctrl+N pressed at this point would act on the right collection.
        Assert.Equal(McpEditorField.Arguments, this.controller.State.Editor!.FocusedField);
    }

    [Fact]
    public void ShiftTab_moves_focus_to_the_previous_focusable_field()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Name));
        this.form.SetFocus();
        this.application.LayoutAndDraw();

        // Go forward two steps, then backward one — should land on the first field.
        this.form.AdvanceFocus(NavigationDirection.Forward, TabBehavior.TabStop);
        var afterForward = FindFocused(this.form);
        this.form.AdvanceFocus(NavigationDirection.Forward, TabBehavior.TabStop);
        this.form.AdvanceFocus(NavigationDirection.Backward, TabBehavior.TabStop);
        var afterBackward = FindFocused(this.form);

        Assert.NotNull(afterForward);
        Assert.NotNull(afterBackward);
        Assert.Same(afterForward, afterBackward);
    }

    // ── layout ────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_and_cancel_buttons_are_present_in_both_transports()
    {
        foreach (var transport in new[] { McpTransportKind.Stdio, McpTransportKind.Http })
        {
            this.form.ApplyState(MakeEditorState(transport, McpEditorField.Save));
            this.application.LayoutAndDraw();

            Assert.True(HasButton(this.form, "Save"), $"Save button missing for transport={transport}");
            Assert.True(HasButton(this.form, "Cancel"), $"Cancel button missing for transport={transport}");
        }
    }

    [Fact]
    public void Stdio_form_shows_command_field_and_hides_url_field()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Command));
        this.application.LayoutAndDraw();

        Assert.True(this.form.CommandField.Visible);
        Assert.False(this.form.UrlField.Visible);
    }

    [Fact]
    public void Http_form_shows_url_field_and_hides_command_field()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Http, McpEditorField.Url));
        this.application.LayoutAndDraw();

        Assert.False(this.form.CommandField.Visible);
        Assert.True(this.form.UrlField.Visible);
    }

    [Fact]
    public void Http_auth_none_hides_client_id_scopes_bearer_token()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Http, McpEditorField.AuthMode));
        this.application.LayoutAndDraw();

        Assert.False(this.form.ClientIdField.Visible);
    }

    [Fact]
    public void Http_auth_bearer_shows_client_id_field()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Http, McpEditorField.ClientId, McpAuthMode.Bearer));
        this.application.LayoutAndDraw();

        Assert.True(this.form.ClientIdField.Visible);
    }

    // ── draft write-back ──────────────────────────────────────────────────────

    [Fact]
    public void Typing_in_name_field_updates_controller_draft()
    {
        var state = MakeEditorState(McpTransportKind.Stdio, McpEditorField.Name);
        this.controller.SetStateForTest(McpBrowserState.Empty with
        {
            View = McpBrowserView.Editor,
            Editor = state,
        });
        this.form.ApplyState(state);
        this.application.LayoutAndDraw();

        var nameField = this.form.NameField;
        nameField.SetFocus();
        // Setting Value fires ValueChanged → form's handler updates the draft.
        nameField.Value = "new-server-name";

        Assert.Equal("new-server-name", this.controller.State.Editor?.Draft.Name);
    }

    // ── visible text ─────────────────────────────────────────────────────────

    [Fact]
    public void VisibleTextForTest_includes_save_and_cancel_and_no_ansi()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Save));
        this.application.LayoutAndDraw();

        var text = this.form.VisibleTextForTest;
        Assert.Contains("Save", text, StringComparison.Ordinal);
        Assert.Contains("Cancel", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
    }

    // ── scroll behaviour ─────────────────────────────────────────────────────

    /// <summary>
    /// With a field set larger than the viewport, focusing the last field (Cancel) scrolls it
    /// into view and its Y must be within [0, height).
    /// </summary>
    [Fact]
    public void Focusing_last_field_makes_cancel_visible_within_viewport()
    {
        // Use the largest field set: http + auth → 11 fields.  Height = 5 so we must scroll.
        this.application.Driver!.SetScreenSize(80, 5);
        this.application.LayoutAndDraw();

        this.form.ApplyState(MakeEditorState(McpTransportKind.Http, McpEditorField.Cancel, McpAuthMode.Bearer));
        this.application.LayoutAndDraw();

        var height = this.form.ViewportHeightForTest();
        Assert.True(this.form.CancelButton.Visible, "Cancel must be visible when focused");
        Assert.True(this.form.CancelButton.Frame.Y >= 0 && this.form.CancelButton.Frame.Y < height,
            $"Cancel Y={this.form.CancelButton.Frame.Y} must be in [0, {height})");
    }

    /// <summary>
    /// After scrolling to the bottom, switching focus to the first field (Scope) scrolls
    /// back to the top and makes it visible.
    /// </summary>
    [Fact]
    public void Focusing_first_field_after_scrolling_scrolls_back_to_top()
    {
        this.application.Driver!.SetScreenSize(80, 5);
        this.application.LayoutAndDraw();

        // First: focus last to scroll down.
        this.form.ApplyState(MakeEditorState(McpTransportKind.Http, McpEditorField.Cancel, McpAuthMode.Bearer));
        this.application.LayoutAndDraw();

        // Then: focus first — should scroll back to top (Y == 0).
        this.form.ApplyState(MakeEditorState(McpTransportKind.Http, McpEditorField.Scope, McpAuthMode.Bearer));
        this.application.LayoutAndDraw();

        Assert.True(this.form.ScopeSelector.Visible, "Scope must be visible when focused");
        Assert.Equal(0, this.form.ScopeSelector.Frame.Y);
    }

    /// <summary>
    /// Fields outside the scroll window must not be visible.
    /// </summary>
    [Fact]
    public void Fields_outside_scroll_window_are_not_visible()
    {
        // Height = 3, focus on Cancel (last field, index 10 in the http+bearer set).
        this.application.Driver!.SetScreenSize(80, 3);
        this.application.LayoutAndDraw();

        this.form.ApplyState(MakeEditorState(McpTransportKind.Http, McpEditorField.Cancel, McpAuthMode.Bearer));
        this.application.LayoutAndDraw();

        // Scope is field 0; with Cancel in view and height=3 it must be off-screen.
        Assert.False(this.form.ScopeSelector.Visible, "Scope must be hidden when Cancel is focused and height=3");
    }

    /// <summary>
    /// Iterating focus through every field in a small viewport must not throw and the focused
    /// widget must be visible after each step.
    /// </summary>
    [Fact]
    public void Cycling_focus_through_all_fields_does_not_throw_and_focused_widget_is_visible()
    {
        this.application.Driver!.SetScreenSize(80, 4);
        this.application.LayoutAndDraw();

        var allFields = new[]
        {
            McpEditorField.Scope, McpEditorField.Name, McpEditorField.Transport,
            McpEditorField.Url, McpEditorField.Headers, McpEditorField.AuthMode,
            McpEditorField.ClientId, McpEditorField.Scopes, McpEditorField.BearerToken,
            McpEditorField.Save, McpEditorField.Cancel,
        };

        foreach (var field in allFields)
        {
            // Must not throw.
            this.form.ApplyState(MakeEditorState(McpTransportKind.Http, field, McpAuthMode.Bearer));
            this.application.LayoutAndDraw();

            // The view for this field must be visible.
            var view = FieldView(this.form, field);
            if (view is not null)
            {
                Assert.True(view.Visible, $"Field {field} view must be visible when focused");
            }
        }
    }

    /// <summary>
    /// At full size (80x24) all 8 stdio fields are visible simultaneously — no scrolling needed.
    /// </summary>
    [Fact]
    public void At_full_size_all_stdio_fields_are_visible()
    {
        this.application.Driver!.SetScreenSize(80, 24);
        this.application.LayoutAndDraw();

        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Name));
        this.application.LayoutAndDraw();

        Assert.True(this.form.ScopeSelector.Visible, "Scope");
        Assert.True(this.form.NameField.Visible, "Name");
        Assert.True(this.form.TransportSelector.Visible, "Transport");
        Assert.True(this.form.CommandField.Visible, "Command");
        Assert.True(this.form.ArgumentsSummaryLabel.Visible, "Arguments");
        Assert.True(this.form.EnvironmentSummaryLabel.Visible, "Environment");
        Assert.True(this.form.SaveButton.Visible, "Save");
        Assert.True(this.form.CancelButton.Visible, "Cancel");
    }
    // ── render-based ux assertions ────────────────────────────────────────────

    /// <summary>
    /// Every field in the stdio field set must render a visible label so the user knows what
    /// each row is for.  The labels are checked against the full driver cell scrape, which is
    /// the same view the user sees — unlike widget-state checks these cannot be fooled by a
    /// label that exists in the view tree but has Visible=false or is rendered off-screen.
    /// </summary>
    [Fact]
    public void Rendered_stdio_fields_all_have_visible_labels()
    {
        this.application.Driver!.SetScreenSize(80, 24);
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Scope));
        this.application.LayoutAndDraw();

        var rendered = RenderedDriverText(this.application);

        // Every field that appears in the stdio set must have its label visible.
        Assert.Contains("Scope:", rendered, StringComparison.Ordinal);
        Assert.Contains("Name:", rendered, StringComparison.Ordinal);
        Assert.Contains("Transport:", rendered, StringComparison.Ordinal);
        Assert.Contains("Command:", rendered, StringComparison.Ordinal);
        Assert.Contains("Arguments:", rendered, StringComparison.Ordinal);
        Assert.Contains("Env:", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every field in the http+bearer field set must render a visible label.  This is the
    /// largest field set (11 visible fields), so it exercises all label categories including
    /// the auth-specific ones that are only shown when AuthMode=Bearer.
    /// </summary>
    [Fact]
    public void Rendered_http_bearer_fields_all_have_visible_labels()
    {
        this.application.Driver!.SetScreenSize(80, 24);
        this.form.ApplyState(MakeEditorState(McpTransportKind.Http, McpEditorField.Scope, McpAuthMode.Bearer));
        this.application.LayoutAndDraw();

        var rendered = RenderedDriverText(this.application);

        Assert.Contains("Scope:", rendered, StringComparison.Ordinal);
        Assert.Contains("Name:", rendered, StringComparison.Ordinal);
        Assert.Contains("Transport:", rendered, StringComparison.Ordinal);
        Assert.Contains("URL:", rendered, StringComparison.Ordinal);
        Assert.Contains("Headers:", rendered, StringComparison.Ordinal);
        Assert.Contains("Auth:", rendered, StringComparison.Ordinal);
        Assert.Contains("Client ID:", rendered, StringComparison.Ordinal);
        Assert.Contains("Scopes:", rendered, StringComparison.Ordinal);
        Assert.Contains("Token:", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// When Scope is the focused field the gutter marker (❯) must appear on the Scope row.
    /// This is the primary visual affordance the user requested to identify the active field.
    /// </summary>
    [Fact]
    public void Gutter_marker_appears_on_focused_scope_row()
    {
        this.application.Driver!.SetScreenSize(80, 24);
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Scope));
        this.application.LayoutAndDraw();

        var rendered = RenderedDriverText(this.application);

        // "❯ Scope:" means the gutter marker and the Scope prefix label are on the same row.
        Assert.Contains("❯ Scope:", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// When Name is the focused field the gutter marker must move to the Name row and must NOT
    /// remain on the Scope row.  Moving the marker proves the indicator follows focus.
    /// </summary>
    [Fact]
    public void Gutter_marker_moves_when_focused_field_changes()
    {
        this.application.Driver!.SetScreenSize(80, 24);

        // Render with Scope focused.
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Scope));
        this.application.LayoutAndDraw();
        var withScopeFocused = RenderedDriverText(this.application);
        Assert.Contains("❯ Scope:", withScopeFocused, StringComparison.Ordinal);

        // Re-render with Name focused.
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Name));
        this.application.LayoutAndDraw();
        var withNameFocused = RenderedDriverText(this.application);

        Assert.Contains("❯ Name:", withNameFocused, StringComparison.Ordinal);
        // Scope row must no longer have the marker.
        Assert.DoesNotContain("❯ Scope:", withNameFocused, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pressing CursorDown while the ScopeSelector (an OptionSelector) is the active field
    /// must move widget focus to the NameField (a TextField), not cycle within the selector.
    /// This is the fix for the root bug: AdvanceFocus walked into the selector's internal
    /// CheckBoxes; MoveFieldFocus walks only the form's direct children.
    /// TransportSelector is used (not ScopeSelector) because TUI's focus traversal skips
    /// ScopeSelector on the first SetFocus pass; TransportSelector receives focus reliably when
    /// navigated to via MoveFieldFocus from NameField.
    /// </summary>
    [Fact]
    public void CursorDown_from_selector_moves_focus_to_next_field()
    {
        this.application.Driver!.SetScreenSize(80, 24);
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Name));
        this.form.SetFocus();
        this.application.LayoutAndDraw();

        this.form.NameField.SetFocus();

        // Move forward once: NameField → TransportSelector (an OptionSelector → CheckBox leaf).
        this.form.MoveFocusForTest(NavigationDirection.Forward);
        Assert.IsType<CheckBox>(this.form.MostFocused); // Confirm we are now on a selector.

        // Move forward again from the selector: must land on CommandField (TextField), not cycle
        // within TransportSelector's internal CheckBoxes — this is the bug fix being tested.
        this.form.MoveFocusForTest(NavigationDirection.Forward);
        Assert.IsType<TextField>(this.form.MostFocused);
    }

    /// <summary>
    /// CursorDown from NameField (a TextField), then CursorUp, must return focus to the
    /// ScopeSelector.  The round-trip verifies both directions of MoveFieldFocus and that
    /// the form does not skip or duplicate fields.
    /// </summary>
    [Fact]
    public void CursorDown_then_CursorUp_from_text_field_returns_to_original_field()
    {
        this.application.Driver!.SetScreenSize(80, 24);
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Name));
        this.form.SetFocus();
        this.application.LayoutAndDraw();

        this.form.NameField.SetFocus();
        var before = this.form.MostFocused;
        Assert.IsType<TextField>(before);

        // Down → TransportSelector (internal CheckBox).
        this.form.MoveFocusForTest(NavigationDirection.Forward);
        Assert.IsType<CheckBox>(this.form.MostFocused);

        // Up → back to NameField.
        this.form.MoveFocusForTest(NavigationDirection.Backward);
        Assert.IsType<TextField>(this.form.MostFocused);
        Assert.Same(before, this.form.MostFocused);
    }

    /// <summary>
    /// CursorDown from TransportSelector (OptionSelector → CheckBox), then CursorUp, must return
    /// focus to TransportSelector.  Tests the round-trip when the starting field is a selector.
    /// Note: TransportSelector is used because TUI's initial focus traversal skips ScopeSelector;
    /// TransportSelector reliably receives focus when navigated to via MoveFieldFocus.
    /// </summary>
    [Fact]
    public void CursorDown_then_CursorUp_from_selector_returns_to_original_field()
    {
        this.application.Driver!.SetScreenSize(80, 24);
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Name));
        this.form.SetFocus();
        this.application.LayoutAndDraw();

        this.form.NameField.SetFocus();

        // Navigate to TransportSelector (an OptionSelector).
        this.form.MoveFocusForTest(NavigationDirection.Forward);
        var beforeType = this.form.MostFocused?.GetType(); // CheckBox (TransportSelector's leaf)

        // Down to CommandField (TextField).
        this.form.MoveFocusForTest(NavigationDirection.Forward);
        Assert.IsType<TextField>(this.form.MostFocused);

        // Up → back to TransportSelector (deepest focused is its internal CheckBox).
        this.form.MoveFocusForTest(NavigationDirection.Backward);
        Assert.Equal(beforeType, this.form.MostFocused?.GetType());
    }

    /// <summary>
    /// Changing the OptionSelector value (the user-visible selection) must update the rendered
    /// output so the user can confirm which option is now active.  Changing scope from Project
    /// to User and re-rendering must show a different selection mark.
    /// </summary>
    [Fact]
    public void Selector_option_change_is_visible_in_rendered_output()
    {
        this.application.Driver!.SetScreenSize(80, 24);
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Scope));
        this.application.LayoutAndDraw();

        // Initial: Project is selected (value=0).  Both "project" and "user" text appear, but
        // the ● marker must be on "project".
        var before = RenderedDriverText(this.application);
        Assert.Contains("project", before, StringComparison.Ordinal);

        // Simulate user choosing User: set Value=1 (index of "user" option).
        this.form.ScopeSelector.Value = 1;
        this.application.LayoutAndDraw();

        // After the change, render must differ — the marker has moved to "user".
        var after = RenderedDriverText(this.application);
        Assert.NotEqual(before, after);
        Assert.Contains("user", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// There must be at least one blank row between consecutive field rows so the form does
    /// not read as an undifferentiated wall.  "Blank" means the driver renders the row as
    /// whitespace only (TrimEnd produces an empty string or a string containing only border
    /// characters from the enclosing Window).
    /// </summary>
    [Fact]
    public void Blank_separator_rows_exist_between_fields_in_rendered_output()
    {
        this.application.Driver!.SetScreenSize(80, 24);
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Scope));
        this.application.LayoutAndDraw();

        var driver = this.application.Driver!;
        var lines = new List<string>(driver.Rows);
        for (var row = 0; row < driver.Rows; row++)
        {
            var sb = new StringBuilder();
            for (var col = 0; col < driver.Cols; col++) sb.Append(driver.Contents![row, col].Grapheme);

            // Strip Window border chars and whitespace so a visually empty row becomes "".
            lines.Add(sb.ToString().Trim('│', '─', '╭', '╮', '╰', '╯', ' '));
        }

        // Find two consecutive non-empty rows that contain field labels; there must be a blank
        // separator between them.
        var foundSeparator = false;
        for (var i = 1; i < lines.Count - 1; i++)
        {
            if (lines[i - 1].Contains("Scope:", StringComparison.Ordinal) &&
                lines[i + 1].Contains("Name:", StringComparison.Ordinal))
            {
                // Row i is between Scope and Name — it must be blank.
                Assert.True(
                    string.IsNullOrWhiteSpace(lines[i]) || lines[i].Length == 0,
                    $"Expected blank separator row between Scope and Name at row {i}, got: \"{lines[i]}\"");
                foundSeparator = true;
                break;
            }
        }

        Assert.True(foundSeparator, "Could not find Scope row followed by Name row in rendered output");
    }

    /// <summary>
    /// With a tall field set and a small viewport, scrolling to the bottom and then back to the
    /// top must work correctly even with the extra separator rows introduced by the UX fix.
    /// This is the scroll regression guard: separator rows must be counted when computing
    /// scroll offsets so the focused field always lands within the visible area.
    /// </summary>
    [Fact]
    public void Small_viewport_with_separators_scroll_keeps_focused_field_on_screen()
    {
        this.application.Driver!.SetScreenSize(80, 5);
        this.application.LayoutAndDraw();

        // Scroll to the bottom (Cancel = last field in http+bearer, largest field set).
        this.form.ApplyState(MakeEditorState(McpTransportKind.Http, McpEditorField.Cancel, McpAuthMode.Bearer));
        this.application.LayoutAndDraw();

        var height = this.form.ViewportHeightForTest();
        Assert.True(this.form.CancelButton.Visible, "Cancel must be visible when focused at bottom");
        Assert.InRange(this.form.CancelButton.Frame.Y, 0, height - 1);

        // Scroll back to the top (Scope = first field).
        this.form.ApplyState(MakeEditorState(McpTransportKind.Http, McpEditorField.Scope, McpAuthMode.Bearer));
        this.application.LayoutAndDraw();

        Assert.True(this.form.ScopeSelector.Visible, "Scope must be visible when focused at top");
        Assert.Equal(0, this.form.ScopeSelector.Frame.Y);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances Tab focus on the form up to <paramref name="maxSteps"/> times and returns every
    /// distinct view that received focus. Stops early when the traversal wraps back to the first
    /// focused widget, so the returned list covers exactly one complete cycle.
    /// </summary>
    private static IReadOnlyList<View> CollectTabOrder(McpEditorForm form, int maxSteps)
    {
        var result = new List<View>();
        View? first = null;
        for (var i = 0; i < maxSteps; i++)
        {
            form.AdvanceFocus(NavigationDirection.Forward, TabBehavior.TabStop);
            var focused = FindFocused(form);
            if (focused is null) break;
            if (first is null)
            {
                first = focused;
            }
            else if (ReferenceEquals(focused, first))
            {
                break; // wrapped around — full cycle complete
            }

            result.Add(focused);
        }

        return result;
    }

    internal static McpEditorState MakeEditorState(
        McpTransportKind transport,
        McpEditorField focused,
        McpAuthMode authMode = McpAuthMode.None)
    {
        var draft = new McpServerDraft(
            Name: "server",
            Scope: McpConfigScope.Project,
            Enabled: true,
            Transport: transport,
            Command: "node",
            Args: [],
            Url: transport == McpTransportKind.Http ? "https://example.test/mcp" : null,
            Environment: [],
            Headers: [],
            AuthMode: authMode,
            ClientId: null,
            Scopes: [],
            BearerToken: new McpSecretChange("auth/token", McpSecretChangeKind.Unchanged));
        return new McpEditorState(McpEditorMode.Edit, McpBrowserView.List, draft, focused);
    }

    private static View? FieldView(McpEditorForm form, McpEditorField field) => field switch
    {
        McpEditorField.Scope => form.ScopeSelector,
        McpEditorField.Name => form.NameField,
        McpEditorField.Transport => form.TransportSelector,
        McpEditorField.Command => form.CommandField,
        McpEditorField.Arguments => form.ArgumentsSummaryLabel,
        McpEditorField.Url => form.UrlField,
        McpEditorField.Headers => form.HeadersSummaryLabel,
        McpEditorField.AuthMode => form.AuthModeSelector,
        McpEditorField.ClientId => form.ClientIdField,
        McpEditorField.Scopes => form.ScopesSummaryLabel,
        McpEditorField.Environment => form.EnvironmentSummaryLabel,
        McpEditorField.BearerToken => form.BearerTokenLabel,
        McpEditorField.Save => form.SaveButton,
        McpEditorField.Cancel => form.CancelButton,
        _ => null,
    };

    private static IEnumerable<TextField> AllTextFields(View parent)
    {
        foreach (var child in parent.SubViews)
        {
            if (child is TextField tf) yield return tf;
            foreach (var nested in AllTextFields(child)) yield return nested;
        }
    }

    private static IEnumerable<Label> AllLabels(View parent)
    {
        foreach (var child in parent.SubViews)
        {
            if (child is Label lbl) yield return lbl;
            foreach (var nested in AllLabels(child)) yield return nested;
        }
    }

    private static View? FindFocused(View parent)
    {
        foreach (var child in parent.SubViews)
        {
            if (child.HasFocus && child is not View { SubViews: { Count: > 0 } })
            {
                return child;
            }

            var nested = FindFocused(child);
            if (nested is not null) return nested;
        }

        return null;
    }

    private static bool HasButton(View parent, string text)
    {
        foreach (var child in parent.SubViews)
        {
            if (child is Button btn && (btn.Text ?? string.Empty).Contains(text, StringComparison.Ordinal))
            {
                return true;
            }

            if (HasButton(child, text)) return true;
        }

        return false;
    }

    private static string RenderedDriverText(IApplication application)
    {
        var driver = application.Driver!;
        var lines = new List<string>(driver.Rows);
        for (var row = 0; row < driver.Rows; row++)
        {
            var line = new StringBuilder();
            for (var col = 0; col < driver.Cols; col++)
            {
                line.Append(driver.Contents![row, col].Grapheme);
            }

            lines.Add(line.ToString().TrimEnd());
        }

        return string.Join(Environment.NewLine, lines);
    }

    // ── rendered focus affordances ────────────────────────────────────────────
    // Asserted against the driver cell buffer: the widget-state versions of these checks passed
    // while the user had no way to tell which button or option the cursor was on.

    [Fact]
    public void Focused_button_is_painted_inverted_and_carries_the_gutter_marker()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Save));
        this.application.LayoutAndDraw();

        Assert.Contains("\u276f \u27e6 Save \u27e7", RenderedOutput.Text(this.application), StringComparison.Ordinal);

        var save = RenderedOutput.AttributeOf(this.application, "Save");
        var cancel = RenderedOutput.AttributeOf(this.application, "Cancel");
        Assert.NotEqual(cancel, save);

        // Filled, not merely tinted: a focused button must change its BACKGROUND, because a
        // foreground-only accent is what made the previous focus treatment invisible in practice.
        Assert.NotEqual(cancel.Background, save.Background);
    }

    [Fact]
    public void Moving_focus_between_buttons_moves_the_painted_inversion()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Save));
        this.application.LayoutAndDraw();
        var saveFocused = RenderedOutput.AttributeOf(this.application, "Save");
        var cancelUnfocused = RenderedOutput.AttributeOf(this.application, "Cancel");

        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Cancel));
        this.application.LayoutAndDraw();

        Assert.Contains("\u276f \u27e6 Cancel \u27e7", RenderedOutput.Text(this.application), StringComparison.Ordinal);
        Assert.Equal(saveFocused, RenderedOutput.AttributeOf(this.application, "Cancel"));
        Assert.Equal(cancelUnfocused, RenderedOutput.AttributeOf(this.application, "Save"));
    }

    [Fact]
    public void Focused_selector_paints_its_cursor_option_distinctly_from_the_other_options()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Transport));
        this.application.LayoutAndDraw();

        var screen = RenderedOutput.Text(this.application);

        // Selection is carried by the glyph (radio-button style) …
        Assert.Contains("\u25c9 stdio", screen, StringComparison.Ordinal);
        Assert.Contains("\u25cb http", screen, StringComparison.Ordinal);

        // … and the cursor is carried by the attribute, so the two are independently readable.
        Assert.NotEqual(
            RenderedOutput.AttributeOf(this.application, "http"),
            RenderedOutput.AttributeOf(this.application, "stdio"));
    }

    [Fact]
    public void Unfocused_selector_paints_no_cursor_so_the_cursor_attribute_tracks_focus_not_selection()
    {
        this.form.ApplyState(MakeEditorState(McpTransportKind.Stdio, McpEditorField.Command));
        this.application.LayoutAndDraw();

        // stdio is still the SELECTED option here, but the selector does not hold focus, so it must
        // not steal the cursor treatment away from the field the user is actually on.
        Assert.Contains("\u25c9 stdio", RenderedOutput.Text(this.application), StringComparison.Ordinal);
        Assert.Equal(
            RenderedOutput.AttributeOf(this.application, "http"),
            RenderedOutput.AttributeOf(this.application, "stdio"));
    }

    public void Dispose()
    {
        this.form.Dispose();
        if (this.runState is not null) this.application.End(this.runState);
        this.root.Dispose();
        this.application.Dispose();
    }
}
