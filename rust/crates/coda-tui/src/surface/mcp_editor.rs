//! The MCP server editor.
//!
//! Ports the C# `McpEditorForm`. The field set is driven by the draft's
//! transport rather than being fixed: a stdio server has a command and
//! arguments, an HTTP one has a URL, and showing both would offer fields that
//! the loader discards on save — the user would type something and watch it
//! vanish.
//!
//! For the same reason there are no OAuth fields. The config model does not
//! round-trip them, so offering them would be a promise the save cannot keep.

use super::form::{form_cursor, render_form};
use super::{Surface, SurfaceAction, SurfaceOutcome};
use crate::config::{McpDraft, McpServerId, Scope};
use crate::render::glyphs;
use crate::widgets::{Form, FormOutcome, RadioGroup, Select, StaticText, Switch, TextArea, TextInput};
use coda_render::theme::{Role, Theme};
use crossterm::event::KeyEvent;
use ratatui::layout::Rect;
use ratatui::text::Line;

pub const SCOPES: &[&str] = &["user", "project"];
pub const TRANSPORTS: &[&str] = &["stdio", "http"];

/// Control positions. The trailing fields differ by transport, so only the
/// shared prefix has fixed indices.
mod index {
    pub const SCOPE: usize = 1;
    pub const NAME: usize = 2;
    pub const TRANSPORT: usize = 3;
    /// Command for stdio, URL for http.
    pub const TARGET: usize = 4;
    /// Arguments (JSON array) for stdio only — index 5.
    pub const ARGS: usize = 5;
    // ENV and ENABLED depend on transport — use McpEditorSurface helpers.
}

pub struct McpEditorSurface {
    form: Form,
    /// The immutable identity the editor opened on — scope *and* name — so a
    /// save knows exactly which on-disk entry it is replacing, even when the
    /// user changes the scope or name in the form. `None` for a new server.
    original: Option<McpServerId>,
    /// The transport the current form was built for, so a change rebuilds it.
    built_for: String,
    error: Option<String>,
    /// Preserved across transport toggles so switching back restores the value.
    cached_command: String,
    cached_args: String,
    cached_url: String,
}

impl McpEditorSurface {
    pub fn new(draft: McpDraft, original: Option<McpServerId>) -> Self {
        let built_for = draft.transport.clone();
        let cached_command = draft.command.clone();
        let cached_args = draft.args.clone();
        let cached_url = draft.url.clone();
        Self {
            form: build(&draft),
            original,
            built_for,
            error: None,
            cached_command,
            cached_args,
            cached_url,
        }
    }

    /// An editor for a server that does not exist yet.
    pub fn creating() -> Self {
        Self::new(McpDraft::new(), None)
    }

    /// An editor for an existing server, capturing the scope and name it was
    /// loaded from as its immutable identity.
    pub fn editing(draft: McpDraft) -> Self {
        let id = McpServerId::new(draft.scope, draft.name.clone());
        Self::new(draft, Some(id))
    }

    pub fn original(&self) -> Option<&McpServerId> {
        self.original.as_ref()
    }

    pub fn error(&self) -> Option<&str> {
        self.error.as_deref()
    }

    fn text_at(&self, at: usize) -> String {
        self.form
            .control(at)
            .and_then(|c| c.as_any().downcast_ref::<TextInput>())
            .map(TextInput::value)
            .unwrap_or_default()
    }

    fn textarea_at(&self, at: usize) -> String {
        self.form
            .control(at)
            .and_then(|c| c.as_any().downcast_ref::<TextArea>())
            .map(TextArea::value)
            .unwrap_or_default()
    }

    fn choice_at(&self, at: usize, options: &[&str]) -> String {
        let index = self
            .form
            .control(at)
            .and_then(|c| {
                c.as_any()
                    .downcast_ref::<Select>()
                    .map(Select::selected_index)
                    .or_else(|| {
                        c.as_any()
                            .downcast_ref::<RadioGroup>()
                            .map(RadioGroup::selected_index)
                    })
            })
            .unwrap_or(0);
        options.get(index).copied().unwrap_or(options[0]).to_string()
    }

    fn switch_at(&self, at: usize) -> bool {
        self.form
            .control(at)
            .and_then(|c| c.as_any().downcast_ref::<Switch>())
            .map(Switch::is_on)
            .unwrap_or(true)
    }

    /// Index of the Environment TextArea — depends on transport.
    fn env_index(&self) -> usize {
        if self.built_for == "http" { 5 } else { 6 }
    }

    /// Index of the Enabled Switch — depends on transport.
    fn enabled_index(&self) -> usize {
        if self.built_for == "http" { 6 } else { 7 }
    }

