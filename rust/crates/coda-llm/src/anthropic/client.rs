//! HTTP transport for the Anthropic Messages API.
//!
//! Streaming is driven on a spawned task that owns the response body and
//! forwards decoded events over a channel. Dropping the receiver drops the
//! sender, which aborts the task and cancels the request — that is how an
//! interrupt reaches the network.

use std::time::Duration;

use tokio::sync::mpsc;

use crate::anthropic::{protocol::AnthropicDecoder, request};
use crate::client::{LlmClient, ResponseStream};
use crate::error::LlmError;
use crate::message::{ChatRequest, ModelInfo};
use crate::retry::RetryPolicy;

/// Anthropic API version header value.
const API_VERSION: &str = "2023-06-01";

/// Beta features requested on every call.
const BETA_FEATURES: &str = "prompt-caching-2024-07-31,extended-cache-ttl-2025-04-11";

/// How long to wait for the response headers.
const CONNECT_TIMEOUT: Duration = Duration::from_secs(30);

/// Buffer depth for decoded events.
const CHANNEL_DEPTH: usize = 256;

/// Overall bound on the non-streaming model listing.
const MODELS_TIMEOUT: Duration = Duration::from_secs(30);

/// How the client authenticates.
#[derive(Debug, Clone)]
pub enum Auth {
    /// A console API key, sent as `x-api-key`.
    ApiKey(String),
    /// An OAuth bearer token, used by Claude.ai subscriptions.
    Bearer(String),
}

/// Configuration for [`AnthropicClient`].
#[derive(Debug, Clone)]
pub struct AnthropicConfig {
    pub base_url: String,
    pub auth: Auth,
    pub retry: RetryPolicy,
    /// Extra headers, used by subscription auth which requires a beta flag.
    pub extra_headers: Vec<(String, String)>,
    /// Optional dynamic credential source; when present its auth headers
    /// override the static `auth` field on every request.
    pub credential_source: Option<std::sync::Arc<dyn crate::credential_source::CredentialSource>>,
}

impl AnthropicConfig {
    pub fn api_key(key: impl Into<String>) -> Self {
        Self {
            base_url: "https://api.anthropic.com".into(),
            auth: Auth::ApiKey(key.into()),
            retry: RetryPolicy::default(),
            extra_headers: Vec::new(),
            credential_source: None,
        }
    }

    pub fn with_base_url(mut self, url: impl Into<String>) -> Self {
        self.base_url = url.into();
        self
    }

    pub fn with_retry(mut self, retry: RetryPolicy) -> Self {
        self.retry = retry;
        self
    }

    /// Attach a dynamic credential source.  Its `auth_headers()` output takes
    /// priority over the static `auth` field so tokens are always fresh.
    pub fn with_credential_source(
        mut self,
        src: std::sync::Arc<dyn crate::credential_source::CredentialSource>,
    ) -> Self {
        self.credential_source = Some(src);
        self
    }
}

/// Anthropic Messages API client.
pub struct AnthropicClient {
    http: reqwest::Client,
    config: AnthropicConfig,
}

impl AnthropicClient {
    pub fn new(config: AnthropicConfig) -> Result<Self, LlmError> {
        let http = reqwest::Client::builder()
            .connect_timeout(CONNECT_TIMEOUT)
            // No overall timeout: a long generation is not a failure.
            .build()
            .map_err(|error| LlmError::Transport(error.to_string()))?;

        Ok(Self { http, config })
    }

    fn endpoint(&self) -> String {
        format!("{}/v1/messages", self.config.base_url.trim_end_matches('/'))
    }

