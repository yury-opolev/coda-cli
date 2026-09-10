//! The three account identities this product stores credentials for, and the
//! one place their names are canonicalised.
//!
//! # Why an identity is not a transport
//!
//! Two of the three identities speak to the Anthropic Messages API. They are
//! still different accounts: a Claude.ai subscription (`claude-ai`, OAuth) and
//! a console API key (`anthropic-api-key`). Sharing the HTTP transport must
//! never collapse them, because the engine's public provider id decides which
//! saved model row, which effort preference and which "signed in as" line the
//! user sees.
//!
//! The two ids that differ deliberately:
//!
//! | identity            | stored key suffix     | engine / settings id |
//! |---------------------|-----------------------|----------------------|
//! | Claude.ai           | `claude-ai`           | `claude-ai`          |
//! | Anthropic API key   | `anthropic-api-key`   | `anthropic`          |
//! | GitHub Copilot      | `github-copilot`      | `github-copilot`     |
//!
//! [`ProviderIdentity::parse`] is the single alias table. Callers that used to
//! canonicalise names themselves delegate here so a name accepted by the CLI
//! cannot mean something else to the engine.

use crate::provider::{api_key, claude_ai, copilot};

/// One of the three accounts this product can be signed in to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum ProviderIdentity {
    /// Claude.ai subscription (OAuth).
    ClaudeAi,
    /// Anthropic console API key.
    AnthropicApiKey,
    /// GitHub Copilot.
    GithubCopilot,
}

impl ProviderIdentity {
    /// Every identity, in a fixed order. Status enumerates all of them: a
    /// report that stops at the first credential cannot show the second one
    /// that should not be there.
    pub const ALL: [ProviderIdentity; 3] =
        [Self::ClaudeAi, Self::AnthropicApiKey, Self::GithubCopilot];

    /// The id under which this identity's credential is stored
    /// (`llmauth:<stored_id>`).
    pub fn stored_id(self) -> &'static str {
        match self {
            Self::ClaudeAi => claude_ai::PROVIDER_ID,
            Self::AnthropicApiKey => api_key::PROVIDER_ID,
            Self::GithubCopilot => copilot::PROVIDER_ID,
        }
    }

    /// The id the engine and `settings.json` use for this identity.
    ///
    /// Equal to [`Self::stored_id`] except for the API key, whose engine id is
    /// `anthropic` — the name the client, the model rows and `defaultProvider`
    /// have always used.
    pub fn engine_id(self) -> &'static str {
        match self {
            Self::ClaudeAi => "claude-ai",
            Self::AnthropicApiKey => "anthropic",
            Self::GithubCopilot => "github-copilot",
        }
    }

    /// A short label for a user-facing line.
    pub fn label(self) -> &'static str {
        match self {
            Self::ClaudeAi => "Claude.ai subscription",
            Self::AnthropicApiKey => "Anthropic API key",
            Self::GithubCopilot => "GitHub Copilot",
        }
    }

    /// The settings keys that may hold this identity's saved model, most
    /// canonical first.
    ///
    /// The API-key identity has two: `anthropic` is what is written now, and
    /// `anthropic-api-key` is what an older build wrote. Reading only the
    /// canonical row would silently change a user's model.
    pub fn settings_model_keys(self) -> &'static [&'static str] {
        match self {
            Self::ClaudeAi => &["claude-ai"],
            Self::AnthropicApiKey => &["anthropic", "anthropic-api-key"],
            Self::GithubCopilot => &["github-copilot"],
        }
    }

    /// Resolve a user-facing name or alias.
    ///
    /// Returns `None` for anything unrecognised: an unknown name must be
    /// refused where it was typed, never rewritten into a provider that
    /// happens to work.
    pub fn parse(raw: &str) -> Option<Self> {
        match raw.trim().to_ascii_lowercase().as_str() {
            "anthropic" | "anthropic-api-key" | "api-key" | "apikey" => Some(Self::AnthropicApiKey),
            "claude-ai" | "claude" | "claudeai" | "anthropic-subscription" | "subscription" => {
                Some(Self::ClaudeAi)
            }
            "github-copilot" | "copilot" | "github" => Some(Self::GithubCopilot),
            _ => None,
        }
    }

    /// The identity whose credential is stored under `stored_id`, if any.
    pub fn from_stored_id(stored_id: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|identity| identity.stored_id() == stored_id)
    }

    /// The store key for this identity's credential.
    pub fn store_key(self) -> String {
        format!("llmauth:{}", self.stored_id())
    }
}

impl std::fmt::Display for ProviderIdentity {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.engine_id())
    }
}

/// Canonicalise a provider name for engine and settings use.
///
/// Unknown names are lower-cased and returned unchanged so that selection
/// rejects them explicitly instead of silently substituting a provider that
/// would work. This is the function the engine's own alias helper delegates
/// to, so the CLI, the TUI and the engine cannot disagree about a name.
pub fn canonical_engine_provider(raw: &str) -> String {
    match ProviderIdentity::parse(raw) {
        Some(identity) => identity.engine_id().to_owned(),
        None => raw.trim().to_ascii_lowercase(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_stored_and_engine_ids_only_differ_for_the_api_key() {
        for identity in ProviderIdentity::ALL {
            if identity == ProviderIdentity::AnthropicApiKey {
                assert_ne!(identity.stored_id(), identity.engine_id());
            } else {
                assert_eq!(identity.stored_id(), identity.engine_id());
            }
        }
    }

    #[test]
    fn an_unknown_name_is_lowercased_but_never_rewritten() {
        assert_eq!(canonical_engine_provider(" OpenAI "), "openai");
        assert_eq!(canonical_engine_provider("api-key"), "anthropic");
    }

    #[test]
    fn stored_ids_round_trip() {
        for identity in ProviderIdentity::ALL {
            assert_eq!(ProviderIdentity::from_stored_id(identity.stored_id()), Some(identity));
            assert_eq!(identity.store_key(), format!("llmauth:{}", identity.stored_id()));
        }
        assert_eq!(ProviderIdentity::from_stored_id("anthropic"), None);
    }
}
