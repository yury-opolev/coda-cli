//! HTTP transport for the GitHub Copilot provider.
//!
//! The client selects the right protocol (chat-completions, Responses, or
//! Anthropic Messages) per model based on metadata from `GET /models`. Model
//! metadata is cached for 15 minutes so it does not add a round trip to every
//! streaming call. Dropping the `ResponseStream` cancels the underlying request.

use std::time::Duration;

use tokio::sync::mpsc;

use crate::anthropic::protocol::AnthropicDecoder;
use crate::anthropic::StreamEvent;
use crate::client::{LlmClient, ResponseStream};
use crate::error::{parse_retry_after, LlmError};
use crate::message::{ChatRequest, ModelInfo};
use crate::pump::ProtocolDecoder;
use crate::retry::RetryPolicy;

use super::chat::ChatDecoder;
use super::models::{self, CopilotEndpoint};
use super::responses::ResponsesDecoder;

/// Anthropic version header required by the Messages endpoint.
const ANTHROPIC_API_VERSION: &str = "2023-06-01";

const CONNECT_TIMEOUT: Duration = Duration::from_secs(30);
const CHANNEL_DEPTH: usize = 256;
const MODELS_TIMEOUT: Duration = Duration::from_secs(30);

/// Configuration for [`CopilotClient`].
#[derive(Debug, Clone)]
pub struct CopilotConfig {
    pub base_url: String,
    pub token: String,
    pub retry: RetryPolicy,
    /// Optional editor / plugin identification headers sent with every request.
    pub extra_headers: Vec<(String, String)>,
    /// Optional dynamic credential source; when present its `Authorization: Bearer`
    /// header overrides the static `token` field so tokens are always fresh.
    pub credential_source: Option<std::sync::Arc<dyn crate::credential_source::CredentialSource>>,
}

