//! The writable settings seam the auth transaction commits through.
//!
//! # Why a port
//!
//! `settings.json` is shared: the TUI writes themes and models to it, the
//! engine reads it, and several Coda processes may have it open at once. The
//! auth transaction owns exactly two keys in it —`defaultProvider` and
//! `githubEnterpriseDomain` — and must be able to change *those* and nothing
//! else, then put them back if the rest of the commit fails.
//!
//! The file access itself is not here. `coda-auth` is a credential crate: the
//! locking, re-reading and atomic replacement live in the host bootstrap crate
//! (`coda_boot::auth_settings`), which is also where the TUI's own writer takes
//! the same lock. This port is what the transaction sees, and what a test can
//! substitute with [`InMemoryAuthSettings`] — including one that fails on
//! demand, so the rollback path is exercised for real.
//!
//! # Contract for implementors
//!
//! * [`AuthSettingsPort::load`] returns only the owned keys.
//! * [`AuthSettingsPort::apply`] must, under one exclusive settings lock:
//!   re-read the current document, change only the keys the patch names, and
//!   replace the file atomically. It must never write back a snapshot the
//!   caller loaded earlier — that is how a concurrent theme change, or a
//!   freshly committed provider, gets reverted.
//! * A missing file is not an error; an unreadable or non-object one is.

use std::fmt;

use crate::error::AuthError;

/// The settings the auth transaction owns.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct AuthSettings {
    /// `defaultProvider` — the saved provider choice, in engine spelling.
    pub default_provider: Option<String>,
    /// `githubEnterpriseDomain` — the Copilot tenant a login selected.
    pub github_enterprise_domain: Option<String>,
}

/// A change to the owned settings keys.
///
/// Each field is a nested option on purpose:
///
/// * `None` — leave the key exactly as it is (including absent);
/// * `Some(None)` — remove the key;
/// * `Some(Some(value))` — set the key.
///
/// A rollback is expressed as the patch that names *both* keys with their
/// previous values, so restoring "this key was absent" is a real instruction
/// rather than the absence of one.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct AuthSettingsPatch {
    pub default_provider: Option<Option<String>>,
    pub github_enterprise_domain: Option<Option<String>>,
}

impl AuthSettingsPatch {
    /// A patch that changes nothing.
    pub fn empty() -> Self {
        Self::default()
    }

    /// Whether this patch would change anything.
    pub fn is_empty(&self) -> bool {
        self.default_provider.is_none() && self.github_enterprise_domain.is_none()
    }

    pub fn with_default_provider(mut self, value: Option<String>) -> Self {
        self.default_provider = Some(value);
        self
    }

    pub fn with_github_enterprise_domain(mut self, value: Option<String>) -> Self {
        self.github_enterprise_domain = Some(value);
        self
    }

    /// The patch that restores `previous` for every key this patch names.
    ///
    /// Only the named keys are restored: a rollback must not "tidy up" a key
    /// the transaction never touched.
    pub fn inverse_of(&self, previous: &AuthSettings) -> Self {
        Self {
            default_provider: self
                .default_provider
                .as_ref()
                .map(|_| previous.default_provider.clone()),
            github_enterprise_domain: self
                .github_enterprise_domain
                .as_ref()
                .map(|_| previous.github_enterprise_domain.clone()),
        }
    }

    /// The patch reduced to the keys that actually differ from `current`.
    ///
    /// Keeps a commit from rewriting `settings.json` when the saved provider
    /// is already the one being committed.
    pub fn changes_against(&self, current: &AuthSettings) -> Self {
        let keep = |patched: &Option<Option<String>>, now: &Option<String>| match patched {
            Some(value) if value != now => Some(value.clone()),
            _ => None,
        };
        Self {
            default_provider: keep(&self.default_provider, &current.default_provider),
            github_enterprise_domain: keep(
                &self.github_enterprise_domain,
                &current.github_enterprise_domain,
            ),
        }
    }

    /// `settings` with this patch applied — the in-memory equivalent of what an
    /// implementation writes.
    pub fn applied_to(&self, settings: &AuthSettings) -> AuthSettings {
        AuthSettings {
            default_provider: self
                .default_provider
                .clone()
                .unwrap_or_else(|| settings.default_provider.clone()),
            github_enterprise_domain: self
                .github_enterprise_domain
                .clone()
                .unwrap_or_else(|| settings.github_enterprise_domain.clone()),
        }
    }
}

