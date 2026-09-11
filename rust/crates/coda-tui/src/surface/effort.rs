//! Model-aware effort selection. No setting changes until Enter or `s`.

use super::{Placement, Surface, SurfaceAction, SurfaceOutcome};
use crate::render::glyphs;
use coda_render::theme::{Role, Theme};
use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};
use ratatui::{layout::Rect, text::{Line, Span}};

pub const LEVELS: &[&str] = &["low", "medium", "high", "xhigh", "max"];
pub const PREFERRED_WIDTH: u16 = 58;
const SIDE_PADDING: usize = 2;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PickerCapability {
    Unsupported,
    Indeterminate,
    Levels(Vec<String>),
}

pub struct EffortPickerSurface {
    levels: Vec<String>,
    selected: Option<usize>,
    capability: PickerCapability,
    supports_auto: bool,
    for_model: (String, String),
    display_label: Option<String>,
}

impl EffortPickerSurface {
    pub fn new(
        current: Option<&str>,
        capability: PickerCapability,
        supports_auto: bool,
        for_model: (String, String),
    ) -> Self {
        let levels: Vec<String> = LEVELS.iter()
            .filter(|level| match &capability {
                PickerCapability::Unsupported => false,
                PickerCapability::Indeterminate => true,
                PickerCapability::Levels(supported) =>
                    supported.iter().any(|s| s.eq_ignore_ascii_case(level)),
            })
            .map(|level| (*level).to_owned())
            .collect();
        let selected = current.and_then(|value|
            levels.iter().position(|level| level.eq_ignore_ascii_case(value)));
        Self { levels, selected, capability, supports_auto, for_model, display_label: None }
    }

    pub fn with_display_label(mut self, label: String) -> Self {
        self.display_label = Some(label);
        self
    }

    fn emit(&self, persist: bool) -> SurfaceOutcome {
        if self.capability == PickerCapability::Unsupported {
            return SurfaceOutcome::Handled;
        }
        let effort = match self.selected {
            Some(index) => self.levels[index].clone(),
            None if self.supports_auto => "auto".into(),
            None => return SurfaceOutcome::Handled,
        };
        SurfaceOutcome::Emit(SurfaceAction::SetEffort {
            effort, persist, for_model: self.for_model.clone(),
        })
    }
}

impl Surface for EffortPickerSurface {
    fn title(&self) -> String {
        format!("Effort - {}", self.display_label.as_deref().unwrap_or(&self.for_model.1))
    }

    fn hints(&self) -> String {
        if self.capability == PickerCapability::Unsupported {
            return "Esc cancel".into();
        }
        let auto = if self.supports_auto { " · a auto" } else { "" };
        format!("Left/Right adjust · Enter save · s session only{auto} · Esc cancel")
    }

