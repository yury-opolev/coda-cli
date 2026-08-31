//! Adapts a [`Form`] to the [`Surface`] contract.
//!
//! Focus, key routing and scrolling already live in `Form`. This is the thin
//! layer that gives it a title, hints and an outcome, so a form-shaped surface
//! needs no key handling of its own.
//!
//! Scrolling keys off the focused control's row range rather than the caret. A
//! switch and a radio group have no caret, so a caret-based scroll would leave
//! them off screen exactly when the user tabbed to them.

use super::{Surface, SurfaceAction, SurfaceOutcome};
use crate::widgets::{Form, FormOutcome};
use coda_render::theme::Theme;
use crossterm::event::KeyEvent;
use ratatui::layout::Rect;
use ratatui::text::Line;

/// How far a form must scroll to keep its focused control visible.
pub(crate) fn scroll_for(form: &Form, area: Rect, theme: &Theme) -> u16 {
    let (_, focus_end) = form.focused_rows(area.width, theme);
    focus_end.saturating_sub(area.height)
}

/// Renders a form into `area`, scrolled and clipped to fit.
pub(crate) fn render_form(form: &Form, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
    let mut lines = form.render(area.width, theme);
    let scroll = scroll_for(form, area, theme) as usize;
    if scroll > 0 {
        lines.drain(..scroll.min(lines.len()));
    }
    lines.truncate(area.height as usize);
    lines
}

/// The caret for a form, in coordinates relative to `area`.
pub(crate) fn form_cursor(form: &Form, area: Rect, theme: &Theme) -> Option<(u16, u16)> {
    let (column, row) = form.cursor(area.width, theme)?;
    let row = row.checked_sub(scroll_for(form, area, theme))?;
    (row < area.height).then_some((column, row))
}

/// A form with a title, hints, and an action to emit on submit.
pub struct FormSurface {
    form: Form,
    title: String,
    hints: String,
    on_submit: Option<SurfaceAction>,
}

impl FormSurface {
    pub fn new(title: impl Into<String>, hints: impl Into<String>, form: Form) -> Self {
        Self {
            form,
            title: title.into(),
            hints: hints.into(),
            on_submit: None,
        }
    }

    /// Sets what submitting the form asks the application to do.
    ///
    /// Without one, submitting merely closes: a form with nowhere to send its
    /// values should not pretend to have saved them.
    pub fn on_submit(mut self, action: SurfaceAction) -> Self {
        self.on_submit = Some(action);
        self
    }

    pub fn form(&self) -> &Form {
        &self.form
    }
}

impl Surface for FormSurface {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }

    fn title(&self) -> String {
        self.title.clone()
    }

    fn hints(&self) -> String {
        self.hints.clone()
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        match self.form.handle_key(key) {
            FormOutcome::Consumed => SurfaceOutcome::Handled,
            FormOutcome::Ignored => SurfaceOutcome::Ignored,
            FormOutcome::Cancel => SurfaceOutcome::Close,
            FormOutcome::Submit => match self.on_submit.clone() {
                Some(action) => SurfaceOutcome::Emit(action),
                None => SurfaceOutcome::Close,
            },
        }
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        render_form(&self.form, area, theme)
    }

    fn cursor(&self, area: Rect) -> Option<(u16, u16)> {
        form_cursor(&self.form, area, &Theme::default())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::widgets::{StaticText, Switch, TextInput};
    use crossterm::event::{KeyCode, KeyModifiers};

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn form() -> Form {
        Form::new(vec![
            Box::new(StaticText::new("Heading")),
            Box::new(TextInput::new("Name")),
            Box::new(Switch::new("Telemetry")),
        ])
    }

    #[test]
    fn submitting_emits_the_configured_action() {
        let mut surface =
            FormSurface::new("T", "h", form()).on_submit(SurfaceAction::SaveSettings);
        assert!(matches!(
            surface.handle_key(key(KeyCode::Enter)),
            SurfaceOutcome::Emit(SurfaceAction::SaveSettings)
        ));
    }

    #[test]
    fn submitting_without_an_action_merely_closes() {
        // A form with nowhere to send its values must not look like it saved.
        let mut surface = FormSurface::new("T", "h", form());
        assert!(matches!(
            surface.handle_key(key(KeyCode::Enter)),
            SurfaceOutcome::Close
        ));
    }

    #[test]
    fn escape_closes() {
        let mut surface = FormSurface::new("T", "h", form());
        assert!(matches!(
            surface.handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Close
        ));
    }

    #[test]
    fn tab_is_consumed_by_the_form() {
        let mut surface = FormSurface::new("T", "h", form());
        assert!(matches!(
            surface.handle_key(key(KeyCode::Tab)),
            SurfaceOutcome::Handled
        ));
    }

    #[test]
    fn it_never_renders_more_lines_than_the_area_allows() {
        let surface = FormSurface::new("T", "h", form());
        for height in [1, 2, 3, 8, 40] {
            let area = Rect::new(0, 0, 60, height);
            assert!(
                surface.render(area, &Theme::default()).len() <= height as usize,
                "overflowed a {height}-row area"
            );
        }
    }

    #[test]
    fn it_scrolls_to_keep_a_caretless_focused_control_visible() {
        // The switch is last and has no caret. A caret-based scroll would
        // leave it off screen exactly when focused.
        let mut surface = FormSurface::new("T", "h", form());
        while surface.form().focused_index() + 1 < surface.form().len() {
            surface.handle_key(key(KeyCode::Tab));
        }
        let area = Rect::new(0, 0, 60, 3);
        let theme = Theme::default();
        let text: String = surface
            .render(area, &theme)
            .iter()
            .flat_map(|l| l.spans.iter().map(|s| s.content.to_string()))
            .collect();
        assert!(
            text.contains("Telemetry"),
            "the focused switch was scrolled out of sight: {text:?}"
        );
    }

    #[test]
    fn the_caret_stays_inside_the_area() {
        let surface = FormSurface::new("T", "h", form());
        for height in [1, 2, 3, 40] {
            let area = Rect::new(0, 0, 60, height);
            if let Some((_, row)) = surface.cursor(area) {
                assert!(row < height, "caret at row {row} escaped a {height}-row area");
            }
        }
    }
}
