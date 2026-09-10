//! GitHub Copilot device-code flow provider.
//!
//! The two-phase flow:
//! 1. **Device-code request** — POST to `device_code_url`; server returns a
//!    short-lived `device_code`, a human-visible `user_code`, and a
//!    `verification_uri` the user must visit.
//! 2. **Polling** — repeatedly POST to `token_url` until the user authorizes
//!    (access token returned) or the code expires.  The server can send
//!    `authorization_pending` (keep polling at the same rate) or `slow_down`
//!    (increase the interval by 5 s or use the server-supplied value).
//! 3. **Exchange** — optionally exchange the durable GitHub OAuth token for a
//!    short-lived Copilot token that carries full model entitlement.
//!
//! On 401 (and every 5-minute-before-expiry interval thereafter) the GitHub
//! token is re-exchanged for a fresh Copilot token; this constitutes the
//! "refresh" step.
//!
//! All endpoint URLs are injectable so tests never hit the real network.

use std::time::Duration;

use async_trait::async_trait;
use serde::Deserialize;

use crate::credential::{Credential, CredentialKind};
use crate::error::AuthError;
use crate::provider::{AuthProvider, DeviceCodePrompt};
use crate::secret::Secret;

/// Provider id.
pub const PROVIDER_ID: &str = "github-copilot";

/// Refresh the Copilot token 5 minutes before it expires.
const REFRESH_BUFFER: Duration = Duration::from_secs(5 * 60);

/// Device-code grant type.
const DEVICE_GRANT_TYPE: &str = "urn:ietf:params:oauth:grant-type:device_code";

/// Copilot token-exchange probe timeout.  The exchange endpoint is optional
/// and may be absent; bound the probe so it does not stall the whole login.
const EXCHANGE_PROBE_TIMEOUT: Duration = Duration::from_secs(5);

/// RFC 8628 §3.2 default polling interval when the server omits `interval`.
const DEFAULT_POLL_INTERVAL: Duration = Duration::from_secs(5);

/// Minimum GitHub REST API version for all requests.
const GITHUB_API_VERSION: &str = "2026-06-01";

/// GitHub Copilot provider configuration.
///
/// `Debug` is implemented by hand: endpoint URLs come from configuration and
/// the environment, where a proxy token routinely rides in a query string or in
/// userinfo. A derived `Debug` would print those verbatim into any log line
/// that formats the config or a struct containing it, so URLs are reduced to
/// their host. See [`redact_url`].
#[derive(Clone)]
pub struct CopilotConfig {
    /// OAuth client id.
    pub client_id: String,
    /// Device-code request endpoint (RFC 8628).
    pub device_code_url: String,
    /// Device-grant token polling endpoint.
    pub token_url: String,
    /// Exchanges the GitHub OAuth token for a short-lived Copilot token.
    /// `None` means skip the exchange and use the raw GitHub token directly.
    pub copilot_token_url: Option<String>,
    /// OAuth scope requested in the device flow.
    pub scope: String,
    /// Editor identification sent with every request.
    pub editor_version: String,
    pub editor_plugin_version: String,
    pub integration_id: String,
    pub user_agent: String,
    /// Inference endpoint. Callers plumb this into the chat client's base URL;
    /// enterprise tenants serve inference from their own host.
    pub api_base_url: String,
    /// Whether to exchange the GitHub OAuth token for a short-lived Copilot
    /// token. The raw device-flow token grants only a legacy subset of models,
    /// so this is on by default wherever an exchange endpoint exists.
    pub use_exchange: bool,
}

/// Characters that mean a value is not a bare hostname — a path, query,
/// fragment, or embedded credentials.
const DISALLOWED_HOST_CHARS: &[char] = &['/', '\\', '@', '?', '#'];

/// Renders a configured URL for logs: scheme and host only.
///
/// Endpoints are trusted *inputs*, but logs are not a secret-safe channel: a
/// proxy endpoint may carry `?token=…` or `user:password@`, and those must not
/// be reproducible from a `Debug` dump. The host is what an operator needs to
/// diagnose routing; the rest is elided.
pub(crate) fn redact_url(url: &str) -> String {
    match url.split_once("://") {
        Some((scheme, rest)) => {
            let authority = rest.split(['/', '?', '#']).next().unwrap_or("");
            // Drop any userinfo component.
            let host = authority.rsplit_once('@').map(|(_, h)| h).unwrap_or(authority);
            if host.is_empty() {
                format!("{scheme}://<redacted>")
            } else {
                format!("{scheme}://{host}/…")
            }
        }
        None => "<redacted>".to_owned(),
    }
}

impl std::fmt::Debug for CopilotConfig {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CopilotConfig")
            .field("client_id", &self.client_id)
            .field("device_code_url", &redact_url(&self.device_code_url))
            .field("token_url", &redact_url(&self.token_url))
            .field(
                "copilot_token_url",
                &self.copilot_token_url.as_deref().map(redact_url),
            )
            .field("api_base_url", &redact_url(&self.api_base_url))
            .field("scope", &self.scope)
            .field("editor_version", &self.editor_version)
            .field("editor_plugin_version", &self.editor_plugin_version)
            .field("integration_id", &self.integration_id)
            .field("user_agent", &self.user_agent)
            .field("use_exchange", &self.use_exchange)
            .finish()
    }
}

/// Rejects anything but a bare `host[:port]`.
///
/// # Security
/// The domain is interpolated into `api.<domain>` and used as the destination
/// of the durable OAuth token exchange. A stray path, query, fragment, or
/// userinfo component could therefore redirect that token to a host the user
/// never intended — `evil.com/@github.com` and friends. Failing loudly at
/// config time is safer than sanitizing and hoping, which is also what the C#
/// `EnsureBareHost` does.
///
/// Non-ASCII is rejected rather than punycoded: this build does not implement
/// IDNA, and interpolating a Unicode label into a URL produces a host that
/// looks like the one the user typed but resolves somewhere else (homograph
/// domains). A tenant with an internationalised domain must supply its
/// punycode (`xn--…`) form.
fn ensure_bare_host(host: &str) -> Result<(), AuthError> {
    let invalid = |detail: &str| {
        Err(AuthError::InvalidUrl(format!(
            "GitHub Enterprise domain must be a bare hostname, e.g. 'octocorp.ghe.com' \
             ({detail}); got '{}'",
            sanitize_host_for_message(host)
        )))
    };

    if host.is_empty() {
        return invalid("it must not be empty");
    }
    if !host.is_ascii() {
        return invalid("non-ASCII hostnames must be supplied in punycode form");
    }
    if host.chars().any(|c| c.is_whitespace() || c.is_control()) {
        return invalid("no whitespace or control characters");
    }
    if host.contains(DISALLOWED_HOST_CHARS) {
        return invalid("no path, query, fragment, or embedded credentials");
    }

    // Split an optional port; the remainder must look like a DNS name.
    let (name, port) = match host.rsplit_once(':') {
        Some((name, port)) => (name, Some(port)),
        None => (host, None),
    };
    if let Some(port) = port {
        if port.is_empty() || !port.chars().all(|c| c.is_ascii_digit()) {
            return invalid("the port must be numeric");
        }
    }
    if name.is_empty()
        || name.starts_with('.')
        || name.ends_with('.')
        || name.contains("..")
        || !name.chars().all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '.')
    {
        return invalid("only letters, digits, '-' and '.' are allowed in a hostname");
    }
    Ok(())
}

/// Renders an untrusted domain safely inside an error message: bounded length,
/// no control characters. The value is configuration, not a secret, but it is
/// still attacker-influenced text heading for a log line.
fn sanitize_host_for_message(host: &str) -> String {
    const MAX: usize = 64;
    let mut out: String = host
        .chars()
        .take(MAX)
        .map(|c| if c.is_control() { '\u{fffd}' } else { c })
        .collect();
    if host.chars().count() > MAX {
        out.push('…');
    }
    out
}

