//! The trust gate for local authentication, and the seam that opens a profile.
//!
//! # Trust first, before anything is read
//!
//! Signing in, switching provider and signing out are *machine* maintenance:
//! they touch this profile's credential store, this profile's `settings.json`
//! and this user's browser. None of that is meaningful for an engine this
//! client did not start — its keychain is on another machine — and reading
//! this machine's credential store on its behalf would be worse than useless:
//! it would probe the operator's own credentials for a session that has no
//! business knowing whether they exist.
//!
//! So [`refusal`] is consulted **before** an [`AuthPort`] is opened, and
//! [`AuthPort::opens`] exists so a test can prove the store was never touched
//! rather than merely that a message was printed. The refusal names the
//! command to run *on the engine host* instead.
//!
//! # Why the port is a factory rather than a service
//!
//! [`AuthService`] binds a profile, a credential store, a settings file and an
//! environment at construction. Building it in the flow — rather than holding
//! one for the life of the application — is what makes a login see the profile
//! as it is *now*, including one another process changed. The factory is
//! injectable so tests drive an isolated temporary profile with no `CODA_HOME`
//! and no process environment.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;

use coda_auth::service::AuthService;
use futures::future::BoxFuture;

use super::AccessMode;

/// Which constructor a flow needs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Opening {
    /// Signing in. Fails when a provider's saved deployment cannot be
    /// resolved: a device authorization sent to guessed endpoints is a token
    /// handed to the wrong host.
    Strict,
    /// Reporting and signing out, which must keep working *because* the
    /// configuration is broken.
    Degraded,
}

/// The refusal shown — and returned **before** any credential or settings
/// store is opened — when local authentication is attempted against an engine
/// this client did not start.
///
/// `None` means the operation may proceed.
pub fn refusal(mode: AccessMode, what: &str) -> Option<String> {
    if mode.allows_local_maintenance() {
        return None;
    }
    Some(format!(
        "{what} is host-local maintenance. This session is connected to an engine this client \
         did not start, so this machine's credentials are not the ones it uses — and they are \
         not read here. Run `coda auth login`, `coda auth status` or `coda auth logout` on the \
         engine host instead. Nothing was changed, and no credential store was opened."
    ))
}

/// Opens the profile a login commits to.
///
/// Cloneable so a spawned preparation task can hold one without borrowing the
/// application.
#[derive(Clone)]
pub struct AuthPort {
    factory: Arc<dyn Fn(Opening) -> BoxFuture<'static, Result<Arc<AuthService>, String>> + Send + Sync>,
    opens: Arc<AtomicUsize>,
}

impl std::fmt::Debug for AuthPort {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AuthPort").field("opens", &self.opens()).finish()
    }
}

impl AuthPort {
    /// The production port: this process's profile, its credential store and
    /// its `settings.json`.
    ///
    /// [`coda_auth::store::Profile::from_env`] and
    /// [`coda_auth::store::open_profile_storage`] are called **inside** the
    /// future, so they cannot run until something awaits an opening that the
    /// [`AccessMode`] gate already allowed.
    pub fn profile() -> Self {
        Self::from_factory(|opening| {
            Box::pin(async move {
                let settings: Arc<dyn coda_auth::service::AuthSettingsPort> =
                    Arc::new(coda_boot::settings_store::SettingsFile::for_user());
                let storage = tokio::task::spawn_blocking(|| {
                    coda_auth::store::open_profile_storage(&coda_auth::store::Profile::from_env())
                })
                .await
                .map_err(|_| "opening the credential store was interrupted".to_owned())?
                .map_err(|error| {
                    format!(
                        "the credential store could not be opened: {}",
                        coda_auth::failure::AuthFailure::classify(&error)
                    )
                })?;
                match opening {
                    Opening::Strict => AuthService::from_storage(&storage, settings)
                        .await
                        .map(Arc::new)
                        .map_err(|error| {
                            format!(
                                "signing in is not possible until the saved provider \
                                 configuration is corrected: {}",
                                coda_auth::failure::AuthFailure::classify(&error)
                            )
                        }),
                    Opening::Degraded => {
                        Ok(Arc::new(AuthService::from_storage_degraded(&storage, settings).await))
                    }
                }
            })
        })
    }