    /// The draft the form currently describes.
    pub fn draft(&self) -> McpDraft {
        let transport = self.choice_at(index::TRANSPORT, TRANSPORTS);
        let http = transport == "http";
        McpDraft {
            name: self.text_at(index::NAME).trim().to_string(),
            scope: if self.choice_at(index::SCOPE, SCOPES) == "project" {
                Scope::Project
            } else {
                Scope::User
            },
            command: if http { String::new() } else { self.text_at(index::TARGET) },
            args: if http { String::new() } else { self.textarea_at(index::ARGS) },
            url: if http { self.text_at(index::TARGET) } else { String::new() },
            env: self.textarea_at(self.env_index()),
            enabled: self.switch_at(self.enabled_index()),
            transport,
        }
    }

    /// Rebuilds the form when the transport changed, preserving what was typed.
    ///
    /// Without this the field set would describe a transport the user has
    /// moved away from: an HTTP server would still be asking for a command.
    fn rebuild_if_transport_changed(&mut self) {
        let now = self.choice_at(index::TRANSPORT, TRANSPORTS);
        if now == self.built_for {
            return;
        }
        // Flush the current transport's editable fields into the cache before
        // rebuilding.  The new form has a different set of controls, so values
        // typed in the old layout would otherwise be discarded silently.
        let was_http = self.built_for == "http";
        if was_http {
            self.cached_url = self.text_at(index::TARGET);
        } else {
            self.cached_command = self.text_at(index::TARGET);
            self.cached_args = self.textarea_at(index::ARGS);
        }

        let draft = McpDraft {
            name: self.text_at(index::NAME).trim().to_string(),
            scope: if self.choice_at(index::SCOPE, SCOPES) == "project" {
                Scope::Project
            } else {
                Scope::User
            },
            transport: now.clone(),
            command: self.cached_command.clone(),
            args: self.cached_args.clone(),
            url: self.cached_url.clone(),
            env: self.textarea_at(self.env_index()),
            enabled: self.switch_at(self.enabled_index()),
        };
        self.built_for = now;
        self.form = build(&draft);
        // Re-focus the transport control so the user can keep changing it
        // without having to navigate back to it after each toggle.
        while self.form.focused_index() != index::TRANSPORT {
            self.form.focus_next();
        }
    }
}

fn build(draft: &McpDraft) -> Form {
    let http = draft.transport == "http";
    let mut controls: Vec<Box<dyn crate::widgets::Control>> = vec![
        Box::new(
            StaticText::new("Servers are stored in .mcp.json for the chosen scope.")
                .with_role(Role::Notification),
        ),
        Box::new(
            RadioGroup::new("Scope", SCOPES.iter().map(|s| s.to_string()).collect())
                .with_selected(usize::from(draft.scope == Scope::Project)),
        ),
        Box::new(
            TextInput::new("Name")
                .with_placeholder("my-server")
                .with_value(draft.name.clone()),
        ),
        Box::new(
            Select::new(
                "Transport",
                TRANSPORTS.iter().map(|s| s.to_string()).collect(),
            )
            .with_selected(usize::from(http)),
        ),
    ];

    if http {
        controls.push(Box::new(
            TextInput::new("URL")
                .with_placeholder("https://example.com/mcp")
                .with_value(draft.url.clone()),
        ));
    } else {
        controls.push(Box::new(
            TextInput::new("Command")
                .with_placeholder("npx")
                .with_value(draft.command.clone()),
        ));
        // index::ARGS = 5 (stdio only)
        controls.push(Box::new(
            TextArea::new("Arguments  (JSON array, e.g. [\"-y\",\"server\"])")
                .with_visible_rows(2)
                .with_value(draft.args.clone()),
        ));
    }

    // Environment TextArea — index 6 for stdio, 5 for http.
    controls.push(Box::new(
        TextArea::new("Environment  (JSON object, e.g. {\"KEY\":\"value\"})")
            .with_visible_rows(3)
            .with_value(draft.env.clone()),
    ));

    controls.push(Box::new(Switch::new("Enabled").with_value(draft.enabled)));
    Form::new(controls)
}

impl Surface for McpEditorSurface {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn title(&self) -> String {
        match &self.original {
            Some(id) => format!("Edit MCP server: {}", id.name),
            None => "Add MCP server".to_string(),
        }
    }

    fn hints(&self) -> String {
        match &self.error {
            // The reason it will not save outranks the key list: the user has
            // just pressed Enter and needs to know why nothing happened.
            Some(problem) => problem.clone(),
            None => format!(
                "Tab: next    {}: change    Enter: save (Ctrl+Enter in a text box)    Esc: cancel",
                glyphs::ARROWS_VERTICAL
            ),
        }
    }