/// Read and patch the settings keys the auth transaction owns.
///
/// Implementations are shared between processes, so `apply` must do its own
/// locking and re-reading; see the module docs.
pub trait AuthSettingsPort: Send + Sync + fmt::Debug {
    fn load(&self) -> Result<AuthSettings, AuthError>;
    fn apply(&self, patch: &AuthSettingsPatch) -> Result<(), AuthError>;
}

/// An in-process settings port for tests and for hosts with no settings file.
///
/// `fail_next` makes the *settings* step of a commit fail on demand, which is
/// how the rollback of an already-written credential is tested without
/// corrupting a real file.
#[derive(Debug, Default)]
pub struct InMemoryAuthSettings {
    state: std::sync::Mutex<AuthSettings>,
    fail: std::sync::Mutex<Option<String>>,
    fail_load: std::sync::Mutex<Option<String>>,
    applied: std::sync::atomic::AtomicUsize,
}

impl InMemoryAuthSettings {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn with(settings: AuthSettings) -> Self {
        Self { state: std::sync::Mutex::new(settings), ..Self::default() }
    }

    /// Make the next `apply` fail with a store error.
    pub fn fail_next(&self, detail: impl Into<String>) {
        *self.fail.lock().unwrap() = Some(detail.into());
    }

    /// Make every `load` fail, the way a corrupt or unreadable settings file
    /// does, until [`Self::stop_failing`].
    pub fn fail_load(&self, detail: impl Into<String>) {
        *self.fail_load.lock().unwrap() = Some(detail.into());
    }

    /// Stop failing.
    pub fn stop_failing(&self) {
        *self.fail.lock().unwrap() = None;
        *self.fail_load.lock().unwrap() = None;
    }

    /// How many patches were applied — a rollback is one of them.
    pub fn applied_count(&self) -> usize {
        self.applied.load(std::sync::atomic::Ordering::SeqCst)
    }

    /// The current owned settings.
    pub fn current(&self) -> AuthSettings {
        self.state.lock().unwrap().clone()
    }

    /// Change the settings behind the service's back, the way another process
    /// would.
    pub fn set(&self, settings: AuthSettings) {
        *self.state.lock().unwrap() = settings;
    }
}

impl AuthSettingsPort for InMemoryAuthSettings {
    fn load(&self) -> Result<AuthSettings, AuthError> {
        if let Some(detail) = self.fail_load.lock().unwrap().clone() {
            return Err(AuthError::Store(detail));
        }
        Ok(self.state.lock().unwrap().clone())
    }

    fn apply(&self, patch: &AuthSettingsPatch) -> Result<(), AuthError> {
        if let Some(detail) = self.fail.lock().unwrap().take() {
            return Err(AuthError::Store(detail));
        }
        let mut state = self.state.lock().unwrap();
        *state = patch.applied_to(&state);
        self.applied.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn settings(provider: Option<&str>, domain: Option<&str>) -> AuthSettings {
        AuthSettings {
            default_provider: provider.map(str::to_owned),
            github_enterprise_domain: domain.map(str::to_owned),
        }
    }

    #[test]
    fn an_inverse_restores_absence_as_an_instruction() {
        let previous = settings(None, Some("octocorp.ghe.com"));
        let patch = AuthSettingsPatch::empty()
            .with_default_provider(Some("claude-ai".into()))
            .with_github_enterprise_domain(None);

        let inverse = patch.inverse_of(&previous);
        assert_eq!(inverse.default_provider, Some(None), "the key was absent and must go back to absent");
        assert_eq!(inverse.github_enterprise_domain, Some(Some("octocorp.ghe.com".into())));
        assert_eq!(inverse.applied_to(&patch.applied_to(&previous)), previous);
    }

    #[test]
    fn an_inverse_never_touches_a_key_the_patch_left_alone() {
        let previous = settings(Some("github-copilot"), Some("octocorp.ghe.com"));
        let patch = AuthSettingsPatch::empty().with_default_provider(Some("claude-ai".into()));
        let inverse = patch.inverse_of(&previous);
        assert_eq!(inverse.github_enterprise_domain, None);
    }

    #[test]
    fn a_patch_that_changes_nothing_is_reduced_away() {
        let current = settings(Some("claude-ai"), None);
        let patch = AuthSettingsPatch::empty()
            .with_default_provider(Some("claude-ai".into()))
            .with_github_enterprise_domain(None);
        assert!(patch.changes_against(&current).is_empty());
    }
}
