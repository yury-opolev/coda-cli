//! First-run detection, from the shared selector rather than from file guesses.
//!
//! # What "first run" actually means
//!
//! It means *this profile has no credential and no configured way to get one*.
//! It emphatically does not mean "`settings.json` has no `defaultProvider`" —
//! a machine signed in to a single account has a perfectly usable credential
//! and no saved choice — and it does not mean "the store would not open",
//! which is a fault to report rather than an invitation to sign in again over
//! a credential that may still be recoverable.
//!
//! The verdict therefore comes from [`AuthService::selected_provider`], the
//! same selector the engine resolves its own credential with, so the wizard
//! opens exactly when the engine would have nothing to connect with.
//!
//! # What replaced the previous module
//!
//! The earlier version listed two providers, guessed at a first run by reading
//! `settings.json` and `ANTHROPIC_API_KEY`, and told the user to run a
//! `/login` that did not exist — while its own documentation said the real
//! work was waiting on "login RPCs" that were never going to be added, because
//! signing in is host-local maintenance and has no serve-API method by design.
//! The flow it described now exists in [`crate::app::auth`]; this module is
//! the detection half of it.

use coda_auth::service::{AuthService, ProviderIdentity, SelectionError};

/// What a profile looks like before an engine is asked to connect with it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FirstRun {
    /// A credential is available. Nothing is said.
    Ready,
    /// Nothing is stored and nothing is configured: the wizard applies.
    NoCredentials,
    /// A provider was chosen — on the command line, in the environment or in
    /// settings — and has no usable credential. Nameable, and therefore
    /// fixable by signing in to exactly that account.
    SelectedMissing { identity: ProviderIdentity, reason: String },
    /// Something is wrong that signing in would not fix, and might make
    /// worse. Reported, never treated as a first run.
    Unusable(String),
}

/// The verdict for `service`, with no explicit provider named.
pub async fn first_run_state(service: &AuthService) -> FirstRun {
    launch_state(service, None).await
}

/// The verdict for `service` when the launch explicitly named a provider.
///
/// `explicit` is the launch's own intent — a `--provider` flag or a
/// `CODA_SERVE_PROVIDER` in the environment the engine would inherit — and it
/// is passed to the *same* selector the engine resolves its credential with.
/// Asking without it would report a healthy profile for a launch that is about
/// to fail closed on the one account it was told to use.
pub async fn launch_state(service: &AuthService, explicit: Option<&str>) -> FirstRun {
    match service.select(explicit).await {
        Ok(_) => FirstRun::Ready,
        Err(SelectionError::NoCredentials) => FirstRun::NoCredentials,
        // Chosen but unusable is not a first run: the user made a decision and
        // it is still theirs. Naming the provider is what turns "it does not
        // work" into something actionable.
        Err(SelectionError::NeedsLogin { identity, .. }) => FirstRun::SelectedMissing {
            identity,
            reason: format!(
                "This profile is set to {}, but it has no usable credential. Run /login {} to \
                 connect it, or /provider to choose a different account.",
                identity.label(),
                identity.engine_id(),
            ),
        },
        Err(SelectionError::Ambiguous { stored }) => FirstRun::Unusable(format!(
            "Credentials are stored for {}, and nothing says which to use. Run /provider to \
             choose one.",
            stored.iter().copied().map(ProviderIdentity::label).collect::<Vec<_>>().join(" and "),
        )),
        Err(SelectionError::UnknownProvider { .. }) => FirstRun::Unusable(
            "The saved provider choice names a provider this build does not know. Run /provider \
             to choose one of this product's accounts."
                .to_owned(),
        ),
        // Deliberately not "signed out": the credential may be perfectly valid
        // and simply unreadable from here, and signing in again over it is
        // exactly how a recoverable one gets overwritten.
        Err(SelectionError::Unavailable { failure }) => FirstRun::Unusable(format!(
            "The stored authentication could not be read ({failure}). Do not sign in again \
             until that is understood, or a recoverable credential may be overwritten. \
             `coda auth status` reports what is on this machine."
        )),
    }
}

/// The welcome shown on a genuine first run.
///
/// It names commands that exist and do the work, rather than describing a
/// handoff to something that is not there.
pub const WELCOME_TEXT: &str = "\
Welcome to Coda. No account is connected on this machine yet.\n\
\n\
Run /setup to choose one — a Claude.ai subscription, GitHub Copilot (public or \
an enterprise tenant), or an Anthropic API key. /login <provider> goes straight \
to one, and /logout disconnects again.";