    fn placement(&self) -> super::Placement {
        super::Placement::FitContent { preferred_width: 72 }
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        let outcome = self.form.handle_key(key);
        self.rebuild_if_transport_changed();

        match outcome {
            FormOutcome::Consumed => {
                // Editing anything clears a stale complaint, so the hint line
                // does not keep accusing the user after they have fixed it.
                self.error = None;
                SurfaceOutcome::Handled
            }
            FormOutcome::Ignored => SurfaceOutcome::Ignored,
            FormOutcome::Cancel => SurfaceOutcome::Close,
            FormOutcome::Submit => match self.draft().validation_error() {
                // Refused in the surface rather than at the write, so the
                // modal stays open with the field still on screen.
                Some(problem) => {
                    self.error = Some(problem);
                    SurfaceOutcome::Handled
                }
                None => SurfaceOutcome::Emit(SurfaceAction::SaveMcpServer),
            },
        }
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        render_form(&self.form, area, theme)
    }

    fn cursor(&self, area: Rect, theme: &Theme) -> Option<(u16, u16)> {
        form_cursor(&self.form, area, theme)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::{KeyCode, KeyModifiers};

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn stdio_draft() -> McpDraft {
        McpDraft {
            name: "everything".into(),
            scope: Scope::User,
            transport: "stdio".into(),
            command: "npx".into(),
            args: "[\"-y\",\"server\"]".into(),
            url: String::new(),
            enabled: true,
            env: String::new(),
        }
    }

    #[test]
    fn it_opens_on_the_existing_server_not_on_defaults() {
        let surface = McpEditorSurface::editing(stdio_draft());
        let draft = surface.draft();
        assert_eq!(draft.name, "everything");
        assert_eq!(draft.command, "npx");
        assert_eq!(draft.args, "[\"-y\",\"server\"]");
        let id = surface.original().expect("identity captured");
        assert_eq!(id.name, "everything");
        assert_eq!(id.scope, Scope::User);
    }

    #[test]
    fn switching_transport_replaces_the_fields() {
        // An HTTP server must not still be asking for a command: the loader
        // would discard whatever was typed there.
        let mut surface = McpEditorSurface::editing(stdio_draft());
        let rendered = |s: &McpEditorSurface| -> String {
            s.render(Rect::new(0, 0, 60, 30), &Theme::default())
                .iter()
                .flat_map(|l| l.spans.iter().map(|sp| sp.content.to_string()))
                .collect()
        };
        assert!(rendered(&surface).contains("Command"));

        // Tab to Transport, then change it.
        while surface.form.focused_index() != index::TRANSPORT {
            surface.handle_key(key(KeyCode::Tab));
        }
        surface.handle_key(key(KeyCode::Down));

        let text = rendered(&surface);
        assert!(text.contains("URL"), "the URL field never appeared: {text:?}");
        assert!(!text.contains("Command"), "the command field survived: {text:?}");
    }

    #[test]
    fn switching_transport_keeps_what_was_already_typed() {
        let mut surface = McpEditorSurface::editing(stdio_draft());
        while surface.form.focused_index() != index::TRANSPORT {
            surface.handle_key(key(KeyCode::Tab));
        }
        surface.handle_key(key(KeyCode::Down));
        assert_eq!(surface.draft().name, "everything", "the name was lost");
    }

    #[test]
    fn saving_an_incomplete_server_explains_itself_and_stays_open() {
        let mut surface = McpEditorSurface::creating();
        assert!(matches!(
            surface.handle_key(key(KeyCode::Enter)),
            SurfaceOutcome::Handled
        ));
        assert!(surface.error().is_some(), "no reason was given");
        assert!(surface.hints().contains("name"), "the hint hid the reason");
    }

    #[test]
    fn editing_clears_a_stale_complaint() {
        let mut surface = McpEditorSurface::creating();
        surface.handle_key(key(KeyCode::Enter));
        assert!(surface.error().is_some());
        surface.handle_key(key(KeyCode::Tab));
        assert!(surface.error().is_none(), "the complaint outlived the fix");
    }

    #[test]
    fn a_complete_server_asks_the_host_to_save_it() {
        let mut surface = McpEditorSurface::editing(stdio_draft());
        assert!(matches!(
            surface.handle_key(key(KeyCode::Enter)),
            SurfaceOutcome::Emit(SurfaceAction::SaveMcpServer)
        ));
    }

    #[test]
    fn escape_closes_without_saving() {
        let mut surface = McpEditorSurface::editing(stdio_draft());
        assert!(matches!(
            surface.handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Close
        ));
    }

    #[test]
    fn it_never_renders_more_lines_than_the_area_allows() {
        let surface = McpEditorSurface::editing(stdio_draft());
        for height in [1, 3, 8, 40] {
            let area = Rect::new(0, 0, 60, height);
            assert!(surface.render(area, &Theme::default()).len() <= height as usize);
        }
    }

    // -- Transport toggle preservation tests ---------------------------------

    #[test]
    fn switching_to_http_and_back_preserves_args() {
        // Switching transports used to call draft() before capturing the OLD
        // transport's fields; since draft() reads based on the NEW transport,
        // args were silently cleared when going stdio → http → stdio.
        let mut surface = McpEditorSurface::editing(stdio_draft());
        let initial_args = surface.draft().args.clone();

        // Switch to http
        while surface.form.focused_index() != index::TRANSPORT {
            surface.handle_key(key(KeyCode::Tab));
        }
        surface.handle_key(key(KeyCode::Down)); // stdio → http

        // Switch back to stdio
        surface.handle_key(key(KeyCode::Up)); // http → stdio

        assert_eq!(
            surface.draft().args,
            initial_args,
            "args were lost across a transport round-trip"
        );
    }

    #[test]
    fn switching_transport_preserves_command() {
        let mut surface = McpEditorSurface::editing(stdio_draft());
        let initial_command = surface.draft().command.clone();

        while surface.form.focused_index() != index::TRANSPORT {
            surface.handle_key(key(KeyCode::Tab));
        }
        surface.handle_key(key(KeyCode::Down)); // → http

        // The name must still be intact after the rebuild.
        assert_eq!(surface.draft().name, "everything");

        surface.handle_key(key(KeyCode::Up)); // → stdio again
        assert_eq!(
            surface.draft().command,
            initial_command,
            "command was lost across a transport round-trip"
        );
    }

    #[test]
    fn typing_args_in_the_textarea_appends_not_replaces() {
        // The TextArea must accumulate keystrokes rather than replacing the
        // pre-loaded value on the first keystroke.
        let mut surface = McpEditorSurface::editing(stdio_draft());
        let initial_args = surface.draft().args.clone();

        // Navigate to the Arguments field.
        while surface.form.focused_index() != index::ARGS {
            surface.handle_key(key(KeyCode::Tab));
        }
        // Type some extra chars at the end.
        surface.handle_key(key(KeyCode::Char('!')));
        surface.handle_key(key(KeyCode::Char('?')));

        let new_args = surface.draft().args.clone();
        assert!(
            new_args.starts_with(&initial_args[..initial_args.len().saturating_sub(1)]),
            "typing overwrote the existing args: initial={initial_args:?} new={new_args:?}"
        );
        assert!(
            new_args.len() > initial_args.len(),
            "typing did not append: initial={initial_args:?} new={new_args:?}"
        );
    }

    #[test]
    fn env_textarea_is_present_and_editable() {
        let draft_with_env = McpDraft {
            env: "{\n  \"FOO\": \"bar\",\n  \"BAZ\": \"qux\"\n}".into(),
            ..stdio_draft()
        };
        let mut surface = McpEditorSurface::editing(draft_with_env);
        assert!(
            surface.draft().env.contains("FOO"),
            "env was not loaded into the form"
        );

        // Navigate to env field (index 6 for stdio) and type.
        while surface.form.focused_index() != surface.env_index() {
            surface.handle_key(key(KeyCode::Tab));
        }
        surface.handle_key(key(KeyCode::Char('X')));

        // The env textarea now has extra text; draft should reflect it.
        assert!(
            !surface.draft().env.is_empty(),
            "env was lost after typing"
        );
    }

    #[test]
    fn ctrl_enter_saves_from_a_multiline_field() {
        // Enter inside a TextArea inserts a newline, so a user parked in the
        // Environment box needs Ctrl+Enter (or Tab out) to save. Confirm the
        // chord submits from within that field.
        let mut surface = McpEditorSurface::editing(stdio_draft());
        while surface.form.focused_index() != surface.env_index() {
            surface.handle_key(key(KeyCode::Tab));
        }
        let ctrl_enter = KeyEvent::new(KeyCode::Enter, KeyModifiers::CONTROL);
        assert!(matches!(
            surface.handle_key(ctrl_enter),
            SurfaceOutcome::Emit(SurfaceAction::SaveMcpServer)
        ));
    }

    #[test]
    fn invalid_args_json_blocks_save() {
        let bad_args = McpDraft {
            args: "not-json-array".into(),
            ..stdio_draft()
        };
        let mut surface = McpEditorSurface::editing(bad_args);
        assert!(matches!(
            surface.handle_key(key(KeyCode::Enter)),
            SurfaceOutcome::Handled
        ));
        assert!(
            surface.error().is_some(),
            "invalid JSON args did not produce an error"
        );
    }

    #[test]
    fn invalid_env_blocks_save() {
        let bad_env = McpDraft {
            env: "{ not valid json".into(),
            ..stdio_draft()
        };
        let mut surface = McpEditorSurface::editing(bad_env);
        assert!(matches!(
            surface.handle_key(key(KeyCode::Enter)),
            SurfaceOutcome::Handled
        ));
        assert!(
            surface.error().is_some(),
            "invalid env JSON did not produce an error"
        );
    }
}
