//! OS token broker abstraction and the process-wide broker token cache.
//!
//! [`TokenBroker`] is implemented by the Windows Web Account Manager broker on
//! Windows, by [`UnavailableBroker`] everywhere else (and when disabled with
//! `CODA_DISABLE_WAM`), and by fakes in tests. The provider in
//! `entra_broker.rs` only talks to `Arc<dyn TokenBroker>`.

use std::collections::HashMap;
use std::future::Future;
use std::sync::{Arc, OnceLock};
use std::time::Duration;

use async_trait::async_trait;
use coda_auth::secret::Secret;
use serde::{Deserialize, Serialize};

/// One token request to the broker.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BrokerRequest {
    /// Authority URL, e.g. `https://login.microsoftonline.com/<tenant>`.
    pub authority: String,
    pub client_id: String,
    pub scopes: Vec<String>,
    /// Configured account (UPN) to prefer.
    pub login_hint: Option<String>,
    /// Broker account id remembered from an earlier acquisition.
    pub account_id: Option<String>,
    /// Bypass the broker's own token cache: set only when re-acquiring after
    /// the server rejected a token with 401.
    pub force_refresh: bool,
}

/// The account a broker token was issued to (non-secret).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BrokerAccount {
    pub id: String,
    pub username: String,
}

/// An access token issued by the broker. `Debug` never shows the token.
#[derive(Debug, Clone)]
pub struct BrokerToken {
    pub access_token: Secret<String>,
    pub expires_at_unix: i64,
    pub account: Option<BrokerAccount>,
}

#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum BrokerError {
    #[error("interaction required: {0}")]
    InteractionRequired(String),
    #[error("sign-in was cancelled")]
    Cancelled,
    #[error("Windows broker unavailable: {0}")]
    Unavailable(String),
    #[error("Windows broker did not respond within {0:?}")]
    Timeout(Duration),
    #[error("{0}")]
    Failed(String),
}

/// An OS token broker (WAM on Windows).
#[async_trait]
pub trait TokenBroker: Send + Sync {
    fn is_available(&self) -> bool;
    /// Whether an interactive prompt can be parented to a window right now.
    fn can_interact(&self) -> bool;
    async fn acquire_silent(&self, request: &BrokerRequest) -> Result<BrokerToken, BrokerError>;
    async fn acquire_interactive(
        &self,
        request: &BrokerRequest,
    ) -> Result<BrokerToken, BrokerError>;
}

/// A broker that is never available (non-Windows, or disabled).
#[derive(Debug, Clone)]
pub struct UnavailableBroker {
    pub reason: String,
}

impl UnavailableBroker {
    pub fn new(reason: impl Into<String>) -> Self {
        Self { reason: reason.into() }
    }
}

#[async_trait]
impl TokenBroker for UnavailableBroker {
    fn is_available(&self) -> bool {
        false
    }

    fn can_interact(&self) -> bool {
        false
    }

    async fn acquire_silent(&self, _: &BrokerRequest) -> Result<BrokerToken, BrokerError> {
        Err(BrokerError::Unavailable(self.reason.clone()))
    }

    async fn acquire_interactive(&self, _: &BrokerRequest) -> Result<BrokerToken, BrokerError> {
        Err(BrokerError::Unavailable(self.reason.clone()))
    }
}

/// The platform broker, or an unavailable one when `CODA_DISABLE_WAM` is truthy.
pub fn default_broker() -> Arc<dyn TokenBroker> {
    if is_truthy(std::env::var("CODA_DISABLE_WAM").ok().as_deref()) {
        return Arc::new(UnavailableBroker::new("disabled by CODA_DISABLE_WAM"));
    }
    platform_broker()
}

// The single place where the Windows WAM broker is plugged in.
#[cfg(windows)]
fn platform_broker() -> Arc<dyn TokenBroker> {
    Arc::new(UnavailableBroker::new("the Windows broker is not supported in this build"))
}

#[cfg(not(windows))]
fn platform_broker() -> Arc<dyn TokenBroker> {
    Arc::new(UnavailableBroker::new("not Windows"))
}

fn is_truthy(value: Option<&str>) -> bool {
    value.is_some_and(|v| {
        matches!(v.trim().to_ascii_lowercase().as_str(), "1" | "true" | "yes" | "on")
    })
}

/// Bound a broker call; an elapsed deadline becomes [`BrokerError::Timeout`].
pub async fn with_timeout<F>(limit: Duration, f: F) -> Result<BrokerToken, BrokerError>
where
    F: Future<Output = Result<BrokerToken, BrokerError>>,
{
    tokio::time::timeout(limit, f).await.unwrap_or(Err(BrokerError::Timeout(limit)))
}

/// Cache key: canonical resource + configured account (lower-cased) or `<default>`.
pub fn cache_key(resource: &str, account: Option<&str>) -> String {
    format!("{resource}\n{}", account_label(account))
}

