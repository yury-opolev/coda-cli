//! Resolves the Coda profile root — the directory that contains `.coda`.
//!
//! Every serve-reachable reader of user state (settings, credentials, the model
//! catalog cache, skills, plugins, hooks) must agree on where that state lives,
//! and must be redirectable so a test — or the differential parity harness —
//! can force the engine into an isolated profile instead of the developer's
//! real `~/.coda`.
//!
//! # Why an explicit override is mandatory on Windows
//!
//! The obvious idea — "just set `USERPROFILE`/`HOME` for the child process" —
//! does **not** work on Windows. The `directories` crate resolves the home via
//! `SHGetKnownFolderPath(FOLDERID_Profile)`, which reads the user token, not the
//! environment; setting `USERPROFILE` leaves it returning the real profile. This
//! was verified empirically (`UserDirs::home_dir()` returned the real
//! `C:\Users\<name>` even with `USERPROFILE` pointed elsewhere). The C# engine
//! has the same property with `SpecialFolder.UserProfile`. So the only reliable
//! redirect is an explicit application-level override, which is what
//! [`coda_home`] provides via `CODA_HOME`.
//!
//! `CODA_HOME` names the profile root (the parent of `.coda`), matching the TUI
//! (`coda-tui`) and the C# `CODA_SETTINGS_DIR` seam, so a single value isolates
//! every reader consistently.

use std::path::PathBuf;

/// The environment variable that overrides the profile root.
pub const CODA_HOME_ENV: &str = "CODA_HOME";

/// The profile root: `CODA_HOME` when set and non-empty, else the OS home
/// directory, else the current directory as a last resort.
pub fn coda_home() -> PathBuf {
    if let Some(explicit) = std::env::var_os(CODA_HOME_ENV) {
        if !explicit.is_empty() {
            return PathBuf::from(explicit);
        }
    }
    directories::BaseDirs::new()
        .map(|d| d.home_dir().to_path_buf())
        .or_else(|| directories::UserDirs::new().map(|d| d.home_dir().to_path_buf()))
        .unwrap_or_else(|| PathBuf::from("."))
}

/// The `.coda` state directory under the resolved [`coda_home`].
pub fn coda_dir() -> PathBuf {
    coda_home().join(".coda")
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    // `set_var`/`remove_var` mutate process-global state; serialise so the two
    // env-sensitive tests cannot interleave.
    static ENV_LOCK: Mutex<()> = Mutex::new(());

    #[test]
    fn coda_home_prefers_the_explicit_override() {
        let _guard = ENV_LOCK.lock().unwrap();
        let saved = std::env::var_os(CODA_HOME_ENV);
        std::env::set_var(CODA_HOME_ENV, "C:\\isolated-home");
        assert_eq!(coda_home(), PathBuf::from("C:\\isolated-home"));
        assert_eq!(coda_dir(), PathBuf::from("C:\\isolated-home").join(".coda"));
        match saved {
            Some(v) => std::env::set_var(CODA_HOME_ENV, v),
            None => std::env::remove_var(CODA_HOME_ENV),
        }
    }

    #[test]
    fn coda_home_falls_back_to_the_os_home_when_unset() {
        let _guard = ENV_LOCK.lock().unwrap();
        let saved = std::env::var_os(CODA_HOME_ENV);
        std::env::remove_var(CODA_HOME_ENV);
        // Whatever the OS reports, it must not be empty and must not be the
        // override sentinel.
        let home = coda_home();
        assert!(!home.as_os_str().is_empty());
        assert_ne!(home, PathBuf::from("C:\\isolated-home"));
        if let Some(v) = saved {
            std::env::set_var(CODA_HOME_ENV, v);
        }
    }
}