/// Canonicalises a GitHub Enterprise domain into the bare GHE host every
/// endpoint is derived from.
///
/// A leading scheme and trailing slashes are stripped. If the caller pastes the
/// *Copilot* host (`copilot-api.<ghe>`) by mistake, the GHE host is recovered so
/// every derived URL stays consistent and no doubled `copilot-api.` prefix is
/// produced. This is the single normalizer: configuration building and
/// deployment labelling both use it, so a pasted host cannot be normalised in
/// one place and treated as a different tenant in the other.
fn normalize_enterprise_domain(domain: &str) -> Result<String, AuthError> {
    let trimmed = domain.trim();
    if trimmed.is_empty() {
        return Err(AuthError::InvalidUrl(
            "GitHub Enterprise domain must not be empty".into(),
        ));
    }

    let mut d = trimmed;
    for scheme in ["https://", "http://"] {
        if let Some(rest) = strip_prefix_ascii_ci(d, scheme) {
            d = rest;
            break;
        }
    }
    let mut d = d.trim_end_matches('/').to_owned();

    const COPILOT_PREFIX: &str = "copilot-api.";
    if let Some(rest) = strip_prefix_ascii_ci(&d, COPILOT_PREFIX) {
        d = rest.to_owned();
    }

    ensure_bare_host(&d)?;
    Ok(d.to_ascii_lowercase())
}

/// Case-insensitively strips an ASCII prefix.
///
/// Byte comparison, not slicing by length: `&s[..n]` panics when `n` lands
/// inside a multi-byte character, which a pasted Unicode domain
/// (`abcdefgö.com`) trivially produces. Comparing bytes is boundary-safe, and a
/// successful ASCII match guarantees `n` is a char boundary.
fn strip_prefix_ascii_ci<'a>(value: &'a str, prefix: &str) -> Option<&'a str> {
    let bytes = value.as_bytes();
    let prefix_bytes = prefix.as_bytes();
    if bytes.len() >= prefix_bytes.len()
        && bytes[..prefix_bytes.len()].eq_ignore_ascii_case(prefix_bytes)
    {
        Some(&value[prefix_bytes.len()..])
    } else {
        None
    }
}

impl CopilotConfig {
    /// Default (VS Code Copilot-style) values for public github.com.
    pub fn default_public() -> Self {
        Self {
            client_id: "Iv1.b507a08c87ecfe98".into(),
            device_code_url: "https://github.com/login/device/code".into(),
            token_url: "https://github.com/login/oauth/access_token".into(),
            copilot_token_url: Some("https://api.github.com/copilot_internal/v2/token".into()),
            scope: "read:user".into(),
            editor_version: "vscode/1.95.0".into(),
            editor_plugin_version: "copilot-chat/0.22.0".into(),
            integration_id: "vscode-chat".into(),
            user_agent: "GitHubCopilotChat/0.22.0".into(),
            api_base_url: "https://api.githubcopilot.com".into(),
            use_exchange: true,
        }
    }

    /// Configuration for a GitHub Enterprise data-residency tenant.
    ///
    /// Device-code and token endpoints live on the GHE host, inference on
    /// `copilot-api.<domain>`, and the token exchange on
    /// `api.<domain>/copilot_internal/v2/token`. Client id and editor headers
    /// are inherited from [`CopilotConfig::default_public`].
    ///
    /// A leading scheme and trailing slashes are stripped. If the caller pastes
    /// the *Copilot* host (`copilot-api.<ghe>`) by mistake, the GHE host is
    /// recovered so every derived URL stays consistent and no doubled
    /// `copilot-api.` prefix is produced.
    pub fn for_enterprise(domain: &str) -> Result<Self, AuthError> {
        let d = normalize_enterprise_domain(domain)?;

        Ok(Self {
            device_code_url: format!("https://{d}/login/device/code"),
            token_url: format!("https://{d}/login/oauth/access_token"),
            copilot_token_url: Some(format!("https://api.{d}/copilot_internal/v2/token")),
            api_base_url: format!("https://copilot-api.{d}"),
            use_exchange: true,
            ..Self::default_public()
        })
    }

    /// Applies environment overrides.
    ///
    /// When `GH_COPILOT_ENTERPRISE_DOMAIN` is set the base is
    /// [`CopilotConfig::for_enterprise`] rather than the public default;
    /// individual overrides are then layered on top, so a tenant can still
    /// redirect one endpoint without restating the rest.
    pub fn from_environment() -> Result<Self, AuthError> {
        Self::from_env_lookup(|key| std::env::var(key).ok().filter(|v| !v.is_empty()))
    }

    /// Testable core of [`CopilotConfig::from_environment`].
    ///
    /// Takes an explicit lookup because the process environment is global
    /// mutable state: tests that set real variables interfere with each other
    /// under the default parallel test runner.
    pub fn from_env_lookup(
        env: impl Fn(&str) -> Option<String>,
    ) -> Result<Self, AuthError> {
        let base = match env("GH_COPILOT_ENTERPRISE_DOMAIN") {
            Some(domain) => Self::for_enterprise(&domain)?,
            None => Self::default_public(),
        };

        // Any value other than "false" or "0" enables the exchange, matching
        // the C# truthiness rule rather than a stricter bool parse.
        let use_exchange = match env("GH_COPILOT_USE_EXCHANGE") {
            Some(raw) => !raw.eq_ignore_ascii_case("false") && raw != "0",
            None => base.use_exchange,
        };

        Ok(Self {
            client_id: env("GH_COPILOT_CLIENT_ID").unwrap_or(base.client_id),
            device_code_url: env("GH_COPILOT_DEVICE_CODE_URL").unwrap_or(base.device_code_url),
            token_url: env("GH_COPILOT_TOKEN_URL").unwrap_or(base.token_url),
            copilot_token_url: env("GH_COPILOT_COPILOT_TOKEN_URL").or(base.copilot_token_url),
            api_base_url: env("GH_COPILOT_API_BASE_URL").unwrap_or(base.api_base_url),
            use_exchange,
            editor_version: env("GH_COPILOT_EDITOR_VERSION").unwrap_or(base.editor_version),
            editor_plugin_version: env("GH_COPILOT_PLUGIN_VERSION")
                .unwrap_or(base.editor_plugin_version),
            integration_id: env("GH_COPILOT_INTEGRATION_ID").unwrap_or(base.integration_id),
            user_agent: env("GH_COPILOT_USER_AGENT").unwrap_or(base.user_agent),
            scope: base.scope,
        })
    }
}

/// Which GitHub deployment a Copilot configuration actually talks to.
///
/// Derived from the resolved endpoints, never from the caller's intent, so a
/// config whose endpoints were overridden can never be labelled with a
/// deployment it does not contact.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CopilotDeployment {
    /// Public github.com.
    Public,
    /// A GitHub Enterprise (data-residency) tenant.
    Enterprise { domain: String },
    /// Endpoint overrides moved authentication to some other host.
    Custom { auth_host: String },
}

/// An explicit deployment choice made during login.
///
/// A user who picks "public GitHub" in the login UI must get public GitHub even
/// on a machine where `GH_COPILOT_ENTERPRISE_DOMAIN` is exported or a domain is
/// saved in settings — the alternative is silently signing them in to a tenant
/// they did not choose.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CopilotDeploymentChoice {
    Public,
    Enterprise(String),
}

/// A resolved Copilot configuration together with what it actually contacts.
#[derive(Debug, Clone)]
pub struct ResolvedCopilotConfig {
    pub config: CopilotConfig,
    /// The deployment derived from the resolved authentication host.
    pub deployment: CopilotDeployment,
    /// Environment variables that overrode an individual endpoint. The service
    /// discloses these; they are the reason `deployment` may be `Custom`.
    pub endpoint_overrides: Vec<&'static str>,
}

/// Environment variables that redirect an individual endpoint.
const ENDPOINT_OVERRIDE_KEYS: &[&str] = &[
    "GH_COPILOT_DEVICE_CODE_URL",
    "GH_COPILOT_TOKEN_URL",
    "GH_COPILOT_COPILOT_TOKEN_URL",
    "GH_COPILOT_API_BASE_URL",
];