#[cfg(test)]
mod tests {
    use super::*;
    use coda_auth::coordination::LocalCoordinator;
    use coda_auth::service::{
        AuthEnvironment, AuthSettings, InMemoryAuthSettings, MapEnvironment,
    };
    use coda_auth::store::{open_profile_storage, Profile};
    use std::sync::Arc;

    /// A service over a throwaway profile: no `CODA_HOME`, no process
    /// environment, nothing shared with another test.
    async fn service(
        root: &std::path::Path,
        settings: AuthSettings,
        env: &[(&str, &str)],
    ) -> AuthService {
        let storage = open_profile_storage(&Profile::isolated(root)).expect("profile opens");
        AuthService::builder(Arc::clone(&storage.profile), Arc::new(LocalCoordinator::new()))
            .with_settings(Arc::new(InMemoryAuthSettings::with(settings)))
            .with_environment(Arc::new(MapEnvironment::new(env)) as Arc<dyn AuthEnvironment>)
            .build()
            .await
            .expect("builds")
    }

    #[tokio::test]
    async fn an_empty_profile_is_a_first_run() {
        let root = tempfile::tempdir().expect("temp");
        let service = service(root.path(), AuthSettings::default(), &[]).await;
        assert_eq!(first_run_state(&service).await, FirstRun::NoCredentials);
    }

    #[tokio::test]
    async fn an_exported_key_with_no_saved_choice_is_not_a_first_run() {
        // The engine would connect with it, so the wizard must not claim there
        // is nothing to connect with.
        let root = tempfile::tempdir().expect("temp");
        let service =
            service(root.path(), AuthSettings::default(), &[("ANTHROPIC_API_KEY", "sk-x")]).await;
        assert_eq!(first_run_state(&service).await, FirstRun::Ready);
    }

    #[tokio::test]
    async fn a_saved_choice_with_nothing_behind_it_is_reported_not_treated_as_a_first_run() {
        let root = tempfile::tempdir().expect("temp");
        let settings = AuthSettings {
            default_provider: Some("github-copilot".into()),
            github_enterprise_domain: None,
        };
        let service = service(root.path(), settings, &[]).await;
        match first_run_state(&service).await {
            FirstRun::SelectedMissing { identity, reason } => {
                assert_eq!(identity, ProviderIdentity::GithubCopilot);
                assert!(reason.contains("GitHub Copilot"), "{reason}");
                assert!(reason.contains("/login github-copilot"), "{reason}");
            }
            other => panic!("expected a named account to connect, got {other:?}"),
        }
    }

    /// The launch's own intent decides what "missing" means. Without it, a
    /// profile holding one healthy credential reports `Ready` for a launch
    /// that was explicitly told to use a different account and is about to
    /// fail closed on it.
    #[tokio::test]
    async fn an_explicitly_named_provider_with_no_credential_is_a_setup_the_launch_can_offer() {
        let root = tempfile::tempdir().expect("temp");
        let service =
            service(root.path(), AuthSettings::default(), &[("ANTHROPIC_API_KEY", "sk-x")]).await;
        assert_eq!(first_run_state(&service).await, FirstRun::Ready);

        match launch_state(&service, Some("github-copilot")).await {
            FirstRun::SelectedMissing { identity, .. } => {
                assert_eq!(identity, ProviderIdentity::GithubCopilot);
            }
            other => panic!("an explicitly selected, unconnected provider reported {other:?}"),
        }
    }

    #[tokio::test]
    async fn a_provider_this_build_does_not_know_is_reported_rather_than_offered_a_wizard() {
        let root = tempfile::tempdir().expect("temp");
        let service = service(root.path(), AuthSettings::default(), &[]).await;
        match launch_state(&service, Some("not-a-provider")).await {
            FirstRun::Unusable(reason) => assert!(reason.contains("does not know"), "{reason}"),
            other => panic!("an unknown provider was treated as {other:?}"),
        }
    }

    #[test]
    fn the_welcome_names_commands_that_exist() {
        assert!(WELCOME_TEXT.contains("/setup"));
        assert!(WELCOME_TEXT.contains("/login"));
        assert!(WELCOME_TEXT.contains("/logout"));
        // The previous wording promised work that no code did.
        assert!(!WELCOME_TEXT.contains("SEAM"));
    }
}