    fn placement(&self) -> Placement {
        Placement::FitContent { preferred_width: PREFERRED_WIDTH }
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        if key.modifiers.intersects(KeyModifiers::CONTROL | KeyModifiers::ALT) {
            return SurfaceOutcome::Ignored;
        }
        match key.code {
            KeyCode::Esc => SurfaceOutcome::Close,
            KeyCode::Left if !self.levels.is_empty() => {
                self.selected = Some(self.selected.unwrap_or(0).saturating_sub(1));
                SurfaceOutcome::Handled
            }
            KeyCode::Right if !self.levels.is_empty() => {
                self.selected = Some(match self.selected {
                    Some(i) => (i + 1).min(self.levels.len() - 1),
                    None => 0,
                });
                SurfaceOutcome::Handled
            }
            KeyCode::Char('a') if self.supports_auto => {
                self.selected = None;
                SurfaceOutcome::Handled
            }
            KeyCode::Enter => self.emit(true),
            KeyCode::Char('s') => self.emit(false),
            _ => SurfaceOutcome::Ignored,
        }
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        let padding = SIDE_PADDING.min((area.width as usize).saturating_sub(1) / 2);
        let width = (area.width as usize).saturating_sub(padding * 2);
        let padded = |content: Vec<Line<'static>>| {
            let mut lines = vec![Line::raw("")];
            for mut line in content {
                if line.width() > width {
                    line = Line::styled(
                        coda_render::text::truncate_with_ellipsis(&line.to_string(), width),
                        theme.style(Role::Notification),
                    );
                }
                if !line.spans.is_empty() {
                    line.spans.insert(0, Span::raw(" ".repeat(padding)));
                    line.spans.push(Span::raw(" ".repeat(padding)));
                }
                lines.push(line);
            }
            lines.push(Line::raw(""));
            lines
        };
        if self.capability == PickerCapability::Unsupported {
            return padded(vec![Line::raw("This model does not support reasoning effort.")]);
        }
        let selected_style = theme.style(Role::FocusText);
        let normal_style = theme.style(Role::Notification);
        let mut lines = vec![Line::styled(
            if self.selected.is_none() { "Automatic (model default)" } else { "Faster / Smarter" },
            normal_style,
        ), Line::raw("")];
        let count = self.levels.len().max(1);
        if width >= count * 8 {
            let cell = width / count;
            let mut scale = Vec::new();
            let mut labels = Vec::new();
            for (i, level) in self.levels.iter().enumerate() {
                let selected = self.selected == Some(i);
                let style = if selected { selected_style } else { normal_style };
                let mid = cell / 2;
                scale.push(Span::styled(format!("{}{}{}",
                    glyphs::RULE.repeat(mid),
                    if selected { glyphs::CHEVRON_UP } else { glyphs::RULE },
                    glyphs::RULE.repeat(cell.saturating_sub(mid + 1)),
                ), style));
                labels.push(Span::styled(format!("{level:^cell$}"), style));
            }
            lines.push(Line::from(scale));
            lines.push(Line::from(labels));
        } else {
            // A short vertical scale keeps every option readable on narrow terminals.
            for (i, level) in self.levels.iter().enumerate() {
                let selected = self.selected == Some(i);
                lines.push(Line::styled(
                    format!("{} {level}", if selected { glyphs::CHEVRON_UP } else { " " }),
                    if selected { selected_style } else { normal_style },
                ));
            }
        }
        if self.capability == PickerCapability::Indeterminate {
            lines.push(Line::styled("Model capability not yet known.", normal_style));
        }
        padded(lines)
    }

    fn as_any(&self) -> &dyn std::any::Any { self }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn picker(current: Option<&str>, levels: &[&str]) -> EffortPickerSurface {
        EffortPickerSurface::new(
            current,
            PickerCapability::Levels(levels.iter().map(|s| (*s).into()).collect()),
            true,
            ("provider".into(), "model-id".into()),
        )
    }

    fn key(code: KeyCode) -> KeyEvent { KeyEvent::new(code, KeyModifiers::NONE) }

    fn emitted(surface: &mut EffortPickerSurface, code: KeyCode) -> (String, bool) {
        match surface.handle_key(key(code)) {
            SurfaceOutcome::Emit(SurfaceAction::SetEffort { effort, persist, for_model }) => {
                assert_eq!(for_model, ("provider".into(), "model-id".into()));
                (effort, persist)
            }
            _ => panic!("expected an effort action"),
        }
    }

    #[test]
    fn saved_xhigh_is_selected_and_enter_persists() {
        let mut surface = picker(Some("xhigh"), LEVELS);
        assert_eq!(emitted(&mut surface, KeyCode::Enter), ("xhigh".into(), true));
    }

    #[test]
    fn automatic_is_not_misreported_as_high() {
        let mut surface = picker(None, LEVELS);
        assert!(surface.selected.is_none());
        assert_eq!(emitted(&mut surface, KeyCode::Enter), ("auto".into(), true));
        let text = surface.render(Rect::new(0, 0, 54, 8), &Theme::default());
        assert!(text.iter().any(|line| line.to_string().contains("Automatic")));
        assert!(!text.iter().any(|line| line.to_string().contains(glyphs::CHEVRON_UP)));
    }

    #[test]
    fn session_only_and_auto_do_not_request_persistence() {
        let mut surface = picker(Some("high"), LEVELS);
        assert_eq!(emitted(&mut surface, KeyCode::Char('s')), ("high".into(), false));
        surface.handle_key(key(KeyCode::Char('a')));
        assert_eq!(emitted(&mut surface, KeyCode::Char('s')), ("auto".into(), false));
    }

    #[test]
    fn arrows_only_visit_advertised_levels() {
        let mut surface = picker(Some("low"), &["low", "high"]);
        surface.handle_key(key(KeyCode::Right));
        surface.handle_key(key(KeyCode::Right));
        assert_eq!(emitted(&mut surface, KeyCode::Enter).0, "high");
        surface.handle_key(key(KeyCode::Left));
        surface.handle_key(key(KeyCode::Left));
        assert_eq!(emitted(&mut surface, KeyCode::Enter).0, "low");
        let lines = surface.render(Rect::new(0, 0, 54, 8), &Theme::default());
        let text = lines.iter().map(ToString::to_string).collect::<String>();
        assert!(!text.contains("max"));
        assert!(!text.contains("medium"));
    }

    #[test]
    fn unsupported_is_read_only_and_escape_cancels() {
        let mut surface = EffortPickerSurface::new(
            None, PickerCapability::Unsupported, false, ("p".into(), "m".into()),
        );
        assert!(matches!(surface.handle_key(key(KeyCode::Enter)), SurfaceOutcome::Handled));
        assert!(matches!(surface.handle_key(key(KeyCode::Char('s'))), SurfaceOutcome::Handled));
        assert!(matches!(surface.handle_key(key(KeyCode::Esc)), SurfaceOutcome::Close));
    }

    #[test]
    fn narrow_layout_keeps_all_labels_readable() {
        let surface = picker(Some("xhigh"), LEVELS);
        let lines = surface.render(Rect::new(0, 0, 20, 10), &Theme::default());
        for level in LEVELS {
            assert!(lines.iter().any(|line| line.to_string().contains(level)));
        }
        assert!(lines.iter().all(|line| line.width() <= 20));
    }

    #[test]
    fn effort_scale_has_vertical_and_two_column_side_padding() {
        let surface = picker(Some("high"), LEVELS);
        let lines = surface.render(Rect::new(0, 0, PREFERRED_WIDTH, 12), &Theme::default());
        assert!(lines.first().unwrap().to_string().trim().is_empty());
        assert!(lines.last().unwrap().to_string().trim().is_empty());
        let scale = lines.iter().position(|line| line.to_string().contains(glyphs::RULE)).unwrap();
        assert!(lines[scale - 1].to_string().trim().is_empty());
        for line in lines.iter().filter(|line| !line.to_string().trim().is_empty()) {
            let text = line.to_string();
            assert!(text.starts_with("  ") && text.ends_with("  "), "{text:?}");
            assert!(line.width() <= PREFERRED_WIDTH as usize);
        }
    }

    #[test]
    fn escape_and_control_chords_never_emit_a_setting() {
        let mut surface = picker(Some("high"), LEVELS);
        assert!(matches!(surface.handle_key(key(KeyCode::Esc)), SurfaceOutcome::Close));
        assert!(matches!(surface.handle_key(KeyEvent::new(KeyCode::Char('s'), KeyModifiers::CONTROL)),
            SurfaceOutcome::Ignored));
    }
}