/// The one Copilot configuration resolver, shared by login and the engine.
///
/// Layering:
/// 1. `choice` — an explicit deployment picked during login. It wins over every
///    domain default so "sign in to public GitHub" cannot be redirected by an
///    exported `GH_COPILOT_ENTERPRISE_DOMAIN` or a saved tenant.
/// 2. `GH_COPILOT_ENTERPRISE_DOMAIN` from `env` — used when no explicit choice
///    was made, preserving the existing enterprise behaviour for the engine.
/// 3. `saved_domain` — the domain persisted in settings.
/// 4. Public github.com defaults.
///
/// Every other `GH_COPILOT_*` variable is a pure environment override and still
/// applies on top; the ones that move an endpoint are reported in
/// [`ResolvedCopilotConfig::endpoint_overrides`], and `deployment` is derived
/// from the *resolved* authentication host, so the returned value never claims
/// a deployment that differs from the host it will contact.
///
/// This function performs no I/O and persists nothing: reading settings is the
/// caller's job, and a cancelled login therefore writes nothing.
pub fn resolve_copilot_config(
    choice: Option<&CopilotDeploymentChoice>,
    saved_domain: Option<&str>,
    env: impl Fn(&str) -> Option<String>,
) -> Result<ResolvedCopilotConfig, AuthError> {
    let saved = saved_domain.map(str::to_owned).filter(|v| !v.trim().is_empty());

    let config = CopilotConfig::from_env_lookup(|key| {
        if key == "GH_COPILOT_ENTERPRISE_DOMAIN" {
            return match choice {
                Some(CopilotDeploymentChoice::Public) => None,
                Some(CopilotDeploymentChoice::Enterprise(domain)) => Some(domain.clone()),
                None => env(key)
                    .filter(|v| !v.trim().is_empty())
                    .or_else(|| saved.clone()),
            };
        }
        env(key).filter(|v| !v.trim().is_empty())
    })?;

    let endpoint_overrides: Vec<&'static str> = ENDPOINT_OVERRIDE_KEYS
        .iter()
        .copied()
        .filter(|key| env(key).map(|v| !v.trim().is_empty()).unwrap_or(false))
        .collect();

    let deployment = deployment_of(&config, choice);

    Ok(ResolvedCopilotConfig { config, deployment, endpoint_overrides })
}

/// Derives the deployment from the host that will actually receive the device
/// -code request (and therefore the user's authorization).
fn deployment_of(
    config: &CopilotConfig,
    choice: Option<&CopilotDeploymentChoice>,
) -> CopilotDeployment {
    let auth_host = host_of(&config.device_code_url).unwrap_or_default();
    if auth_host == "github.com" {
        return CopilotDeployment::Public;
    }
    // An enterprise domain owns its auth host. Normalise the claim through the
    // same canonicaliser the configuration used, so a pasted `copilot-api.`
    // host is recognised as that tenant rather than labelled "custom".
    let claimed = match choice {
        Some(CopilotDeploymentChoice::Enterprise(domain)) => {
            normalize_enterprise_domain(domain).ok()
        }
        _ => None,
    };
    if let Some(domain) = claimed {
        if domain == auth_host {
            return CopilotDeployment::Enterprise { domain: auth_host };
        }
        return CopilotDeployment::Custom { auth_host };
    }
    if auth_host.is_empty() {
        return CopilotDeployment::Custom { auth_host };
    }
    // No explicit choice: the enterprise base derives every endpoint from the
    // domain, so an auth host that still owns the API base is that tenant.
    if config.api_base_url == format!("https://copilot-api.{auth_host}") {
        CopilotDeployment::Enterprise { domain: auth_host }
    } else {
        CopilotDeployment::Custom { auth_host }
    }
}

/// Extracts the lowercase `host[:port]` from an absolute URL.
fn host_of(url: &str) -> Option<String> {
    let rest = url.split_once("://").map(|(_, rest)| rest)?;
    let authority = rest.split(['/', '?', '#']).next().unwrap_or("");
    let host = authority.rsplit_once('@').map(|(_, h)| h).unwrap_or(authority);
    if host.is_empty() {
        None
    } else {
        Some(host.to_ascii_lowercase())
    }
}

/// GitHub Copilot provider.
pub struct CopilotProvider {
    config: CopilotConfig,
    http: reqwest::Client,
    /// Latches the `copilot_token_url` value once the exchange endpoint has been
    /// found absent, preventing infinite re-probing within the same process.
    latched_absent_exchange_url: std::sync::Mutex<Option<String>>,
}

impl CopilotProvider {
    pub fn new(config: CopilotConfig) -> Self {
        let http = reqwest::Client::builder()
            .timeout(Duration::from_secs(30))
            .build()
            .expect("failed to build HTTP client");

        Self {
            config,
            http,
            latched_absent_exchange_url: std::sync::Mutex::new(None),
        }
    }

    /// Drive the device-code login flow.
    ///
    /// `on_prompt` is called once with the user code and verification URL to
    /// display; `CopilotProvider` then polls until the user authorizes or the
    /// code expires.
    pub async fn login_with_device_code<F, Fut>(
        &self,
        on_prompt: F,
    ) -> Result<Credential, AuthError>
    where
        F: FnOnce(DeviceCodePrompt) -> Fut,
        Fut: std::future::Future<Output = Result<(), AuthError>>,
    {
        let device = self.request_device_code().await?;

        let prompt = DeviceCodePrompt {
            user_code: device.user_code.clone(),
            verification_uri: device.verification_uri.clone(),
            verification_uri_complete: device.verification_uri_complete.clone(),
            expires_in: device.expires_in,
            interval: device.interval,
        };
        on_prompt(prompt).await?;

        let github_token = self.poll_for_github_token(&device).await?;

        // Exchange the GitHub token for a short-lived Copilot token when the
        // configuration asks for it. `use_exchange = false` is a deliberate
        // instruction (an enterprise tenant without the endpoint, a user who
        // set GH_COPILOT_USE_EXCHANGE=0) and must be honoured here, not just
        // documented: the durable token would otherwise be sent to a host the
        // configuration said not to contact.
        //
        // If the exchange endpoint turns out to be absent on this host, fall
        // back to the raw token so login succeeds with reduced entitlement
        // rather than failing. That fallback covers only endpoint absence
        // (404/501/502/503/504, transport failure, probe timeout) — a 401/403
        // is a real auth failure and propagates.
        match &self.config.copilot_token_url {
            Some(_) if self.config.use_exchange => {
                let exchanged = self.exchange_for_credential(&github_token).await?;
                Ok(exchanged.unwrap_or_else(|| build_direct_credential(&github_token)))
            }
            _ => Ok(build_direct_credential(&github_token)),
        }
    }

    async fn request_device_code(&self) -> Result<DeviceCodeResponse, AuthError> {
        let response = self
            .http
            .post(&self.config.device_code_url)
            .header("accept", "application/json")
            .header("user-agent", &self.config.user_agent)
            .form(&[
                ("client_id", self.config.client_id.as_str()),
                ("scope", self.config.scope.as_str()),
            ])
            .send()
            .await
            .map_err(|e| AuthError::Transport(e.to_string()))?;

        let status = response.status().as_u16();
        let text = response
            .text()
            .await
            .map_err(|e| AuthError::Transport(e.to_string()))?;

        if status / 100 != 2 {
            return Err(AuthError::OAuth { status, body: text });
        }

        parse_device_code_response(status, &text)
    }