/// Credential-store key of the persisted broker account for a resource and
/// configured account.
pub fn account_store_key(resource: &str, account: Option<&str>) -> String {
    format!("mcp-broker-account:{resource}#{}", account_label(account))
}

fn account_label(account: Option<&str>) -> String {
    account
        .map(str::trim)
        .filter(|a| !a.is_empty())
        .map(str::to_lowercase)
        .unwrap_or_else(|| "<default>".to_owned())
}

/// Process-wide broker token cache, so a restarted MCP connection (a new
/// provider) reuses the token and the single-flight lock.
#[derive(Default)]
pub struct BrokerTokenCache {
    entries: std::sync::Mutex<HashMap<String, Arc<CacheEntry>>>,
}

impl BrokerTokenCache {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn global() -> Arc<Self> {
        static GLOBAL: OnceLock<Arc<BrokerTokenCache>> = OnceLock::new();
        Arc::clone(GLOBAL.get_or_init(|| Arc::new(Self::new())))
    }

    pub fn entry(&self, key: &str) -> Arc<CacheEntry> {
        let mut entries = self.entries.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        Arc::clone(entries.entry(key.to_owned()).or_default())
    }
}

/// One cache slot. `flight` serialises broker calls for the key; `state` is
/// only held briefly and never across an `.await`.
#[derive(Default)]
pub struct CacheEntry {
    pub flight: tokio::sync::Mutex<()>,
    pub state: std::sync::Mutex<CachedState>,
}

impl CacheEntry {
    /// Lock `state`, recovering it if a holder panicked.
    pub fn state(&self) -> std::sync::MutexGuard<'_, CachedState> {
        self.state.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
    }
}

#[derive(Debug, Default)]
pub struct CachedState {
    pub token: Option<BrokerToken>,
    /// The account the broker last returned (or the persisted one).
    pub account: Option<BrokerAccount>,
    /// An interactive prompt was cancelled or failed; do not re-show it
    /// until the user runs `/mcp login`.
    pub interaction_declined: bool,
    /// The user signed out; no automatic acquisition until the next login.
    pub signed_out: bool,
    pub last_error: Option<String>,
    /// When the last broker acquisition failed; automatic refreshes are
    /// skipped for a cool-down after it.
    pub last_failure_at: Option<tokio::time::Instant>,
    /// Incremented whenever a new token is cached.
    pub generation: u64,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cache_key_lowercases_account_and_defaults() {
        assert_eq!(cache_key("https://r", Some("User@Contoso.COM")), "https://r\nuser@contoso.com");
        assert_eq!(cache_key("https://r", None), "https://r\n<default>");
        assert_eq!(cache_key("https://r", Some("  ")), "https://r\n<default>");
        assert_eq!(
            cache_key("https://r", Some("a@b.c")),
            cache_key("https://r", Some("A@B.C"))
        );
        assert_ne!(cache_key("https://r", Some("a@b.c")), cache_key("https://r", None));
    }

    #[test]
    fn account_store_key_includes_the_account() {
        assert_eq!(account_store_key("https://r", Some("A@B.C")), "mcp-broker-account:https://r#a@b.c");
        assert_ne!(account_store_key("https://r", Some("a@b.c")), account_store_key("https://r", None));
    }

    #[test]
    fn cache_entries_are_shared_per_key() {
        let cache = BrokerTokenCache::new();
        let a = cache.entry("k");
        a.state().signed_out = true;
        assert!(cache.entry("k").state().signed_out);
        assert!(!cache.entry("other").state().signed_out);
        assert!(Arc::ptr_eq(&BrokerTokenCache::global(), &BrokerTokenCache::global()));
    }

    #[tokio::test(start_paused = true)]
    async fn with_timeout_maps_elapsed_deadline_to_timeout() {
        let limit = Duration::from_secs(30);
        let never = std::future::pending::<Result<BrokerToken, BrokerError>>();
        assert_eq!(with_timeout(limit, never).await.unwrap_err(), BrokerError::Timeout(limit));

        let ready = async { Err(BrokerError::Cancelled) };
        assert_eq!(with_timeout(limit, ready).await.unwrap_err(), BrokerError::Cancelled);
    }

    #[test]
    fn truthy_values() {
        for v in ["1", "true", "TRUE", " yes ", "on"] {
            assert!(is_truthy(Some(v)), "{v}");
        }
        for v in ["0", "false", "", "no"] {
            assert!(!is_truthy(Some(v)), "{v}");
        }
        assert!(!is_truthy(None));
    }

    #[tokio::test]
    async fn unavailable_broker_never_issues_tokens() {
        let b = UnavailableBroker::new("not Windows");
        assert!(!b.is_available());
        assert!(!b.can_interact());
        let req = BrokerRequest {
            authority: "https://login.microsoftonline.com/t".into(),
            client_id: "c".into(),
            scopes: vec![],
            login_hint: None,
            account_id: None,
            force_refresh: false,
        };
        assert_eq!(
            b.acquire_silent(&req).await.unwrap_err(),
            BrokerError::Unavailable("not Windows".into())
        );
    }
}
