//! The `/settings` surface.
//!
//! Built from the settings on disk and applied back onto them, so what opens
//! is what is persisted rather than a set of defaults that would silently
//! overwrite the user's configuration on save.

use super::{Surface, SurfaceAction, SurfaceOutcome};
use crate::config::{ConfigError, Paths, Settings};
use crate::render::glyphs;
use crate::widgets::{Form, RadioGroup, Select, StaticText, Switch};
use coda_render::theme::{Role, Theme};
use crossterm::event::KeyEvent;
use ratatui::layout::Rect;
use ratatui::text::Line;

/// Options offered for each choice, in the order they appear in the form.
///
/// Kept as slices rather than inline literals so the index-to-value mapping is
/// stated once. Building and applying read the same list, which is what stops
/// the two drifting apart and writing the wrong value.
pub const PERMISSION_MODES: &[&str] = &["ask", "auto", "plan"];
pub const THEMES: &[&str] = &["warm-ember", "cool-slate", "high-contrast"];
pub const TOOL_DISPLAY_MODES: &[&str] = &["compact", "expanded"];

/// Index of each control within the form.
mod index {
    pub const PERMISSION: usize = 1;
    pub const THEME: usize = 2;
    pub const TOOL_DISPLAY: usize = 3;
    pub const TELEMETRY: usize = 4;
}

/// Finds `value` in `options`, falling back to the first entry.
///
/// A settings file can legitimately hold a value this build does not know —
/// written by a newer version, or by hand. Falling back keeps the form usable
/// instead of refusing to open.
fn position_or_first(options: &[&str], value: Option<&str>) -> usize {
    value
        .and_then(|v| options.iter().position(|o| o.eq_ignore_ascii_case(v)))
        .unwrap_or(0)
}

/// Builds the settings form from the current configuration.
fn build(settings: &Settings) -> Form {
    Form::new(vec![
        Box::new(
            StaticText::new("Changes apply on save and are written to settings.json.")
                .with_role(Role::Notification),
        ),
        Box::new(
            RadioGroup::new(
                "Permission mode",
                PERMISSION_MODES.iter().map(|s| s.to_string()).collect(),
            )
            .with_selected(position_or_first(
                PERMISSION_MODES,
                settings.permission_mode(),
            )),
        ),
        Box::new(
            Select::new("Theme", THEMES.iter().map(|s| s.to_string()).collect())
                .with_selected(position_or_first(THEMES, settings.theme())),
        ),
        Box::new(
            Select::new(
                "Tool display",
                TOOL_DISPLAY_MODES.iter().map(|s| s.to_string()).collect(),
            )
            .with_selected(position_or_first(
                TOOL_DISPLAY_MODES,
                settings.tool_display_mode(),
            )),
        ),
        Box::new(Switch::new("Telemetry").with_value(settings.telemetry_enabled())),
    ])
}

/// Reads a control's chosen index back out of the form.
///
/// Returns `None` when the control is missing or is not one of the two types
/// that carry an index, which can only happen if [`build`] and [`index`] fall
/// out of step. The caller skips rather than substituting a default, because
/// substituting would write the wrong setting.
fn selected(form: &Form, at: usize) -> Option<usize> {
    let control = form.control(at)?;
    if let Some(radio) = control.as_any().downcast_ref::<RadioGroup>() {
        return Some(radio.selected_index());
    }
    control
        .as_any()
        .downcast_ref::<Select>()
        .map(Select::selected_index)
}

pub struct SettingsSurface {
    inner: super::form::FormSurface,
}

impl SettingsSurface {
    pub fn new(settings: &Settings) -> Self {
        Self {
            inner: super::form::FormSurface::new(
                "Settings",
                format!(
                    "Tab: next    {}: change    Enter: save    Esc: cancel",
                    glyphs::ARROWS_VERTICAL
                ),
                build(settings),
            )
            .on_submit(SurfaceAction::SaveSettings),
        }
    }

    /// Loads settings and builds the surface.
    pub fn open(paths: &Paths) -> Self {
        let settings =
            Settings::load(paths).unwrap_or_else(|_| Settings::empty_at(paths.settings()));
        Self::new(&settings)
    }

    pub fn theme_index(&self) -> usize {
        selected(self.form(), index::THEME).unwrap_or(0)
    }

    pub fn form(&self) -> &Form {
        self.inner.form()
    }

    /// Applies the form's values onto `settings` and saves.
    pub fn apply(&self, settings: &mut Settings) -> Result<(), ConfigError> {
        let form = self.form();
        if let Some(i) = selected(form, index::PERMISSION) {
            settings.set_permission_mode(PERMISSION_MODES[i]);
        }
        if let Some(i) = selected(form, index::THEME) {
            settings.set_theme(THEMES[i]);
        }
        if let Some(i) = selected(form, index::TOOL_DISPLAY) {
            settings.set_tool_display_mode(TOOL_DISPLAY_MODES[i]);
        }
        if let Some(switch) = form
            .control(index::TELEMETRY)
            .and_then(|c| c.as_any().downcast_ref::<Switch>())
        {
            // Level and stderr are not exposed in the form; preserve whatever
            // is configured rather than resetting them as a side effect of
            // toggling the switch.
            let level = settings.telemetry_level().unwrap_or("info").to_string();
            let stderr = settings.telemetry_stderr();
            settings.set_telemetry(switch.is_on(), &level, stderr);
        }
        settings.save()
    }
}