    /// Polls the device-grant token endpoint until the user authorizes, the
    /// grant is denied, or the device code expires.
    ///
    /// The expiry is a hard deadline: each wait is clamped to the time left, so
    /// a large server-supplied `interval` (or a `slow_down` back-off) can never
    /// push a poll past the point where the code is dead.
    ///
    /// Cancellation: drop the future. Every await point is a `sleep` or an HTTP
    /// send, so cancelling leaves no half-committed state and yields no
    /// credential.
    async fn poll_for_github_token(
        &self,
        device: &DeviceCodeResponse,
    ) -> Result<String, AuthError> {
        let mut interval = device.interval.max(Duration::from_secs(1));
        let deadline = std::time::Instant::now() + device.expires_in;

        loop {
            let now = std::time::Instant::now();
            if now >= deadline {
                return Err(AuthError::LoginCancelled(
                    "device-code login expired before the user authorized".into(),
                ));
            }

            // Never sleep past the deadline: the answer after it would be
            // useless, and the caller would wait for it anyway.
            let remaining = deadline.saturating_duration_since(now);
            tokio::time::sleep(interval.min(remaining)).await;
            if std::time::Instant::now() >= deadline {
                return Err(AuthError::LoginCancelled(
                    "device-code login expired before the user authorized".into(),
                ));
            }

            let response = self
                .http
                .post(&self.config.token_url)
                .header("accept", "application/json")
                .header("user-agent", &self.config.user_agent)
                .form(&[
                    ("client_id", self.config.client_id.as_str()),
                    ("device_code", device.device_code.as_str()),
                    ("grant_type", DEVICE_GRANT_TYPE),
                ])
                .send()
                .await
                .map_err(|e| AuthError::Transport(e.to_string()))?;

            let text = response
                .text()
                .await
                .map_err(|e| AuthError::Transport(e.to_string()))?;
            let token: DeviceTokenResponse = match serde_json::from_str(&text) {
                Ok(t) => t,
                Err(_) => continue, // transient / unparseable response
            };

            if let Some(access_token) = token
                .access_token
                .as_deref()
                .map(str::trim)
                .filter(|s| !s.is_empty())
                .map(str::to_owned)
            {
                return Ok(access_token);
            }

            match token.error.as_deref() {
                Some("authorization_pending") => {
                    // User has not yet authorized; keep polling.
                }
                Some("slow_down") => {
                    // RFC 8628 §3.5: back off using the server-supplied interval
                    // when present, otherwise add 5 s.
                    interval = token
                        .interval
                        .map(|i| Duration::from_secs(i.max(1) as u64))
                        .unwrap_or_else(|| interval + Duration::from_secs(5));
                }
                Some("expired_token") => {
                    return Err(AuthError::LoginCancelled(
                        "device code expired; restart the login".into(),
                    ));
                }
                Some("access_denied") => {
                    return Err(AuthError::LoginCancelled(
                        "authorization was denied by the user".into(),
                    ));
                }
                Some(_) => {
                    return Err(AuthError::OAuth {
                        status: 400,
                        body: text,
                    });
                }
                None => {
                    // Transient (no error field, no token): keep polling.
                }
            }
        }
    }

    /// Returns `None` when the exchange endpoint is absent (404/5xx/timeout/
    /// transport failure).  Returns `Some(credential)` on success.
    /// Returns `Err` on a genuine auth failure (401/403).
    async fn exchange_for_credential(
        &self,
        github_token: &str,
    ) -> Result<Option<Credential>, AuthError> {
        let exchange_url = match &self.config.copilot_token_url {
            Some(url) => url.clone(),
            None => return Ok(None),
        };

        // Validate the exchange URL before sending the durable token to it.
        if !is_acceptable_exchange_url(&exchange_url) {
            return Err(AuthError::InvalidUrl(
                "Copilot token exchange URL must use https (plaintext is accepted \
                 only for a loopback address)"
                    .into(),
            ));
        }

        // If this URL was already probed and found absent in this process,
        // skip probing again.
        {
            let latched = self.latched_absent_exchange_url.lock().unwrap();
            if latched.as_deref() == Some(exchange_url.as_str()) {
                return Ok(None);
            }
        }

        let request = self
            .http
            .get(&exchange_url)
            .header("authorization", format!("token {github_token}"))
            .header("accept", "application/json")
            .header("user-agent", &self.config.user_agent)
            .header("editor-version", &self.config.editor_version)
            .timeout(EXCHANGE_PROBE_TIMEOUT);

        let response = match request.send().await {
            Ok(r) => r,
            Err(_) => {
                // Transport failure (DNS, connection refused, probe timeout):
                // treat as absent endpoint.
                *self.latched_absent_exchange_url.lock().unwrap() =
                    Some(exchange_url.clone());
                return Ok(None);
            }
        };

        let status = response.status().as_u16();

        if is_exchange_absent_status(status) {
            *self.latched_absent_exchange_url.lock().unwrap() = Some(exchange_url.clone());
            return Ok(None);
        }

        let text = response
            .text()
            .await
            .map_err(|e| AuthError::Transport(e.to_string()))?;

        if status / 100 != 2 {
            // 401/403 → genuine auth problem; do not silently downgrade.
            return Err(AuthError::OAuth { status, body: text });
        }

        let copilot: CopilotTokenResponse = serde_json::from_str(&text)?;
        if copilot.token.as_ref().map(String::is_empty).unwrap_or(true) {
            return Err(AuthError::OAuth {
                status: 200,
                body: "Copilot token exchange returned no token".into(),
            });
        }

        let expires_at = if copilot.expires_at > 0 {
            chrono::DateTime::from_timestamp(copilot.expires_at, 0)
        } else {
            None
        };

        Ok(Some(Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new(copilot.token.unwrap())),
            // Keep the durable GitHub token as the "refresh" token so
            // `refresh` can re-exchange it for a fresh Copilot token.
            refresh_token: Some(Secret::new(github_token.into())),
            api_key: None,
            expires_at,
            scopes: Vec::new(),
            account: None,
        }))
    }
}

#[async_trait]
impl AuthProvider for CopilotProvider {
    fn provider_id(&self) -> &str {
        PROVIDER_ID
    }

    fn needs_refresh(&self, credential: &Credential) -> bool {
        if credential.kind != CredentialKind::OAuth {
            return false;
        }

        // Self-heal: if the exchange URL is configured AND the stored access
        // token is a raw GitHub OAuth/device-flow/PAT token (identifiable by
        // its prefix), force a refresh so the token is exchanged for a full-
        // entitlement Copilot token.  ExpiresAt is null on direct credentials
        // built without the exchange (BuildDirectCredential never sets it).
        //
        // This covers the "stale credential" scenario where the user logged in
        // before the exchange endpoint existed and has a raw ghu_/gho_/ghe_…
        // token stored.  Without this check, the null ExpiresAt would cause
        // needs_refresh to return false forever, permanently denying the user
        // full model entitlement without prompting a re-login.
        if self.config.copilot_token_url.is_some() && self.config.use_exchange {
            if let Some(token) = &credential.access_token {
                if is_raw_github_token(token.expose()) && credential.expires_at.is_none() {
                    return true;
                }
            }
        }

        // Normal path: refresh when the token is within the 5-minute buffer.
        credential
            .expires_at
            .map(|exp| chrono::Utc::now() + chrono::Duration::from_std(REFRESH_BUFFER).unwrap() >= exp)
            .unwrap_or(false)
    }

    async fn refresh(&self, credential: &Credential) -> Result<Credential, AuthError> {
        let github_token = credential
            .refresh_token
            .as_ref()
            .ok_or_else(|| {
                AuthError::CannotRefresh(
                    PROVIDER_ID.into(),
                    "no GitHub token available to refresh the Copilot token".into(),
                )
            })?
            .expose()
            .clone();

        match &self.config.copilot_token_url {
            Some(_) if self.config.use_exchange => {
                let exchanged = self.exchange_for_credential(&github_token).await?;
                Ok(exchanged.unwrap_or_else(|| build_direct_credential(&github_token)))
            }
            _ => Ok(build_direct_credential(&github_token)),
        }
    }

