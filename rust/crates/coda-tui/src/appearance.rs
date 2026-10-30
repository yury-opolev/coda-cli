//! The colour theme a launch opens with.
//!
//! The theme is chosen per profile and saved in that profile's own
//! `settings.json`, so it has to be resolved before the first screen is drawn,
//! including the first-run wizard. Both binaries share this one resolution so
//! they cannot disagree about which file or fallback applies.

use coda_boot::auth_cli::sanitize;
use coda_render::theme::{ColorDepth, Theme};

use crate::config::{Paths, Settings};

/// Longest settings-supplied text echoed into a warning.
const MAX_ECHOED_CHARS: usize = 80;

/// The theme to launch with, and a note for the user when the saved choice
/// could not be honoured.
pub struct StartupTheme {
    pub theme: Theme,
    pub warning: Option<String>,
}

/// Resolves the saved theme. A bad settings file must never stop Coda from
/// opening, so every failure falls back to the default theme with a warning.
pub fn startup_theme(paths: &Paths, depth: ColorDepth) -> StartupTheme {
    let fallback = |warning: Option<String>| StartupTheme {
        theme: Theme::default().with_depth(depth),
        warning,
    };
    let settings = match Settings::load(paths) {
        Ok(settings) => settings,
        Err(error) => {
            return fallback(Some(format!(
                "Could not read {}: {}; using {}.",
                paths.settings().display(),
                sanitize(&error.to_string(), MAX_ECHOED_CHARS * 2),
                Theme::default().name,
            )));
        }
    };
    let Some(name) = settings.theme() else {
        return fallback(None);
    };
    match Theme::by_name(name) {
        Some(theme) => StartupTheme {
            theme: theme.with_depth(depth),
            warning: None,
        },
        None => fallback(Some(format!(
            "Unknown theme '{}' in {}; using {}. Available: {}.",
            sanitize(name, MAX_ECHOED_CHARS),
            paths.settings().display(),
            Theme::default().name,
            Theme::names().join(", "),
        ))),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn resolve(settings: Option<&str>, depth: ColorDepth) -> StartupTheme {
        let dir = tempfile::tempdir().unwrap();
        if let Some(text) = settings {
            std::fs::write(dir.path().join("settings.json"), text).unwrap();
        }
        let paths = Paths::new(dir.path()).with_user_root(dir.path());
        startup_theme(&paths, depth)
    }

    #[test]
    fn missing_file_uses_the_default_theme_silently() {
        let resolved = resolve(None, ColorDepth::TrueColor);
        assert_eq!(resolved.theme.name, Theme::default().name);
        assert!(resolved.warning.is_none());
    }

    #[test]
    fn saved_theme_is_used_regardless_of_case_and_padding() {
        let resolved = resolve(Some(r#"{"theme": " Cool-Dark "}"#), ColorDepth::TrueColor);
        assert_eq!(resolved.theme.name, "cool-dark");
        assert!(resolved.warning.is_none());
    }

    #[test]
    fn unknown_theme_falls_back_and_names_the_choices() {
        let resolved = resolve(Some(r#"{"theme": "neon"}"#), ColorDepth::TrueColor);
        assert_eq!(resolved.theme.name, Theme::default().name);
        let warning = resolved.warning.unwrap();
        assert!(warning.contains("'neon'"), "{warning}");
        assert!(warning.contains("settings.json"), "{warning}");
        assert!(warning.contains("warm-ember, cool-dark"), "{warning}");
    }

    #[test]
    fn warning_never_carries_control_characters() {
        let resolved = resolve(Some("{\"theme\": \"a\\u001b[2Jb\\nc\"}"), ColorDepth::TrueColor);
        let warning = resolved.warning.unwrap();
        assert!(!warning.chars().any(char::is_control), "{warning:?}");
    }

    #[test]
    fn invalid_json_falls_back_with_a_warning() {
        let resolved = resolve(Some("{ not json"), ColorDepth::TrueColor);
        assert_eq!(resolved.theme.name, Theme::default().name);
        assert!(resolved.warning.unwrap().contains("settings.json"));
    }

    #[test]
    fn colour_depth_is_applied_to_the_resolved_theme() {
        let expected = Theme::by_name("cool-dark").unwrap().with_depth(ColorDepth::Ansi16);
        let resolved = resolve(Some(r#"{"theme": "cool-dark"}"#), ColorDepth::Ansi16);
        assert_eq!(resolved.theme.depth(), expected.depth());
    }
}
