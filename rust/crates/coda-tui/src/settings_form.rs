//! The `/settings` form.
//!
//! The first consumer of [`crate::widgets`], and the reason that module exists:
//! settings are a list of heterogeneous choices, which is exactly what the
//! hand-rolled overlays could not express without repeating themselves.
//!
//! The form is built from the current [`Settings`] and applied back onto them,
//! so what the user sees on open is what is actually persisted rather than a
//! set of defaults that silently overwrite their configuration on save.

use crate::config::{ConfigError, Paths, Settings};
use crate::widgets::{Form, RadioGroup, Select, StaticText, Switch};
use coda_render::theme::Role;

/// Options offered for each choice, in the order they appear in the form.
///
/// Kept as slices rather than inline literals so the index-to-value mapping is
/// stated once. Building and applying the form read the same list, which is
/// what stops the two drifting apart and writing the wrong value.
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
pub fn build(settings: &Settings) -> Form {
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
/// out of step. Silently substituting a default here would write the wrong
/// setting, so the caller skips instead.
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

/// Applies the form's values onto `settings` and saves.
pub fn apply(form: &Form, settings: &mut Settings) -> Result<(), ConfigError> {
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
        // Level and stderr are not exposed in the form; preserve whatever is
        // already configured rather than resetting them as a side effect of
        // toggling the switch.
        let level = settings.telemetry_level().unwrap_or("info").to_string();
        let stderr = settings.telemetry_stderr();
        settings.set_telemetry(switch.is_on(), &level, stderr);
    }
    settings.save()
}

/// Loads settings, builds the form.
pub fn open(paths: &Paths) -> Form {
    let settings =
        Settings::load(paths).unwrap_or_else(|_| Settings::empty_at(paths.settings()));
    build(&settings)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crossterm::event::{KeyCode, KeyEvent, KeyModifiers};

    fn settings() -> Settings {
        Settings::empty_at(std::env::temp_dir().join("coda-settings-form-test.json"))
    }

    fn key(code: KeyCode) -> KeyEvent {
        KeyEvent::new(code, KeyModifiers::NONE)
    }

    #[test]
    fn the_form_opens_on_the_configured_values_not_on_defaults() {
        let mut settings = settings();
        settings.set_permission_mode("plan");
        settings.set_theme("high-contrast");

        let form = build(&settings);
        assert_eq!(
            selected(&form, index::PERMISSION),
            Some(2),
            "permission mode did not round-trip"
        );
        assert_eq!(
            selected(&form, index::THEME),
            Some(2),
            "theme did not round-trip"
        );
    }

    #[test]
    fn an_unknown_configured_value_falls_back_rather_than_failing() {
        let mut settings = settings();
        settings.set_theme("a-theme-from-the-future");
        let form = build(&settings);
        assert_eq!(selected(&form, index::THEME), Some(0));
    }

    #[test]
    fn editing_the_form_changes_the_value_it_reports() {
        let settings = settings();
        let mut form = build(&settings);

        // Focus starts on the permission radio group, past the static heading.
        assert_eq!(form.focused_index(), index::PERMISSION);
        form.handle_key(key(KeyCode::Down));
        assert_eq!(selected(&form, index::PERMISSION), Some(1));
    }

    #[test]
    fn the_telemetry_switch_reflects_and_toggles() {
        let settings = settings();
        let mut form = build(&settings);
        let before = form
            .control(index::TELEMETRY)
            .and_then(|c| c.as_any().downcast_ref::<Switch>())
            .expect("a switch")
            .is_on();

        for _ in 0..(index::TELEMETRY - index::PERMISSION) {
            form.handle_key(key(KeyCode::Tab));
        }
        assert_eq!(form.focused_index(), index::TELEMETRY);
        form.handle_key(key(KeyCode::Char(' ')));

        let after = form
            .control(index::TELEMETRY)
            .and_then(|c| c.as_any().downcast_ref::<Switch>())
            .expect("a switch")
            .is_on();
        assert_ne!(before, after, "the switch did not toggle");
    }

    #[test]
    fn every_control_index_points_at_the_type_apply_expects() {
        // Guards build() and index:: against drifting apart, which would write
        // a value into the wrong setting.
        let form = build(&settings());
        assert!(form.control(index::PERMISSION).unwrap().as_any().downcast_ref::<RadioGroup>().is_some());
        assert!(form.control(index::THEME).unwrap().as_any().downcast_ref::<Select>().is_some());
        assert!(form.control(index::TOOL_DISPLAY).unwrap().as_any().downcast_ref::<Select>().is_some());
        assert!(form.control(index::TELEMETRY).unwrap().as_any().downcast_ref::<Switch>().is_some());
    }
}