impl CopilotConfig {
    pub fn with_token(token: impl Into<String>) -> Self {
        Self {
            base_url: "https://api.githubcopilot.com".into(),
            token: token.into(),
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

    /// Appends an editor identification header (e.g. `editor-version`).
    pub fn with_header(mut self, name: impl Into<String>, value: impl Into<String>) -> Self {
        self.extra_headers.push((name.into(), value.into()));
        self
    }

    /// Attach a dynamic credential source.  Its `Authorization: Bearer` header
    /// overrides the static `token` field so refreshed tokens are used immediately.
    pub fn with_credential_source(
        mut self,
        src: std::sync::Arc<dyn crate::credential_source::CredentialSource>,
    ) -> Self {
        self.credential_source = Some(src);
        self
    }
}

/// GitHub Copilot provider client.
pub struct CopilotClient {
    http: reqwest::Client,
    config: CopilotConfig,
}

impl CopilotClient {
    pub fn new(config: CopilotConfig) -> Result<Self, LlmError> {
        let http = reqwest::Client::builder()
            .connect_timeout(CONNECT_TIMEOUT)
            .build()
            .map_err(|e| LlmError::Transport(e.to_string()))?;

        Ok(Self { http, config })
    }

    fn endpoint_url(&self, endpoint: CopilotEndpoint) -> String {
        let base = self.config.base_url.trim_end_matches('/');
        match endpoint {
            CopilotEndpoint::ChatCompletions => format!("{base}/chat/completions"),
            CopilotEndpoint::Messages => format!("{base}/v1/messages"),
            CopilotEndpoint::Responses => format!("{base}/responses"),
        }
    }

    fn endpoint_for(&self, model_id: &str) -> CopilotEndpoint {
        models::cache_get(&self.config.base_url)
            .as_deref()
            .and_then(|ms| ms.iter().find(|m| m.id.eq_ignore_ascii_case(model_id)))
            .map(models::resolve_endpoint)
            .unwrap_or(CopilotEndpoint::ChatCompletions)
    }

    /// Build a POST request applying auth headers.
    ///
    /// `dynamic_auth` (from the credential source) overrides the static `token`
    /// field so refreshed Copilot tokens are used immediately.
    fn auth_post_with_auth(
        &self,
        url: &str,
        dynamic_auth: Option<&[(String, String)]>,
    ) -> reqwest::RequestBuilder {
        let mut builder = self
            .http
            .post(url)
            .header("content-type", "application/json")
            .header("accept", "text/event-stream");

        if let Some(headers) = dynamic_auth {
            for (name, value) in headers {
                builder = builder.header(name.as_str(), value.as_str());
            }
        } else {
            builder = builder.header("authorization", format!("Bearer {}", self.config.token));
        }

        for (name, value) in &self.config.extra_headers {
            builder = builder.header(name.as_str(), value.as_str());
        }
        builder
    }

    /// Build a GET request applying auth headers (used for model listing).
    fn auth_get_with_auth(
        &self,
        url: &str,
        dynamic_auth: Option<&[(String, String)]>,
    ) -> reqwest::RequestBuilder {
        let mut builder = self.http.get(url);

        if let Some(headers) = dynamic_auth {
            for (name, value) in headers {
                builder = builder.header(name.as_str(), value.as_str());
            }
        } else {
            builder = builder.header("authorization", format!("Bearer {}", self.config.token));
        }

        for (name, value) in &self.config.extra_headers {
            builder = builder.header(name.as_str(), value.as_str());
        }
        builder
    }
    /// Fetches models from the API and stores them in the cache on success.
    async fn do_list_models(&self) -> Result<Vec<ModelInfo>, LlmError> {
        let url = format!(
            "{}/models",
            self.config.base_url.trim_end_matches('/')
        );

        // Use the credential source if configured, same as streaming requests.
        let dynamic_auth: Option<Vec<(String, String)>> = if let Some(src) = &self.config.credential_source {
            src.auth_headers().await
        } else {
            None
        };

        let response = tokio::time::timeout(MODELS_TIMEOUT, self.auth_get_with_auth(&url, dynamic_auth.as_deref()).send())
            .await
            .map_err(|_| LlmError::Transport("listing models timed out".into()))?
            .map_err(|e| LlmError::Transport(e.to_string()))?;

        if !response.status().is_success() {
            let status = response.status().as_u16();
            let body = response.text().await.unwrap_or_default();
            return Err(LlmError::from_status(status, &body, None));
        }

        let value: serde_json::Value = tokio::time::timeout(MODELS_TIMEOUT, response.json())
            .await
            .map_err(|_| LlmError::Transport("reading model list timed out".into()))?
            .map_err(|e| LlmError::Protocol(e.to_string()))?;

        let fetched = models::parse_models(&value);
        models::cache_set(&self.config.base_url, fetched.clone());
        Ok(fetched)
    }

    async fn send_with_retry(
        &self,
        url: &str,
        endpoint: CopilotEndpoint,
        body: &serde_json::Value,
        dynamic_auth: Option<&[(String, String)]>,
    ) -> Result<reqwest::Response, LlmError> {
        let mut attempt = 1u32;

        loop {
            let mut builder = self.auth_post_with_auth(url, dynamic_auth).json(body);
            if endpoint == CopilotEndpoint::Messages {
                builder = builder.header("anthropic-version", ANTHROPIC_API_VERSION);
            }

            let error = match builder.send().await {
                Ok(response) if response.status().is_success() => return Ok(response),
                Ok(response) => {
                    let status = response.status().as_u16();
                    let retry_after = response
                        .headers()
                        .get("retry-after")
                        .and_then(|v| v.to_str().ok())
                        .and_then(parse_retry_after);
                    let body = response.text().await.unwrap_or_default();
                    LlmError::from_status(status, &body, retry_after)
                }
                Err(e) if e.is_timeout() => LlmError::Transport(format!("request timed out: {e}")),
                Err(e) => LlmError::Transport(e.to_string()),
            };

            match self.config.retry.delay_before(attempt + 1, &error) {
                Some(delay) => {
                    tracing::warn!(attempt, ?delay, %error, "copilot request failed; retrying");
                    tokio::time::sleep(delay).await;
                    attempt += 1;
                }
                None => return Err(error),
            }
        }
    }

    /// Selects the endpoint, sends the request, and handles the chat-completions
    /// 400 mismatch case by refreshing metadata and retrying on a better endpoint.
    async fn prepare_and_send(
        &self,
        request: &ChatRequest,
    ) -> Result<(CopilotEndpoint, reqwest::Response), LlmError> {
        // Best-effort metadata load — failure falls back to ChatCompletions.
        if models::cache_get(&self.config.base_url).is_none() {
            let _ = self.do_list_models().await;
        }

        // Fetch dynamic auth once per request (before endpoint selection / retry).
        let dynamic_auth_owned: Option<Vec<(String, String)>> = if let Some(src) = &self.config.credential_source {
            src.auth_headers().await
        } else {
            None
        };
        let dynamic_auth = dynamic_auth_owned.as_deref();

        let endpoint = self.endpoint_for(&request.model);
        let url = self.endpoint_url(endpoint);
        let body = build_body(request, endpoint);

        match self.send_with_retry(&url, endpoint, &body, dynamic_auth).await {
            Ok(r) => Ok((endpoint, r)),
            Err(err) if endpoint == CopilotEndpoint::ChatCompletions && is_chat_mismatch(&err) => {
                // The model rejected /chat/completions. Refresh metadata and retry on the
                // correct endpoint — this happens when a model's entry arrives in the catalog
                // without being in our cached list yet.
                models::cache_invalidate(&self.config.base_url);
                let _ = self.do_list_models().await;

                let new_endpoint = self.endpoint_for(&request.model);
                if new_endpoint == CopilotEndpoint::ChatCompletions {
                    return Err(err);
                }

                let new_url = self.endpoint_url(new_endpoint);
                let new_body = build_body(request, new_endpoint);
                let r = self.send_with_retry(&new_url, new_endpoint, &new_body, dynamic_auth).await?;
                Ok((new_endpoint, r))
            }
            Err(err) => Err(err),
        }
    }
}

#[async_trait::async_trait]
impl LlmClient for CopilotClient {
    fn provider_id(&self) -> &str {
        "github-copilot"
    }

    async fn stream(&self, request: ChatRequest) -> Result<ResponseStream, LlmError> {
        // Normalize `claude-opus-4-8` to `claude-opus-4.8` so the live API accepts it.
        let mut request = request;
        let normalized = models::normalize_model_id(&request.model);
        if *normalized != request.model {
            request.model = normalized.into_owned();
        }

        let (endpoint, response) = self.prepare_and_send(&request).await?;

        let (tx, rx) = mpsc::channel(CHANNEL_DEPTH);
        let decoder = match endpoint {
            CopilotEndpoint::ChatCompletions => Decoder::Chat(ChatDecoder::new()),
            CopilotEndpoint::Responses => Decoder::Responses(ResponsesDecoder::new()),
            CopilotEndpoint::Messages => Decoder::Messages(AnthropicDecoder::new()),
        };
        tokio::spawn(crate::pump::pump(response, decoder, tx));
        Ok(ResponseStream::new(rx))
    }

    async fn list_models(&self) -> Result<Vec<ModelInfo>, LlmError> {
        if let Some(cached) = models::cache_get(&self.config.base_url) {
            return Ok(cached);
        }
        self.do_list_models().await
    }

    async fn refresh_models(&self) -> Result<Vec<ModelInfo>, LlmError> {
        models::cache_invalidate(&self.config.base_url);
        self.do_list_models().await
    }
}

fn build_body(request: &ChatRequest, endpoint: CopilotEndpoint) -> serde_json::Value {
    match endpoint {
        CopilotEndpoint::Messages => crate::anthropic::request::build(request),
        CopilotEndpoint::Responses => super::responses::build(request),
        CopilotEndpoint::ChatCompletions => super::chat::build(request),
    }
}

/// A 400 whose message mentions `/chat/completions` being inaccessible for the
/// model means we should retry on a different endpoint.
fn is_chat_mismatch(err: &LlmError) -> bool {
    let LlmError::Api {
        status: 400,
        message,
        body,
        ..
    } = err
    else {
        return false;
    };
    // Prefer the raw body so the marker is found even when it sits outside
    // `error.message` or past the 500-char truncation point.
    let haystack = body.as_deref().unwrap_or(message.as_str());
    let haystack_lc = haystack.to_ascii_lowercase();
    haystack_lc.contains("/chat/completions")
        && (haystack_lc.contains("not accessible") || haystack_lc.contains("not supported"))
}

/// Enum dispatch over the three protocol decoders so a single `pump` handles all.
enum Decoder {
    Chat(ChatDecoder),
    Responses(ResponsesDecoder),
    Messages(AnthropicDecoder),
}

impl ProtocolDecoder for Decoder {
    fn decode(&mut self, name: &str, data: &str) -> Result<Vec<StreamEvent>, LlmError> {
        match self {
            Decoder::Chat(d) => {
                if data.trim() == "[DONE]" {
                    Ok(d.flush())
                } else {
                    d.decode(data)
                }
            }
            Decoder::Responses(d) => d.decode(name, data),
            Decoder::Messages(d) => d.decode(name, data),
        }
    }

    fn finished(&self) -> bool {
        match self {
            Decoder::Chat(d) => d.finished(),
            Decoder::Responses(d) => d.finished(),
            Decoder::Messages(d) => d.finished(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn config(base_url: &str) -> CopilotConfig {
        CopilotConfig::with_token("test-key")
            .with_base_url(base_url)
            .with_retry(RetryPolicy::none())
    }

    fn client(base_url: &str) -> CopilotClient {
        CopilotClient::new(config(base_url)).expect("client")
    }

    #[test]
    fn reports_provider_id() {
        let c = client("https://api.githubcopilot.com");
        assert_eq!(c.provider_id(), "github-copilot");
    }

    #[test]
    fn builds_chat_completions_endpoint() {
        let c = client("https://api.githubcopilot.com");
        assert_eq!(
            c.endpoint_url(CopilotEndpoint::ChatCompletions),
            "https://api.githubcopilot.com/chat/completions"
        );
    }

    #[test]
    fn builds_messages_endpoint() {
        let c = client("https://api.githubcopilot.com");
        assert_eq!(
            c.endpoint_url(CopilotEndpoint::Messages),
            "https://api.githubcopilot.com/v1/messages"
        );
    }

    #[test]
    fn builds_responses_endpoint() {
        let c = client("https://api.githubcopilot.com");
        assert_eq!(
            c.endpoint_url(CopilotEndpoint::Responses),
            "https://api.githubcopilot.com/responses"
        );
    }

    #[test]
    fn tolerates_a_trailing_slash_in_base_url() {
        let c = client("https://proxy.example.com/");
        assert_eq!(
            c.endpoint_url(CopilotEndpoint::ChatCompletions),
            "https://proxy.example.com/chat/completions"
        );
    }

    #[test]
    fn default_config_retries() {
        let config = CopilotConfig::with_token("k");
        assert!(config.retry.max_attempts > 1);
    }

    #[test]
    fn retry_can_be_disabled() {
        let config = CopilotConfig::with_token("k").with_retry(RetryPolicy::none());
        assert_eq!(config.retry.max_attempts, 1);
    }

    #[test]
    fn is_chat_mismatch_detects_not_accessible_messages() {
        let err = LlmError::from_status(
            400,
            r#"{"error":{"message":"This model is not accessible via /chat/completions endpoint"}}"#,
            None,
        );
        assert!(is_chat_mismatch(&err));
    }

    #[test]
    fn is_chat_mismatch_ignores_other_400s() {
        let err = LlmError::from_status(400, r#"{"error":{"message":"bad input"}}"#, None);
        assert!(!is_chat_mismatch(&err));
    }

    #[test]
    fn is_chat_mismatch_ignores_non_400_statuses() {
        let err = LlmError::from_status(
            500,
            r#"{"error":{"message":"not accessible via /chat/completions"}}"#,
            None,
        );
        assert!(!is_chat_mismatch(&err));
    }

    #[test]
    fn is_chat_mismatch_fires_when_marker_is_outside_error_message() {
        // `extract_message` pulls only `error.message`, which here is generic.
        // The marker lives in a sibling field that would be invisible if we only
        // checked the truncated `message`. Detection must use the raw body.
        let err = LlmError::from_status(
            400,
            r#"{"error":{"message":"generic error","detail":"not accessible via /chat/completions"}}"#,
            None,
        );
        let LlmError::Api { message, .. } = &err else { panic!("expected Api error") };
        assert!(!message.contains("/chat/completions"), "test setup: message must not have the marker");
        assert!(is_chat_mismatch(&err));
    }

    #[test]
    fn parses_a_model_list_with_endpoints() {
        let value = json!({
            "data": [
                { "id": "gpt-4o", "supported_endpoints": ["/chat/completions", "/responses"] },
                { "id": "claude-opus-5", "supported_endpoints": ["/v1/messages"] },
            ]
        });
        let models = models::parse_models(&value);
        assert_eq!(models.len(), 2);
        assert_eq!(
            models::resolve_endpoint(&models[0]),
            CopilotEndpoint::Responses
        );
        assert_eq!(
            models::resolve_endpoint(&models[1]),
            CopilotEndpoint::Messages
        );
    }
}