    /// Build a POST request applying auth headers.
    ///
    /// `dynamic_auth` (fetched from the credential source before the retry loop)
    /// overrides the static `auth` field so refreshed tokens are used immediately.
    fn request_builder_with_auth(
        &self,
        url: &str,
        dynamic_auth: Option<&[(String, String)]>,
    ) -> reqwest::RequestBuilder {
        let mut builder = self
            .http
            .post(url)
            .header("anthropic-version", API_VERSION)
            .header("anthropic-beta", BETA_FEATURES)
            .header("content-type", "application/json")
            .header("accept", "text/event-stream");

        if let Some(headers) = dynamic_auth {
            // Dynamic credential source wins over static auth.
            for (name, value) in headers {
                builder = builder.header(name.as_str(), value.as_str());
            }
        } else {
            builder = match &self.config.auth {
                Auth::ApiKey(key) => builder.header("x-api-key", key.as_str()),
                Auth::Bearer(token) => builder.header("authorization", format!("Bearer {}", token)),
            };
        }

        for (name, value) in &self.config.extra_headers {
            builder = builder.header(name.as_str(), value.as_str());
        }
        builder
    }

    /// Build a GET request applying auth headers (used for model listing).
    fn get_builder_with_auth(
        &self,
        url: &str,
        dynamic_auth: Option<&[(String, String)]>,
    ) -> reqwest::RequestBuilder {
        let mut builder = self
            .http
            .get(url)
            .header("anthropic-version", API_VERSION);

        if let Some(headers) = dynamic_auth {
            for (name, value) in headers {
                builder = builder.header(name.as_str(), value.as_str());
            }
        } else {
            builder = match &self.config.auth {
                Auth::ApiKey(key) => builder.header("x-api-key", key.as_str()),
                Auth::Bearer(token) => builder.header("authorization", format!("Bearer {}", token)),
            };
        }
        builder
    }
    /// Sends the request, retrying transient failures before any bytes are
    /// streamed.
    ///
    /// Retrying only happens here: once events have been emitted the caller has
    /// already seen partial output, and replaying would duplicate it.
    ///
    /// The shared `crate::retry::send_with_retry` handles the retry loop so the
    /// Anthropic and Copilot clients do not duplicate that logic.
    async fn send_with_retry(&self, body: &serde_json::Value) -> Result<reqwest::Response, LlmError> {
        let url = self.endpoint();

        // Fetch dynamic auth headers once per request (before the retry loop) so
        // a refreshed token is used immediately without recreating the client.
        let dynamic_auth: Option<Vec<(String, String)>> = if let Some(src) = &self.config.credential_source {
            src.auth_headers().await
        } else {
            None
        };

        crate::retry::send_with_retry(&self.config.retry, "anthropic", || {
            self.request_builder_with_auth(&url, dynamic_auth.as_deref()).json(body)
        })
        .await
    }
}

#[async_trait::async_trait]
impl LlmClient for AnthropicClient {
    fn provider_id(&self) -> &str {
        "anthropic"
    }

    async fn stream(&self, request: ChatRequest) -> Result<ResponseStream, LlmError> {
        let body = request::build(&request);
        let response = self.send_with_retry(&body).await?;

        let (tx, rx) = mpsc::channel(CHANNEL_DEPTH);
        tokio::spawn(crate::pump::pump(response, AnthropicDecoder::new(), tx));
        Ok(ResponseStream::new(rx))
    }

    async fn list_models(&self) -> Result<Vec<ModelInfo>, LlmError> {
        // The Messages API exposes a model list, but it needs no streaming and
        // the catalog rarely changes; a plain GET is enough.
        let url = format!("{}/v1/models", self.config.base_url.trim_end_matches('/'));

        // Use the credential source if configured, same as streaming requests.
        let dynamic_auth: Option<Vec<(String, String)>> = if let Some(src) = &self.config.credential_source {
            src.auth_headers().await
        } else {
            None
        };

        let builder = self.get_builder_with_auth(&url, dynamic_auth.as_deref());

        // Unlike streaming, this has no per-chunk idle timeout to fall back
        // on, so a half-open connection would hang it forever.
        let response = tokio::time::timeout(MODELS_TIMEOUT, builder.send())
            .await
            .map_err(|_| LlmError::Transport("listing models timed out".into()))?
            .map_err(|error| LlmError::Transport(error.to_string()))?;

        if !response.status().is_success() {
            let status = response.status().as_u16();
            let body = response.text().await.unwrap_or_default();
            return Err(LlmError::from_status(status, &body, None));
        }

        let value: serde_json::Value = tokio::time::timeout(MODELS_TIMEOUT, response.json())
            .await
            .map_err(|_| LlmError::Transport("reading the model list timed out".into()))?
            .map_err(|error| LlmError::Protocol(error.to_string()))?;

        Ok(parse_models(&value))   }
}