impl Surface for SettingsSurface {
    fn as_any(&self) -> &dyn std::any::Any {
        self
    }
        fn as_any_mut(&mut self) -> &mut dyn std::any::Any {
            self
        }

    fn title(&self) -> String {
        self.inner.title()
    }

    fn hints(&self) -> String {
        self.inner.hints()
    }

    fn handle_key(&mut self, key: KeyEvent) -> SurfaceOutcome {
        self.inner.dispatch(key)
    }

    fn render(&self, area: Rect, theme: &Theme) -> Vec<Line<'static>> {
        self.inner.render(area, theme)
    }

    fn cursor(&self, area: Rect, theme: &Theme) -> Option<(u16, u16)> {
        self.inner.cursor(area, theme)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::{KeyCode, KeyModifiers};

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    fn settings() -> Settings {
        Settings::empty_at(std::env::temp_dir().join("coda-settings-surface-test.json"))
    }

    #[test]
    fn enter_emits_a_save_action_rather_than_saving_directly() {
        // The surface must not touch the filesystem: that is the app's job,
        // and it is what keeps this testable without one.
        let mut surface = SettingsSurface::new(&settings());
        assert!(matches!(
            surface.handle_key(key(KeyCode::Enter)),
            SurfaceOutcome::Emit(SurfaceAction::SaveSettings)
        ));
    }

    #[test]
    fn escape_closes_the_surface() {
        let mut surface = SettingsSurface::new(&settings());
        assert!(matches!(
            surface.handle_key(key(KeyCode::Esc)),
            SurfaceOutcome::Close
        ));
    }

    #[test]
    fn the_surface_opens_on_configured_values_not_on_defaults() {
        let mut s = settings();
        s.set_theme("high-contrast");
        s.set_permission_mode("plan");
        let surface = SettingsSurface::new(&s);
        assert_eq!(surface.theme_index(), 2, "theme did not round-trip");
        assert_eq!(
            selected(surface.form(), index::PERMISSION),
            Some(2),
            "permission mode did not round-trip"
        );
    }

    #[test]
    fn an_unknown_configured_value_falls_back_rather_than_failing() {
        let mut s = settings();
        s.set_theme("a-theme-from-the-future");
        assert_eq!(SettingsSurface::new(&s).theme_index(), 0);
    }

    #[test]
    fn editing_changes_the_value_it_reports() {
        let mut surface = SettingsSurface::new(&settings());
        assert_eq!(surface.form().focused_index(), index::PERMISSION);
        surface.handle_key(key(KeyCode::Down));
        assert_eq!(selected(surface.form(), index::PERMISSION), Some(1));
    }

    #[test]
    fn applying_writes_every_edited_value() {
        let mut surface = SettingsSurface::new(&settings());
        surface.handle_key(key(KeyCode::Down)); // permission -> auto
        let mut s = settings();
        // Save to a temp path; the point is the values, not the file.
        let _ = surface.apply(&mut s);
        assert_eq!(s.permission_mode(), Some("auto"));
    }

    #[test]
    fn the_telemetry_switch_reflects_and_toggles() {
        let mut surface = SettingsSurface::new(&settings());
        for _ in 0..(index::TELEMETRY - index::PERMISSION) {
            surface.handle_key(key(KeyCode::Tab));
        }
        assert_eq!(surface.form().focused_index(), index::TELEMETRY);
        surface.handle_key(key(KeyCode::Char(' ')));

        let on = surface
            .form()
            .control(index::TELEMETRY)
            .and_then(|c| c.as_any().downcast_ref::<Switch>())
            .expect("a switch")
            .is_on();
        assert!(on, "the switch did not toggle");
    }

    #[test]
    fn every_control_index_points_at_the_type_apply_expects() {
        // Guards build() and index:: against drifting apart, which would write
        // a value into the wrong setting.
        let surface = SettingsSurface::new(&settings());
        let form = surface.form();
        assert!(form
            .control(index::PERMISSION)
            .unwrap()
            .as_any()
            .downcast_ref::<RadioGroup>()
            .is_some());
        assert!(form
            .control(index::THEME)
            .unwrap()
            .as_any()
            .downcast_ref::<Select>()
            .is_some());
        assert!(form
            .control(index::TOOL_DISPLAY)
            .unwrap()
            .as_any()
            .downcast_ref::<Select>()
            .is_some());
        assert!(form
            .control(index::TELEMETRY)
            .unwrap()
            .as_any()
            .downcast_ref::<Switch>()
            .is_some());
    }

    #[test]
    fn it_never_renders_more_lines_than_the_area_allows() {
        let surface = SettingsSurface::new(&settings());
        for height in [1, 3, 8, 40] {
            let area = Rect::new(0, 0, 60, height);
            assert!(surface.render(area, &Theme::default()).len() <= height as usize);
        }
    }
}