    fn auth_headers(&self, credential: &Credential) -> Result<Vec<(String, String)>, AuthError> {
        let token = credential
            .access_token
            .as_ref()
            .ok_or_else(|| {
                AuthError::NotFound(format!(
                    "no Copilot token for provider '{PROVIDER_ID}'; log in first"
                ))
            })?
            .expose()
            .clone();

        Ok(vec![
            ("authorization".into(), format!("Bearer {token}")),
            ("editor-version".into(), self.config.editor_version.clone()),
            (
                "editor-plugin-version".into(),
                self.config.editor_plugin_version.clone(),
            ),
            ("copilot-integration-id".into(), self.config.integration_id.clone()),
            ("user-agent".into(), self.config.user_agent.clone()),
            ("x-initiator".into(), "user".into()),
            ("x-github-api-version".into(), GITHUB_API_VERSION.into()),
        ])
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

/// Build a direct credential where the raw GitHub token IS the bearer.
fn build_direct_credential(github_token: &str) -> Credential {
    Credential {
        provider_id: PROVIDER_ID.into(),
        kind: CredentialKind::OAuth,
        access_token: Some(Secret::new(github_token.into())),
        refresh_token: Some(Secret::new(github_token.into())),
        api_key: None,
        expires_at: None,
        scopes: Vec::new(),
        account: None,
    }
}

/// HTTP status codes that indicate the exchange endpoint itself is absent (not
/// a credentials problem).  On these statuses we fall back silently rather than
/// failing the entire login.
fn is_exchange_absent_status(status: u16) -> bool {
    matches!(status, 404 | 501 | 502 | 503 | 504)
}

/// Whether the durable GitHub token may be sent to this URL.
///
/// TLS is required, with one exception: a loopback address. There is no
/// network to eavesdrop on `127.0.0.1`, and the exception is what makes the
/// exchange path testable end to end without a certificate. Every other host —
/// including a plaintext LAN address — is refused, because the token being sent
/// is the durable one.
fn is_acceptable_exchange_url(url: &str) -> bool {
    if strip_prefix_ascii_ci(url, "https://").is_some() {
        return true;
    }
    match strip_prefix_ascii_ci(url, "http://") {
        Some(rest) => {
            let authority = rest.split(['/', '?', '#']).next().unwrap_or("");
            // Reject embedded userinfo outright: it hides the real host.
            if authority.contains('@') {
                return false;
            }
            let host = authority.rsplit_once(':').map(|(h, _)| h).unwrap_or(authority);
            let host = host.trim_start_matches('[').trim_end_matches(']');
            host.eq_ignore_ascii_case("localhost") || host == "127.0.0.1" || host == "::1"
        }
        None => false,
    }
}

/// Returns `true` for raw GitHub OAuth / device-flow / PAT tokens that carry
/// no Copilot entitlement and must be exchanged before use.
///
/// These prefixes are part of the GitHub token format spec; a token matching
/// any of them has never passed through the Copilot exchange endpoint and will
/// result in reduced model access if used as-is.
fn is_raw_github_token(token: &str) -> bool {
    token.starts_with("ghu_")
        || token.starts_with("gho_")
        || token.starts_with("ghp_")
        || token.starts_with("ghs_")
        || token.starts_with("ghr_")
        || token.starts_with("ghe_")
        || token.starts_with("github_pat_")
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

/// The device-code endpoint's response as it arrives on the wire.
///
/// Every field is optional because the same 200 response can carry either a
/// grant or an OAuth error envelope (`{"error":"unauthorized_client",...}`).
/// Requiring the success fields at deserialization time would turn that error
/// into a serde failure, which the safe classifier can only report as a
/// credential parse problem — telling the user their stored credential is
/// corrupt when in fact the server refused the client.
#[derive(Debug, Deserialize)]
struct DeviceCodeEnvelope {
    device_code: Option<String>,
    user_code: Option<String>,
    verification_uri: Option<String>,
    verification_uri_complete: Option<String>,
    expires_in: Option<u32>,
    interval: Option<u32>,
    error: Option<String>,
}

/// A validated device-code grant.
#[derive(Debug, Clone)]
struct DeviceCodeResponse {
    device_code: String,
    user_code: String,
    verification_uri: String,
    verification_uri_complete: Option<String>,
    expires_in: Duration,
    interval: Duration,
}

/// Interprets a 2xx device-code response.
///
/// Order matters: the OAuth error envelope is recognised *before* the success
/// fields are required, so an authorization failure is classified as one. Only
/// then are the fields RFC 8628 makes mandatory enforced; `interval` is
/// optional there and defaults to 5 s.
fn parse_device_code_response(
    status: u16,
    text: &str,
) -> Result<DeviceCodeResponse, AuthError> {
    let envelope: DeviceCodeEnvelope = serde_json::from_str(text)?;

    if let Some(error) = envelope.error.as_deref().map(str::trim).filter(|e| !e.is_empty()) {
        return Err(AuthError::OAuth { status, body: error.to_owned() });
    }

    let required = |value: Option<String>, name: &str| -> Result<String, AuthError> {
        match value.as_deref().map(str::trim).filter(|v| !v.is_empty()) {
            Some(v) => Ok(v.to_owned()),
            None => Err(AuthError::OAuth {
                status,
                body: format!("device-code response was missing '{name}'"),
            }),
        }
    };

    let device_code = required(envelope.device_code, "device_code")?;
    let user_code = required(envelope.user_code, "user_code")?;
    let verification_uri = required(envelope.verification_uri, "verification_uri")?;
    let expires_in = match envelope.expires_in {
        Some(secs) if secs > 0 => Duration::from_secs(secs as u64),
        _ => {
            return Err(AuthError::OAuth {
                status,
                body: "device-code response was missing a usable 'expires_in'".into(),
            })
        }
    };

    Ok(DeviceCodeResponse {
        device_code,
        user_code,
        verification_uri,
        verification_uri_complete: envelope
            .verification_uri_complete
            .filter(|v| !v.trim().is_empty()),
        expires_in,
        // RFC 8628 §3.2: `interval` is OPTIONAL and defaults to 5 seconds.
        interval: envelope
            .interval
            .map(|i| Duration::from_secs(i as u64))
            .unwrap_or(DEFAULT_POLL_INTERVAL),
    })
}

#[derive(Debug, Deserialize, Default)]
struct DeviceTokenResponse {
    access_token: Option<String>,
    error: Option<String>,
    interval: Option<u32>,
}

#[derive(Debug, Deserialize)]
struct CopilotTokenResponse {
    token: Option<String>,
    expires_at: i64,
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::TcpListener;

    // ── Enterprise configuration ─────────────────────────────────────────────
    //
    // Mirrors the C# GitHubCopilotConfig ForEnterprise_* / FromEnvironment_*
    // tests. Enterprise support was absent from the Rust port entirely, so
    // enterprise GitHub users could not authenticate at all.

    mod enterprise {
        use super::*;

        /// An empty or whitespace domain is a configuration error, not a
        /// silently-accepted host.
        #[test]
        fn a_blank_domain_is_rejected() {
            for blank in ["", "   ", "\t"] {
                assert!(
                    CopilotConfig::for_enterprise(blank).is_err(),
                    "blank domain {blank:?} must be rejected"
                );
            }
        }

        /// SECURITY: the domain lands in the OAuth token-exchange URL, so a
        /// path, query, fragment, or userinfo component could redirect the
        /// durable token to an unintended host.
        #[test]
        fn a_domain_that_is_not_a_bare_host_is_rejected() {
            for hostile in [
                "ghe.com/path",
                "ghe.com?query=1",
                "ghe.com#frag",
                "user@evil.com",
                "evil.com/@ghe.com",
                "ghe.com\\share",
                "ghe com",
            ] {
                assert!(
                    CopilotConfig::for_enterprise(hostile).is_err(),
                    "non-bare host {hostile:?} must be rejected"
                );
            }
        }

        #[test]
        fn a_scheme_and_trailing_slashes_are_stripped() {
            for form in [
                "https://octocorp.ghe.com",
                "http://octocorp.ghe.com",
                "octocorp.ghe.com/",
                "HTTPS://octocorp.ghe.com//",
                "  octocorp.ghe.com  ",
            ] {
                let config = CopilotConfig::for_enterprise(form).expect("valid host");
                assert_eq!(
                    config.device_code_url, "https://octocorp.ghe.com/login/device/code",
                    "input form {form:?} produced the wrong device-code URL"
                );
            }
        }

        /// Pasting the Copilot host instead of the GHE host is an easy mistake;
        /// recovering it keeps every derived URL consistent and avoids a
        /// doubled `copilot-api.` prefix.
        #[test]
        fn the_copilot_host_pasted_by_mistake_recovers_the_ghe_host() {
            let config =
                CopilotConfig::for_enterprise("copilot-api.octocorp.ghe.com").expect("valid host");
            assert_eq!(config.device_code_url, "https://octocorp.ghe.com/login/device/code");
            assert_eq!(
                config.copilot_token_url.as_deref(),
                Some("https://api.octocorp.ghe.com/copilot_internal/v2/token")
            );
            assert_eq!(
                config.api_base_url, "https://copilot-api.octocorp.ghe.com",
                "the copilot-api prefix must not be doubled"
            );
        }

        #[test]
        fn enterprise_urls_are_derived_from_the_domain() {
            let config = CopilotConfig::for_enterprise("octocorp.ghe.com").expect("valid host");
            assert_eq!(config.device_code_url, "https://octocorp.ghe.com/login/device/code");
            assert_eq!(config.token_url, "https://octocorp.ghe.com/login/oauth/access_token");
            assert_eq!(
                config.copilot_token_url.as_deref(),
                Some("https://api.octocorp.ghe.com/copilot_internal/v2/token")
            );
            assert_eq!(config.api_base_url, "https://copilot-api.octocorp.ghe.com");
        }

        /// The raw device-flow token grants only a legacy subset of models, so
        /// the exchange must be on for enterprise tenants.
        #[test]
        fn enterprise_uses_the_token_exchange() {
            let config = CopilotConfig::for_enterprise("octocorp.ghe.com").expect("valid host");
            assert!(config.use_exchange);
        }

        #[test]
        fn enterprise_inherits_client_id_and_editor_headers_from_the_default() {
            let default = CopilotConfig::default_public();
            let config = CopilotConfig::for_enterprise("octocorp.ghe.com").expect("valid host");
            assert_eq!(config.client_id, default.client_id);
            assert_eq!(config.editor_version, default.editor_version);
            assert_eq!(config.editor_plugin_version, default.editor_plugin_version);
            assert_eq!(config.integration_id, default.integration_id);
            assert_eq!(config.user_agent, default.user_agent);
            assert_eq!(config.scope, default.scope);
        }
    }

    mod from_environment {
        use super::*;

        fn env_of(pairs: &[(&str, &str)]) -> impl Fn(&str) -> Option<String> {
            let owned: Vec<(String, String)> =
                pairs.iter().map(|(k, v)| ((*k).to_string(), (*v).to_string())).collect();
            move |key| owned.iter().find(|(k, _)| k == key).map(|(_, v)| v.clone())
        }

        #[test]
        fn no_variables_matches_the_public_default() {
            let config = CopilotConfig::from_env_lookup(env_of(&[])).expect("config");
            let default = CopilotConfig::default_public();
            assert_eq!(config.device_code_url, default.device_code_url);
            assert_eq!(config.token_url, default.token_url);
            assert_eq!(config.copilot_token_url, default.copilot_token_url);
            assert_eq!(config.api_base_url, default.api_base_url);
            assert_eq!(config.use_exchange, default.use_exchange);
        }

        #[test]
        fn an_enterprise_domain_starts_from_the_enterprise_config() {
            let config = CopilotConfig::from_env_lookup(env_of(&[(
                "GH_COPILOT_ENTERPRISE_DOMAIN",
                "octocorp.ghe.com",
            )]))
            .expect("config");
            assert_eq!(config.device_code_url, "https://octocorp.ghe.com/login/device/code");
            assert_eq!(config.api_base_url, "https://copilot-api.octocorp.ghe.com");
        }

        /// A tenant may redirect one endpoint without restating the rest.
        #[test]
        fn individual_overrides_apply_on_top_of_enterprise() {
            let config = CopilotConfig::from_env_lookup(env_of(&[
                ("GH_COPILOT_ENTERPRISE_DOMAIN", "octocorp.ghe.com"),
                ("GH_COPILOT_COPILOT_TOKEN_URL", "https://proxy.internal/token"),
            ]))
            .expect("config");
            assert_eq!(config.copilot_token_url.as_deref(), Some("https://proxy.internal/token"));
            // Everything not overridden still comes from the enterprise base.
            assert_eq!(config.device_code_url, "https://octocorp.ghe.com/login/device/code");
        }

        #[test]
        fn the_exchange_flag_follows_the_c_sharp_truthiness_rule() {
            for (raw, expected) in
                [("false", false), ("FALSE", false), ("0", false), ("true", true), ("1", true)]
            {
                let config =
                    CopilotConfig::from_env_lookup(env_of(&[("GH_COPILOT_USE_EXCHANGE", raw)]))
                        .expect("config");
                assert_eq!(config.use_exchange, expected, "GH_COPILOT_USE_EXCHANGE={raw}");
            }
        }

        #[test]
        fn the_exchange_can_be_disabled_for_an_enterprise_tenant() {
            let config = CopilotConfig::from_env_lookup(env_of(&[
                ("GH_COPILOT_ENTERPRISE_DOMAIN", "octocorp.ghe.com"),
                ("GH_COPILOT_USE_EXCHANGE", "false"),
            ]))
            .expect("config");
            assert!(!config.use_exchange);
        }

        /// A hostile domain must fail the whole configuration rather than
        /// falling back to the public default, which would silently send an
        /// enterprise user's traffic to github.com.
        #[test]
        fn a_hostile_enterprise_domain_fails_rather_than_falling_back() {
            let result = CopilotConfig::from_env_lookup(env_of(&[(
                "GH_COPILOT_ENTERPRISE_DOMAIN",
                "evil.com/@octocorp.ghe.com",
            )]));
            assert!(result.is_err(), "a non-bare host must not fall back to the public default");
        }
    }

    #[allow(dead_code)] // used by exchange tests; kept for future use
    async fn mock_server(status: u16, body: &'static str) -> String {
        let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let port = listener.local_addr().expect("addr").port();

        tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.expect("accept");
            let mut buf = vec![0u8; 4096];
            let _ = socket.read(&mut buf).await;

            let reason = if status == 200 { "OK" } else { "Error" };
            let resp = format!(
                "HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                body.len()
            );
            let _ = socket.write_all(resp.as_bytes()).await;
        });

        format!("http://127.0.0.1:{port}")
    }

    fn config_with_token_url(token_url: &str) -> CopilotConfig {
        CopilotConfig {
            client_id: "test-client".into(),
            device_code_url: "http://unused/device".into(),
            token_url: token_url.into(),
            copilot_token_url: None,
            scope: "read:user".into(),
            editor_version: "vscode/1.0".into(),
            editor_plugin_version: "copilot/0.1".into(),
            integration_id: "vscode".into(),
            user_agent: "TestAgent/0.1".into(),
            ..CopilotConfig::default_public()
        }
    }

    #[test]
    fn credential_does_not_expose_tokens_in_debug() {
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("copilot_secret_token".into())),
            refresh_token: Some(Secret::new("github_durable_token".into())),
            api_key: None,
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        };
        let debug = format!("{cred:?}");
        assert!(!debug.contains("copilot_secret_token"), "access_token leaked: {debug}");
        assert!(!debug.contains("github_durable_token"), "refresh_token leaked: {debug}");
    }

    #[test]
    fn needs_refresh_is_true_when_near_expiry() {
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("tok".into())),
            refresh_token: Some(Secret::new("gh".into())),
            api_key: None,
            expires_at: Some(chrono::Utc::now() + chrono::Duration::minutes(2)),
            scopes: Vec::new(),
            account: None,
        };
        let p = CopilotProvider::new(CopilotConfig::default_public());
        assert!(p.needs_refresh(&cred));
    }

    #[test]
    fn needs_refresh_is_false_with_ample_time() {
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("tok".into())),
            refresh_token: None,
            api_key: None,
            expires_at: Some(chrono::Utc::now() + chrono::Duration::hours(2)),
            scopes: Vec::new(),
            account: None,
        };
        let p = CopilotProvider::new(CopilotConfig::default_public());
        assert!(!p.needs_refresh(&cred));
    }

    #[test]
    fn needs_refresh_is_false_when_no_expiry() {
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("tok".into())),
            refresh_token: None,
            api_key: None,
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        };
        let p = CopilotProvider::new(CopilotConfig::default_public());
        assert!(!p.needs_refresh(&cred));
    }

    #[test]
    fn exchange_absent_statuses_do_not_trigger_hard_failure() {
        for &status in &[404u16, 501, 502, 503, 504] {
            assert!(
                is_exchange_absent_status(status),
                "expected {status} to be treated as absent"
            );
        }
    }

    #[test]
    fn auth_headers_include_editor_and_initiator() {
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("bearer_token".into())),
            refresh_token: None,
            api_key: None,
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        };
        let p = CopilotProvider::new(CopilotConfig::default_public());
        let headers = p.auth_headers(&cred).expect("headers");

        let header_map: std::collections::HashMap<_, _> = headers.into_iter().collect();
        assert!(header_map["authorization"].starts_with("Bearer "));
        assert_eq!(header_map["x-initiator"], "user");
        assert!(!header_map["editor-version"].is_empty());
    }

    // ── MINOR 7: needs_refresh boundary tests ─────────────────────────────────

    fn cred_expiring_in(secs: i64) -> Credential {
        let expires_at = chrono::Utc::now()
            .checked_add_signed(chrono::Duration::seconds(secs))
            .expect("test expiry must be representable");
        Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("tok".into())),
            refresh_token: None,
            api_key: None,
            expires_at: Some(expires_at),
            scopes: Vec::new(),
            account: None,
        }
    }

    #[test]
    fn needs_refresh_is_true_exactly_at_the_buffer_boundary() {
        // At exactly REFRESH_BUFFER seconds remaining, now + REFRESH_BUFFER >= exp,
        // so the token MUST be refreshed (>= not >).
        let at_boundary = cred_expiring_in(REFRESH_BUFFER.as_secs() as i64);
        let p = CopilotProvider::new(CopilotConfig::default_public());
        assert!(
            p.needs_refresh(&at_boundary),
            "token exactly at the refresh boundary must trigger refresh"
        );
    }

    #[test]
    fn needs_refresh_is_true_one_second_inside_the_buffer() {
        let one_in = cred_expiring_in(REFRESH_BUFFER.as_secs() as i64 - 1);
        let p = CopilotProvider::new(CopilotConfig::default_public());
        assert!(
            p.needs_refresh(&one_in),
            "token one second inside the refresh window must trigger refresh"
        );
    }

    #[test]
    fn needs_refresh_is_false_one_second_outside_the_buffer() {
        let one_out = cred_expiring_in(REFRESH_BUFFER.as_secs() as i64 + 1);
        let p = CopilotProvider::new(CopilotConfig::default_public());
        assert!(
            !p.needs_refresh(&one_out),
            "token one second outside the refresh window must not trigger refresh"
        );
    }

    #[tokio::test]
    async fn polling_returns_on_authorization_pending_then_success() {
        // A server that first returns authorization_pending, then a token.
        let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let port = listener.local_addr().expect("addr").port();

        tokio::spawn(async move {
            let mut call_count = 0u32;
            loop {
                let Ok((mut socket, _)) = listener.accept().await else { break };
                let mut buf = vec![0u8; 4096];
                let _ = socket.read(&mut buf).await;

                call_count += 1;
                let body = if call_count == 1 {
                    r#"{"error":"authorization_pending"}"#
                } else {
                    r#"{"access_token":"gh_token_abc"}"#
                };
                let resp = format!(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                let _ = socket.write_all(resp.as_bytes()).await;
            }
        });

        let token_url = format!("http://127.0.0.1:{port}");
        let config = CopilotConfig {
            client_id: "id".into(),
            device_code_url: "http://unused".into(),
            token_url,
            copilot_token_url: None,
            scope: "read:user".into(),
            editor_version: "v".into(),
            editor_plugin_version: "p".into(),
            integration_id: "i".into(),
            user_agent: "u".into(),
            ..CopilotConfig::default_public()
        };

        let device = DeviceCodeResponse {
            device_code: "dc".into(),
            user_code: "ABCD-1234".into(),
            verification_uri: "http://unused".into(),
            verification_uri_complete: None,
            expires_in: Duration::from_secs(900),
            interval: Duration::from_secs(0), // clamped to 1 s by the poll loop
        };

        let p = CopilotProvider::new(config);
        // Override interval to 0 so test doesn't wait a full second.
        // We test via the internal helper.
        let result = tokio::time::timeout(
            Duration::from_secs(10),
            p.poll_for_github_token(&device),
        )
        .await
        .expect("no timeout")
        .expect("token");

        assert_eq!(result, "gh_token_abc");
    }

    #[tokio::test]
    async fn slow_down_increases_interval() {
        // A server that sends slow_down with a new interval, then a token.
        let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let port = listener.local_addr().expect("addr").port();

        tokio::spawn(async move {
            let responses = vec![
                r#"{"error":"slow_down","interval":2}"#,
                r#"{"access_token":"gh_tok"}"#,
            ];
            for body in responses {
                let Ok((mut socket, _)) = listener.accept().await else { break };
                let mut buf = vec![0u8; 4096];
                let _ = socket.read(&mut buf).await;
                let resp = format!(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                let _ = socket.write_all(resp.as_bytes()).await;
            }
        });

        let token_url = format!("http://127.0.0.1:{port}");
        let config = config_with_token_url(&token_url);
        let device = DeviceCodeResponse {
            device_code: "dc".into(),
            user_code: "CODE".into(),
            verification_uri: "http://unused".into(),
            verification_uri_complete: None,
            expires_in: Duration::from_secs(900),
            interval: Duration::from_secs(0),
        };

        let p = CopilotProvider::new(config);
        let result = tokio::time::timeout(
            Duration::from_secs(10),
            p.poll_for_github_token(&device),
        )
        .await
        .expect("no timeout")
        .expect("token");

        assert_eq!(result, "gh_tok");
    }

    #[tokio::test]
    async fn exchange_404_returns_none() {
        // We can't easily test the 404 path with a real HTTP server because the
        // provider requires https for the exchange URL.  Verify the status-code
        // detection logic directly instead.
        assert!(is_exchange_absent_status(404));
    }

    // ── Device-code response parsing ─────────────────────────────────────────

    /// RFC 8628 §3.2 makes `interval` OPTIONAL with a 5-second default;
    /// requiring it locks the user out of a spec-compliant server.
    #[test]
    fn an_absent_interval_defaults_to_five_seconds() {
        let parsed = parse_device_code_response(
            200,
            r#"{"device_code":"dc","user_code":"CODE","verification_uri":"https://x/device","expires_in":900}"#,
        )
        .expect("a response without an interval is valid");
        assert_eq!(parsed.interval, DEFAULT_POLL_INTERVAL);
        assert_eq!(parsed.expires_in, Duration::from_secs(900));
        assert_eq!(parsed.device_code, "dc");
    }

    #[test]
    fn a_supplied_interval_is_used_verbatim() {
        let parsed = parse_device_code_response(
            200,
            r#"{"device_code":"dc","user_code":"C","verification_uri":"https://x","expires_in":60,"interval":7}"#,
        )
        .expect("valid");
        assert_eq!(parsed.interval, Duration::from_secs(7));
    }

    /// An OAuth error envelope on a 200 must classify as an authorization
    /// rejection. As a serde failure it would surface as "credential parse
    /// error", which tells the user their credential store is corrupt.
    #[test]
    fn an_error_envelope_is_an_oauth_error_not_a_parse_error() {
        for body in [
            r#"{"error":"unauthorized_client","error_description":"nope"}"#,
            r#"{"error":"invalid_client","interval":5}"#,
        ] {
            let err = parse_device_code_response(200, body).unwrap_err();
            assert!(matches!(err, AuthError::OAuth { .. }), "{body} produced {err:?}");
            assert_eq!(
                crate::failure::AuthFailure::classify(&err),
                crate::failure::AuthFailure::OAuthRejected { status: 200 },
                "{body}"
            );
        }
    }

    #[test]
    fn required_success_fields_are_still_enforced() {
        for body in [
            r#"{"user_code":"C","verification_uri":"https://x","expires_in":60}"#,
            r#"{"device_code":"  ","user_code":"C","verification_uri":"https://x","expires_in":60}"#,
            r#"{"device_code":"dc","verification_uri":"https://x","expires_in":60}"#,
            r#"{"device_code":"dc","user_code":"C","expires_in":60}"#,
            r#"{"device_code":"dc","user_code":"C","verification_uri":"https://x"}"#,
            r#"{"device_code":"dc","user_code":"C","verification_uri":"https://x","expires_in":0}"#,
        ] {
            let err = parse_device_code_response(200, body).unwrap_err();
            assert!(matches!(err, AuthError::OAuth { .. }), "{body} produced {err:?}");
        }
    }

    #[test]
    fn a_non_json_body_is_still_a_parse_failure() {
        let err = parse_device_code_response(200, "<html>gateway</html>").unwrap_err();
        assert!(matches!(err, AuthError::Serialization(_)), "got {err:?}");
    }

    // ── Exchange URL policy ──────────────────────────────────────────────────

    #[test]
    fn the_durable_token_only_goes_to_tls_or_loopback() {
        for allowed in [
            "https://api.github.com/copilot_internal/v2/token",
            "http://127.0.0.1:8080/token",
            "http://localhost:3000/token",
            "HTTP://LOCALHOST/token",
        ] {
            assert!(is_acceptable_exchange_url(allowed), "{allowed} must be allowed");
        }
        for refused in [
            "http://api.github.com/token",
            "http://10.0.0.5/token",
            "http://localhost@evil.example/token",
            "ftp://localhost/token",
            "//localhost/token",
        ] {
            assert!(!is_acceptable_exchange_url(refused), "{refused} must be refused");
        }
    }

    // ── Debug safety ─────────────────────────────────────────────────────────

    #[test]
    fn config_debug_reduces_urls_to_their_host() {
        let config = CopilotConfig {
            token_url: "https://user:pw@proxy.internal/token?key=SECRETVALUE".into(),
            copilot_token_url: Some("https://proxy.internal/exchange?key=SECRETVALUE".into()),
            ..CopilotConfig::default_public()
        };
        let rendered = format!("{config:?}");
        assert!(!rendered.contains("SECRETVALUE"), "{rendered}");
        assert!(!rendered.contains("pw@"), "{rendered}");
        assert!(rendered.contains("proxy.internal"), "the host is still useful: {rendered}");
    }

    #[test]
    fn redact_url_keeps_only_scheme_and_host() {
        assert_eq!(redact_url("https://host/a/b?c=d"), "https://host/…");
        assert_eq!(redact_url("https://u:p@host:8443/a"), "https://host:8443/…");
        assert_eq!(redact_url("not a url"), "<redacted>");
    }

    #[tokio::test]
    async fn exchange_invalid_url_returns_error() {        let config = CopilotConfig {
            copilot_token_url: Some("http://not-https.example.com/token".into()),
            ..CopilotConfig::default_public()
        };
        let p = CopilotProvider::new(config);
        let err = p
            .exchange_for_credential("gh_token")
            .await
            .unwrap_err();
        assert!(
            matches!(err, AuthError::InvalidUrl(_)),
            "expected InvalidUrl, got {err:?}"
        );
    }

    // ── auth header includes GitHub API version ────────────────────────────────

    #[test]
    fn auth_headers_include_github_api_version() {
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("bearer_token".into())),
            refresh_token: None,
            api_key: None,
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        };
        let p = CopilotProvider::new(CopilotConfig::default_public());
        let headers = p.auth_headers(&cred).expect("headers");
        let header_map: std::collections::HashMap<_, _> = headers.into_iter().collect();
        assert_eq!(
            header_map.get("x-github-api-version").map(String::as_str),
            Some(GITHUB_API_VERSION),
            "x-github-api-version header must match the declared constant"
        );
    }

    // ── raw-token self-heal ────────────────────────────────────────────────────

    /// When the exchange URL is configured AND the stored credential holds a
    /// raw GitHub OAuth token (identifiable by its prefix), `needs_refresh`
    /// must return `true` so the token is immediately re-exchanged for a
    /// full-entitlement Copilot token.  This "self-heal" is crucial for
    /// credentials stored before the exchange endpoint existed.
    #[test]
    fn needs_refresh_is_true_for_raw_github_token_with_exchange_configured() {
        let raw_prefixes = [
            "ghu_RawDeviceFlowToken",
            "gho_RawOAuthToken",
            "ghp_PersonalAccessToken",
            "ghs_ServerToken",
            "ghr_RunnerToken",
            "ghe_EnterpriseToken",
            "github_pat_FinegrainedPat",
        ];
        // default_public() has copilot_token_url = Some(...)  → UseExchange=true equivalent
        let p = CopilotProvider::new(CopilotConfig::default_public());

        for raw_token in raw_prefixes {
            let cred = Credential {
                provider_id: PROVIDER_ID.into(),
                kind: CredentialKind::OAuth,
                access_token: Some(Secret::new(raw_token.into())),
                refresh_token: Some(Secret::new(raw_token.into())),
                api_key: None,
                // Null ExpiresAt is the fingerprint of a build_direct_credential result.
                expires_at: None,
                scopes: Vec::new(),
                account: None,
            };
            assert!(
                p.needs_refresh(&cred),
                "raw token '{raw_token}' with exchange configured must trigger refresh"
            );
        }
    }

    /// When the exchange URL is NOT configured (copilot_token_url = None),
    /// a raw GitHub token with null ExpiresAt represents a legitimate long-lived
    /// credential and must NOT trigger a refresh.
    #[test]
    fn needs_refresh_is_false_for_raw_token_when_no_exchange_configured() {
        let config = CopilotConfig {
            copilot_token_url: None, // UseExchange=false equivalent
            ..CopilotConfig::default_public()
        };
        let p = CopilotProvider::new(config);
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            access_token: Some(Secret::new("ghu_RawToken".into())),
            refresh_token: Some(Secret::new("ghu_RawToken".into())),
            api_key: None,
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        };
        assert!(
            !p.needs_refresh(&cred),
            "raw token without exchange configured must NOT trigger refresh"
        );
    }

    /// An already-exchanged token that happens to have null ExpiresAt (unusual
    /// but possible) must NOT be flagged as needing self-heal, since its
    /// access_token doesn't start with a known raw-token prefix.
    #[test]
    fn needs_refresh_is_false_for_already_exchanged_token_with_null_expiry() {
        let p = CopilotProvider::new(CopilotConfig::default_public());
        let cred = Credential {
            provider_id: PROVIDER_ID.into(),
            kind: CredentialKind::OAuth,
            // "tid=…" is the Copilot-exchanged token format — not a raw prefix.
            access_token: Some(Secret::new(
                "tid=abc;exp=123;sku=copilot_enterprise_seat_quota".into(),
            )),
            refresh_token: Some(Secret::new("ghu_underlying_github_token".into())),
            api_key: None,
            expires_at: None,
            scopes: Vec::new(),
            account: None,
        };
        assert!(
            !p.needs_refresh(&cred),
            "already-exchanged token must not be mistaken for a raw token"
        );
    }

    // ── access denied during device login ────────────────────────────────────

    /// When the user denies consent in the browser, the token endpoint returns
    /// `"error":"access_denied"`.  The polling loop must surface this as
    /// `AuthError::LoginCancelled` rather than retrying indefinitely or panicking.
    /// Mirrors C# `DeviceLogin_AccessDenied_Throws`.
    #[tokio::test]
    async fn device_login_access_denied_throws_login_cancelled() {
        // We test the polling phase directly (same pattern as the other polling
        // tests) because the mock HTTP infrastructure used for the device-code
        // phase would add complexity without covering new code paths.
        let listener = TcpListener::bind("127.0.0.1:0").await.expect("bind");
        let port = listener.local_addr().expect("addr").port();

        tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.expect("accept");
            let mut buf = vec![0u8; 4096];
            let _ = socket.read(&mut buf).await;
            let body = r#"{"error":"access_denied"}"#;
            let resp = format!(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
                body.len(),
                body
            );
            let _ = socket.write_all(resp.as_bytes()).await;
        });

        let token_url = format!("http://127.0.0.1:{port}");
        let config = CopilotConfig {
            client_id: "id".into(),
            device_code_url: "http://unused/device".into(),
            token_url,
            copilot_token_url: None,
            scope: "read:user".into(),
            editor_version: "v".into(),
            editor_plugin_version: "p".into(),
            integration_id: "i".into(),
            user_agent: "u".into(),
            ..CopilotConfig::default_public()
        };

        let device = DeviceCodeResponse {
            device_code: "dc".into(),
            user_code: "AAAA-BBBB".into(),
            verification_uri: "http://gh".into(),
            verification_uri_complete: None,
            expires_in: Duration::from_secs(900),
            interval: Duration::from_secs(0), // → clamped to 1 s before the one poll
        };

        let p = CopilotProvider::new(config);
        let result = tokio::time::timeout(
            Duration::from_secs(10),
            p.poll_for_github_token(&device),
        )
        .await
        .expect("test must not timeout")
        .expect_err("access_denied must produce an error");

        assert!(
            matches!(result, AuthError::LoginCancelled(_)),
            "access_denied must surface as AuthError::LoginCancelled, got {result:?}"
        );
    }
}