/// Parses a `/v1/models` payload.
pub fn parse_models(value: &serde_json::Value) -> Vec<ModelInfo> {
    value
        .get("data")
        .and_then(serde_json::Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(|item| {
                    let id = item.get("id")?.as_str()?.to_string();
                    Some(ModelInfo {
                        display_name: item
                            .get("display_name")
                            .and_then(serde_json::Value::as_str)
                            .map(str::to_string),
                        context_limit: item
                            .get("context_window")
                            .and_then(serde_json::Value::as_u64)
                            .map(|n| n as u32),
                        supported_endpoints: Vec::new(),
                        // Anthropic does not advertise reasoning levels; the
                        // capability is derived from static rules on the id.
                        reasoning_levels: Vec::new(),
                        id,
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn builds_the_messages_endpoint() {
        let client = AnthropicClient::new(AnthropicConfig::api_key("k")).expect("client");
        assert_eq!(client.endpoint(), "https://api.anthropic.com/v1/messages");
    }

    #[test]
    fn tolerates_a_base_url_with_a_trailing_slash() {
        let config = AnthropicConfig::api_key("k").with_base_url("https://proxy.example.com/");
        let client = AnthropicClient::new(config).expect("client");
        assert_eq!(client.endpoint(), "https://proxy.example.com/v1/messages");
    }

    #[test]
    fn reports_its_provider_id() {
        let client = AnthropicClient::new(AnthropicConfig::api_key("k")).expect("client");
        assert_eq!(client.provider_id(), "anthropic");
    }

    #[test]
    fn parses_a_model_list() {
        let value = json!({
            "data": [
                { "id": "claude-opus-5", "display_name": "Claude Opus 5", "context_window": 200000 },
                { "id": "claude-sonnet-5" }
            ]
        });
        let models = parse_models(&value);

        assert_eq!(models.len(), 2);
        assert_eq!(models[0].id, "claude-opus-5");
        assert_eq!(models[0].label(), "Claude Opus 5");
        assert_eq!(models[0].context_limit, Some(200_000));
        assert_eq!(models[1].label(), "claude-sonnet-5", "falls back to the id");
    }

    #[test]
    fn an_entry_without_an_id_is_skipped() {
        let models = parse_models(&json!({ "data": [{ "display_name": "nameless" }] }));
        assert!(models.is_empty());
    }

    #[test]
    fn an_unexpected_model_payload_yields_no_models() {
        assert!(parse_models(&json!({})).is_empty());
        assert!(parse_models(&json!({ "data": "not an array" })).is_empty());
    }

    #[test]
    fn api_key_auth_is_distinct_from_bearer() {
        let key = AnthropicConfig::api_key("secret");
        assert!(matches!(key.auth, Auth::ApiKey(_)));

        let bearer = AnthropicConfig {
            auth: Auth::Bearer("token".into()),
            ..AnthropicConfig::api_key("unused")
        };
        assert!(matches!(bearer.auth, Auth::Bearer(_)));
    }

    #[test]
    fn the_default_config_retries() {
        let config = AnthropicConfig::api_key("k");
        assert!(config.retry.max_attempts > 1);
    }

    #[test]
    fn retry_can_be_disabled() {
        let config = AnthropicConfig::api_key("k").with_retry(RetryPolicy::none());
        assert_eq!(config.retry.max_attempts, 1);
    }
}
