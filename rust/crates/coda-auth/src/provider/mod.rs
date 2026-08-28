//! [`AuthProvider`] trait and its built-in implementations.

use async_trait::async_trait;

use crate::credential::Credential;
use crate::error::AuthError;

pub use self::api_key::ApiKeyProvider;
pub use self::claude_ai::ClaudeAiProvider;
pub use self::copilot::CopilotProvider;

pub mod api_key;
pub mod claude_ai;
pub mod copilot;

/// A callback that the host must provide to display the OAuth authorization URL.
///
/// On an interactive desktop this opens the system browser; on headless
/// systems it can print the URL to stderr or display it in a TUI.
pub type OpenBrowserFn = Box<dyn Fn(&str) -> Result<(), AuthError> + Send + Sync>;

/// Prompt information for the device-code flow.
#[derive(Debug, Clone)]
pub struct DeviceCodePrompt {
    /// The short code the user must enter at `verification_uri`.
    pub user_code: String,
    /// The URL the user must visit (e.g. `https://github.com/login/device`).
    pub verification_uri: String,
    /// A pre-filled URL combining `verification_uri` and `user_code`, when
    /// the authorization server supplies it.
    pub verification_uri_complete: Option<String>,
    /// How long before the device code expires.
    pub expires_in: std::time::Duration,
    /// The server-recommended polling interval.
    pub interval: std::time::Duration,
}

/// A pluggable authentication strategy for one provider.
///
/// Producing the auth headers and driving the interactive login both live
/// here so providers with different flows (PKCE ↔ device-code ↔ API key)
/// share one contract.
#[async_trait]
pub trait AuthProvider: Send + Sync {
    /// Stable provider id (e.g. `"claude-ai"`, `"github-copilot"`).
    fn provider_id(&self) -> &str;

    /// Returns `true` when the stored credential should be refreshed before use.
    fn needs_refresh(&self, credential: &Credential) -> bool;

    /// Exchange the current credential for a refreshed one.
    ///
    /// Called only when `needs_refresh` returns `true` AND there is a refresh
    /// token.
    async fn refresh(&self, credential: &Credential) -> Result<Credential, AuthError>;

    /// The HTTP headers this credential contributes to a provider request.
    fn auth_headers(&self, credential: &Credential) -> Result<Vec<(String, String)>, AuthError>;
}