    /// A port over an **isolated** profile rooted at `root`, with an explicit
    /// environment map.
    ///
    /// For tests and harnesses: no `CODA_HOME`, no process environment, no
    /// keychain, and a `settings.json` inside `root` — so a flow that commits
    /// a credential and a later one that reads it back see the same profile
    /// without either touching the machine running them.
    pub fn isolated(
        root: impl Into<std::path::PathBuf>,
        environment: Vec<(String, String)>,
    ) -> Self {
        let root = root.into();
        Self::from_factory(move |opening| {
            let root = root.clone();
            let environment = environment.clone();
            Box::pin(async move {
                let settings: Arc<dyn coda_auth::service::AuthSettingsPort> = Arc::new(
                    coda_boot::settings_store::SettingsFile::at(root.join("settings.json")),
                );
                let storage = coda_auth::store::open_profile_storage(
                    &coda_auth::store::Profile::isolated(&root),
                )
                .map_err(|error| format!("the test profile could not be opened: {error}"))?;
                let pairs: Vec<(&str, &str)> = environment
                    .iter()
                    .map(|(key, value)| (key.as_str(), value.as_str()))
                    .collect();
                let builder = AuthService::builder(
                    Arc::clone(&storage.profile),
                    Arc::clone(&storage.coordinator),
                )
                .with_settings(settings)
                .with_environment(Arc::new(coda_auth::service::MapEnvironment::new(&pairs))
                    as Arc<dyn coda_auth::service::AuthEnvironment>);
                match opening {
                    Opening::Strict => {
                        builder.build().await.map(Arc::new).map_err(|error| error.to_string())
                    }
                    Opening::Degraded => Ok(Arc::new(builder.build_degraded().await)),
                }
            })
        })
    }

    /// A port over a caller-supplied factory — an isolated temporary profile
    /// in a test, an embedder's own store.
    pub fn from_factory<F>(factory: F) -> Self
    where
        F: Fn(Opening) -> BoxFuture<'static, Result<Arc<AuthService>, String>>
            + Send
            + Sync
            + 'static,
    {
        Self { factory: Arc::new(factory), opens: Arc::new(AtomicUsize::new(0)) }
    }

    /// How many times a profile has actually been opened through this port.
    ///
    /// The evidence for "an API-only session never probed the operator's
    /// credentials": a refusal that still opened the store would pass a
    /// message-only assertion.
    pub fn opens(&self) -> usize {
        self.opens.load(Ordering::SeqCst)
    }

    /// Opens the profile. Counted.
    pub async fn open(&self, opening: Opening) -> Result<Arc<AuthService>, String> {
        self.opens.fetch_add(1, Ordering::SeqCst);
        (self.factory)(opening).await
    }
}

impl Default for AuthPort {
    fn default() -> Self {
        Self::profile()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_trusted_local_session_is_not_refused() {
        assert!(refusal(AccessMode::TrustedLocal, "Signing in").is_none());
    }

    #[test]
    fn an_api_only_refusal_names_the_engine_host_command_and_promises_no_read() {
        let message = refusal(AccessMode::ApiOnly, "Signing in").expect("refused");
        assert!(message.contains("coda auth login"), "{message}");
        assert!(message.contains("engine host"), "{message}");
        assert!(message.contains("no credential store was opened"), "{message}");
    }

    #[tokio::test]
    async fn a_port_counts_every_opening_so_a_refusal_can_be_proven_to_have_read_nothing() {
        let port = AuthPort::from_factory(|_| Box::pin(async { Err("no".to_owned()) }));
        assert_eq!(port.opens(), 0);
        let _ = port.open(Opening::Degraded).await;
        assert_eq!(port.opens(), 1);
    }
}
