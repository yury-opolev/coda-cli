//! First-run detection and setup wizard.
//!
//! Ported from `SetupWizard.cs` and `FirstRunDetector.cs` in C#.
//!
//! The C# wizard uses Spectre.Console and a `CommandContext` that bundles
//! credentials, providers, and prompts.  The Rust port runs entirely inside
//! the existing TUI: it injects notice and prompt blocks into the transcript,
//! using the reducer's existing `UiEvent::PromptRequested` path for provider
//! selection.
//!
//! ## Seams
//!
//! Two steps in the wizard depend on subsystems being ported by other agents:
//!
//! 1. **Credential storage / login handoff** — the `coda-auth` crate handles
//!    OAuth PKCE and device-code flows.  The Rust `App` does not yet wire up
//!    `/login` to the actual auth flow; the wizard surfaces the correct
//!    instruction and marks the seam clearly.
//!
//! 2. **Session persistence** — `/resume` is not yet implemented.  The wizard
//!    does not attempt session restoration.
//!
//! When the seams close, this module gains real login by calling the existing
//! `coda-auth` login RPCs through the engine connection, matching the C#
//! `LoginCommand.ExecuteAsync` call.

use crate::config::{Paths, Settings};

/// A detected provider that the user can log in to.
#[derive(Debug, Clone)]
pub struct ProviderDescriptor {
    /// The provider id used in settings and login commands.
    pub id: &'static str,
    /// The display name shown in the wizard picker.
    pub display_name: &'static str,
}

/// The providers the wizard offers.
///
/// These are the same two providers the C# wizard surfaces.
pub const PROVIDERS: &[ProviderDescriptor] = &[
    ProviderDescriptor {
        id: "claude",
        display_name: "Anthropic Claude",
    },
    ProviderDescriptor {
        id: "copilot",
        display_name: "GitHub Copilot",
    },
];

/// Returns `true` when this appears to be a first run.
///
/// A first run is defined as: no provider has a non-empty `apiKey` in
/// `settings.json`, and `ANTHROPIC_API_KEY` is not set.
///
/// This mirrors `FirstRunDetector.IsFirstRunAsync` in C#.
pub fn is_first_run(paths: &Paths) -> bool {
    // If the ANTHROPIC_API_KEY environment variable is set, the user already
    // has a way to connect — not a first run.
    if std::env::var("ANTHROPIC_API_KEY").is_ok_and(|v| !v.is_empty()) {
        return false;
    }

    // If settings exist and contain any api key for a known provider, not first run.
    match Settings::load(paths) {
        Ok(settings) => {
            // If there is a default provider configured, assume not first run.
            settings.default_provider().is_none()
        }
        // Settings file not found — definitely first run.
        Err(_) => true,
    }
}

/// The text shown in the wizard welcome notice.
pub const WELCOME_TEXT: &str = "\
Welcome to Coda! Let's connect it to an LLM so you can start chatting.\n\
\n\
Run /setup to choose a provider and log in.";

/// The provider-selection prompt presented during setup.
pub fn provider_selection_prompt() -> String {
    let options: Vec<String> = PROVIDERS
        .iter()
        .enumerate()
        .map(|(i, p)| format!("{}. {} ({})", i + 1, p.display_name, p.id))
        .collect();
    format!("Choose a provider:\n{}", options.join("\n"))
}

/// The login-handoff notice for the given provider.
///
/// **Seam**: actual browser-loopback / device-code login is not yet wired up
/// in the Rust front-end.  When `coda-auth` login RPCs are available via the
/// engine, replace this notice with a real login sequence.
pub fn login_seam_notice(provider: &ProviderDescriptor) -> String {
    format!(
        "To connect to {name}, run: /login {id}\n\
        \n\
        [SEAM: real OAuth login handoff depends on coda-auth login RPCs \
        being added by the coda-auth / coda-serve porting agent.  \
        Once those RPCs are available, the wizard will call them directly \
        instead of surfacing this instruction.]",
        name = provider.display_name,
        id = provider.id,
    )
}

/// Finds a provider descriptor by id (case-insensitive).
pub fn find_provider(id: &str) -> Option<&'static ProviderDescriptor> {
    PROVIDERS.iter().find(|p| p.id.eq_ignore_ascii_case(id))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn providers_list_is_not_empty() {
        assert!(!PROVIDERS.is_empty());
    }

    #[test]
    fn find_provider_by_exact_id() {
        let p = find_provider("claude").unwrap();
        assert_eq!(p.id, "claude");
    }

    #[test]
    fn find_provider_case_insensitive() {
        let p = find_provider("COPILOT").unwrap();
        assert_eq!(p.id, "copilot");
    }

    #[test]
    fn find_unknown_provider_returns_none() {
        assert!(find_provider("unknown-llm").is_none());
    }

    #[test]
    fn login_seam_notice_contains_provider_id() {
        let p = find_provider("claude").unwrap();
        let notice = login_seam_notice(p);
        assert!(notice.contains("claude"));
        assert!(notice.contains("SEAM"));
    }

    #[test]
    fn provider_selection_prompt_contains_all_providers() {
        let prompt = provider_selection_prompt();
        for p in PROVIDERS {
            assert!(
                prompt.contains(p.id),
                "missing provider id {}: {prompt:?}",
                p.id
            );
        }
    }

    #[test]
    fn welcome_text_contains_setup_command() {
        assert!(WELCOME_TEXT.contains("/setup"));
    }
}
