//! The colour theme a launch opens with.
//!
//! The theme is chosen per profile and saved in that profile's own
//! `settings.json`, so it has to be resolved before the first screen is drawn,
//! including the first-run wizard. Both binaries share this one resolution so
//! they cannot disagree about which file or fallback applies.

use coda_boot::auth_cli::sanitize;
use coda_render::theme::{ColorDepth, Theme};

use crate::config::{ConfigError, Paths, Settings};

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
            let path = paths.settings().display().to_string();
            return fallback(Some(unreadable_warning(&path, &error_cause(&error))));
        }
    };
    let Some(value) = settings.theme_value() else {
        return fallback(None);
    };
    let Some(name) = value.as_str() else {
        return fallback(Some(non_name_theme_warning(
            &paths.settings().display().to_string(),
        )));
    };
    match Theme::by_name(name) {
        Some(theme) => StartupTheme {
            theme: theme.with_depth(depth),
            warning: None,
        },
        None => fallback(Some(unknown_theme_warning(
            name,
            &paths.settings().display().to_string(),
        ))),
    }
}

/// The underlying io/serde message. `ConfigError`'s own text repeats the path
/// the warning already names, which would also eat into the length bound.
fn error_cause(error: &ConfigError) -> String {
    match error {
        ConfigError::Read { source, .. } | ConfigError::Write { source, .. } => source.to_string(),
        ConfigError::Parse { source, .. } => source.to_string(),
        other => other.to_string(),
    }
}

/// The path comes from `CODA_HOME`, so it is untrusted terminal text just
/// like the settings contents: each piece is sanitized and bounded on its own
/// so a long path cannot truncate the cause.
fn unreadable_warning(path: &str, cause: &str) -> String {
    format!(
        "Could not use {}: {}; using {}.",
        sanitize(path, MAX_ECHOED_CHARS * 2),
        sanitize(cause, MAX_ECHOED_CHARS * 2),
        Theme::default().name,
    )
}

fn non_name_theme_warning(path: &str) -> String {
    format!(
        "The theme in {} is not a name; using {}. Available: {}.",
        sanitize(path, MAX_ECHOED_CHARS * 2),
        Theme::default().name,
        Theme::names().join(", "),
    )
}

fn unknown_theme_warning(name: &str, path: &str) -> String {
    format!(
        "Unknown theme '{}' in {}; using {}. Available: {}.",
        sanitize(name, MAX_ECHOED_CHARS),
        sanitize(path, MAX_ECHOED_CHARS * 2),
        Theme::default().name,
        Theme::names().join(", "),
    )
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
        let warning = resolved.warning.unwrap();
        assert!(warning.contains("settings.json"), "{warning}");
        let (_, cause) = warning.split_once("settings.json: ").expect("cause follows the path");
        assert!(cause.contains("line"), "{warning}");
        assert_eq!(warning.matches("settings.json").count(), 1, "{warning}");
    }

    #[test]
    fn hostile_paths_are_sanitized_in_both_warnings() {
        let path = "/tmp/\u{1b}[2J\nhome/.coda/settings.json";
        let unreadable = unreadable_warning(path, "access \u{1b}denied\n");
        assert!(!unreadable.chars().any(char::is_control), "{unreadable:?}");
        let unknown = unknown_theme_warning("neon", path);
        assert!(!unknown.chars().any(char::is_control), "{unknown:?}");
    }

    #[test]
    fn long_paths_do_not_crowd_out_the_cause() {
        let path = "p".repeat(500);
        let warning = unreadable_warning(&path, "expected value at line 1 column 3");
        assert!(warning.contains("expected value at line 1 column 3"), "{warning}");
    }

    #[test]
    fn colour_depth_is_applied_to_the_resolved_theme() {
        let resolved = resolve(Some(r#"{"theme": "cool-dark"}"#), ColorDepth::Ansi16);
        assert_eq!(resolved.theme.name, "cool-dark");
        assert_eq!(resolved.theme.depth(), ColorDepth::Ansi16);
    }

    #[test]
    fn a_theme_that_is_not_a_name_falls_back_with_a_warning() {
        for value in ["5", "true", "{}", "[]"] {
            let resolved = resolve(Some(&format!(r#"{{"theme": {value}}}"#)), ColorDepth::TrueColor);
            assert_eq!(resolved.theme.name, Theme::default().name, "{value}");
            let warning = resolved.warning.unwrap_or_else(|| panic!("no warning for {value}"));
            assert!(warning.contains("not a name"), "{warning}");
            assert!(warning.contains("settings.json"), "{warning}");
            assert!(warning.contains("warm-ember, cool-dark"), "{warning}");
        }
    }

    #[test]
    fn a_null_theme_is_treated_as_unset() {
        let resolved = resolve(Some(r#"{"theme": null}"#), ColorDepth::TrueColor);
        assert_eq!(resolved.theme.name, Theme::default().name);
        assert!(resolved.warning.is_none());
    }

    #[test]
    fn an_unreadable_settings_file_falls_back_with_a_warning() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::create_dir(dir.path().join("settings.json")).unwrap();
        let paths = Paths::new(dir.path()).with_user_root(dir.path());
        let resolved = startup_theme(&paths, ColorDepth::TrueColor);
        assert_eq!(resolved.theme.name, Theme::default().name);
        let warning = resolved.warning.expect("a warning");
        assert!(warning.contains("settings.json"), "{warning}");
    }
}
