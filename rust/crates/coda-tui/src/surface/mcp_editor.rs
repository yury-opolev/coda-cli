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
use crate::config::{McpDraft, Scope};
use crate::render::glyphs;
use crate::widgets::{Form, FormOutcome, RadioGroup, Select, StaticText, Switch, TextInput};
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
    /// Arguments for stdio; the enabled switch for http.
    pub const ARGS_OR_ENABLED: usize = 5;
}

pub struct McpEditorSurface {
    form: Form,
    /// The name this editor opened on, so a rename can retire the old entry.
    original_name: Option<String>,
    /// The transport the current form was built for, so a change rebuilds it.
    built_for: String,
    error: Option<String>,
}

impl McpEditorSurface {
    pub fn new(draft: McpDraft, original_name: Option<String>) -> Self {
        let built_for = draft.transport.clone();
        Self {
            form: build(&draft),
            original_name,
            built_for,
            error: None,
        }
    }

    /// An editor for a server that does not exist yet.
    pub fn creating() -> Self {
        Self::new(McpDraft::new(), None)
    }

    /// An editor for an existing server.
    pub fn editing(draft: McpDraft) -> Self {
        let name = draft.name.clone();
        Self::new(draft, Some(name))
    }

    pub fn original_name(&self) -> Option<&str> {
        self.original_name.as_deref()
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
            command: if http {
                String::new()
            } else {
                self.text_at(index::TARGET)
            },
            args: if http {
                String::new()
            } else {
                self.text_at(index::ARGS_OR_ENABLED)
            },
            url: if http {
                self.text_at(index::TARGET)
            } else {
                String::new()
            },
            enabled: self.switch_at(if http {
                index::ARGS_OR_ENABLED
            } else {
                index::ARGS_OR_ENABLED + 1
            }),
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
        let draft = self.draft();
        self.built_for = now;
        self.form = build(&draft);
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
        controls.push(Box::new(
            TextInput::new("Arguments")
                .with_placeholder("-y @modelcontextprotocol/server-everything")
                .with_value(draft.args.clone()),
        ));
    }

    controls.push(Box::new(Switch::new("Enabled").with_value(draft.enabled)));
    Form::new(controls)
}

impl Surface for McpEditorSurface {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn title(&self) -> String {
        match &self.original_name {
            Some(name) => format!("Edit MCP server: {name}"),
            None => "Add MCP server".to_string(),
        }
    }

    fn hints(&self) -> String {
        match &self.error {
            // The reason it will not save outranks the key list: the user has
            // just pressed Enter and needs to know why nothing happened.
            Some(problem) => problem.clone(),
            None => format!(
                "Tab: next    {}: change    Enter: save    Esc: cancel",
                glyphs::ARROWS_VERTICAL
            ),
        }
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
            args: "-y server".into(),
            url: String::new(),
            enabled: true,
        }
    }

    #[test]
    fn it_opens_on_the_existing_server_not_on_defaults() {
        let surface = McpEditorSurface::editing(stdio_draft());
        let draft = surface.draft();
        assert_eq!(draft.name, "everything");
        assert_eq!(draft.command, "npx");
        assert_eq!(draft.args, "-y server");
        assert_eq!(surface.original_name(), Some("everything"));
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
}
