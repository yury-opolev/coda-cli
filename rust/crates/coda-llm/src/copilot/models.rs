//! Model metadata, endpoint selection, and the per-base-URL model-list cache.
//!
//! Copilot proxies three APIs at once. The `/models` endpoint advertises which
//! a model supports, so we can route away from `/chat/completions` before a
//! newer model 400s on it. Successful non-empty metadata is cached for 15 minutes
//! keyed by base URL so model listing adds no round trip to streaming calls.

use std::{
    collections::{HashMap, HashSet},
    sync::{Mutex, OnceLock},
    time::{Duration, Instant},
};

use serde_json::Value;

use crate::message::ModelInfo;

/// Which Copilot API a streaming request should be sent to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CopilotEndpoint {
    /// OpenAI chat-completions at `/chat/completions`.
    ChatCompletions,
    /// Anthropic Messages at `/v1/messages`.
    Messages,
    /// OpenAI Responses at `/responses`.
    Responses,
}

/// Selects the best endpoint from the model's `supported_endpoints` list.
///
/// Priority: Responses > Anthropic Messages > Chat Completions. Responses is
/// tested first because a model advertising both Responses and Messages should
/// prefer the newer API.
pub fn resolve_endpoint(model: &ModelInfo) -> CopilotEndpoint {
    for ep in &model.supported_endpoints {
        if ep.eq_ignore_ascii_case("/responses") || ep.eq_ignore_ascii_case("/v1/responses") {
            return CopilotEndpoint::Responses;
        }
    }
    for ep in &model.supported_endpoints {
        if ep.eq_ignore_ascii_case("/v1/messages") {
            return CopilotEndpoint::Messages;
        }
    }
    CopilotEndpoint::ChatCompletions
}

/// Normalizes a Claude model id to the dotted form Copilot's live API requires.
///
/// `claude-opus-4-8` → `claude-opus-4.8`. Only the final `-N-N` tail of a
/// `claude-<word>-<digits>-<digits>` pattern is rewritten; everything else is
/// left unchanged so non-Claude models and already-dotted ids pass through.
pub fn normalize_model_id(id: &str) -> std::borrow::Cow<str> {
    if !id.starts_with("claude-") {
        return std::borrow::Cow::Borrowed(id);
    }
    let Some(last_dash) = id.rfind('-') else {
        return std::borrow::Cow::Borrowed(id);
    };
    let minor = &id[last_dash + 1..];
    if minor.is_empty() || !minor.bytes().all(|b| b.is_ascii_digit()) {
        return std::borrow::Cow::Borrowed(id);
    }
    let prefix = &id[..last_dash];
    let rest = &prefix["claude-".len()..];
    let Some(sep) = rest.find('-') else {
        return std::borrow::Cow::Borrowed(id);
    };
    let family = &rest[..sep];
    let major = &rest[sep + 1..];
    if family.is_empty()
        || !family.bytes().all(|b| b.is_ascii_lowercase())
        || major.is_empty()
        || !major.bytes().all(|b| b.is_ascii_digit())
    {
        return std::borrow::Cow::Borrowed(id);
    }
    std::borrow::Cow::Owned(format!("{prefix}.{minor}"))
}

const CACHE_TTL: Duration = Duration::from_secs(15 * 60);

struct CacheEntry {
    models: Vec<ModelInfo>,
    fetched_at: Instant,
}

static CACHE: OnceLock<Mutex<HashMap<String, CacheEntry>>> = OnceLock::new();

fn global_cache() -> &'static Mutex<HashMap<String, CacheEntry>> {
    CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}

/// Returns the cached model list for `base_url` if still within the 15-minute TTL.
pub fn cache_get(base_url: &str) -> Option<Vec<ModelInfo>> {
    let guard = global_cache().lock().unwrap_or_else(|p| p.into_inner());
    let entry = guard.get(base_url)?;
    if entry.fetched_at.elapsed() < CACHE_TTL {
        Some(entry.models.clone())
    } else {
        None
    }
}

/// Stores a non-empty model list. Empty results are never cached because they
/// cannot be distinguished from a failed or partial fetch.
pub fn cache_set(base_url: &str, models: Vec<ModelInfo>) {
    if models.is_empty() {
        return;
    }
    let mut guard = global_cache().lock().unwrap_or_else(|p| p.into_inner());
    guard.insert(
        base_url.to_string(),
        CacheEntry {
            models,
            fetched_at: Instant::now(),
        },
    );
}

/// Removes any cached entry for `base_url`, used before a forced refresh.
pub fn cache_invalidate(base_url: &str) {
    let mut guard = global_cache().lock().unwrap_or_else(|p| p.into_inner());
    guard.remove(base_url);
}

/// Parses the Copilot `GET /models` response.
///
/// Only chat-capable (`capabilities.type == "chat"`) and picker-enabled
/// (`model_picker_enabled != false`) models are kept, de-duplicated by id.
pub fn parse_models(value: &Value) -> Vec<ModelInfo> {
    let Some(data) = value.get("data").and_then(Value::as_array) else {
        return Vec::new();
    };

    let mut seen = HashSet::new();
    let mut models = Vec::new();

    for item in data {
        let id = match item.get("id").and_then(Value::as_str) {
            Some(id) if !id.is_empty() => id.to_string(),
            _ => continue,
        };

        if let Some(cap_type) = item
            .get("capabilities")
            .and_then(|c| c.get("type"))
            .and_then(Value::as_str)
        {
            if !cap_type.eq_ignore_ascii_case("chat") {
                continue;
            }
        }

        if let Some(false) = item.get("model_picker_enabled").and_then(Value::as_bool) {
            continue;
        }

        if !seen.insert(id.to_lowercase()) {
            continue;
        }

        let display_name = item
            .get("name")
            .and_then(Value::as_str)
            .map(str::to_string);

        let context_limit = item
            .get("capabilities")
            .and_then(|c| c.get("limits"))
            .and_then(|l| {
                l.get("max_context_window_tokens")
                    .or_else(|| l.get("max_prompt_tokens"))
            })
            .and_then(Value::as_u64)
            .map(|n| n as u32);

        let supported_endpoints = item
            .get("supported_endpoints")
            .and_then(Value::as_array)
            .map(|arr| {
                arr.iter()
                    .filter_map(Value::as_str)
                    .filter(|s| !s.is_empty())
                    .map(str::to_string)
                    .collect()
            })
            .unwrap_or_default();

        models.push(ModelInfo {
            id,
            display_name,
            context_limit,
            supported_endpoints,
        });
    }

    models
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn model_with_endpoints(endpoints: &[&str]) -> ModelInfo {
        ModelInfo {
            supported_endpoints: endpoints.iter().map(|s| s.to_string()).collect(),
            ..ModelInfo::new("m")
        }
    }

    #[test]
    fn prefers_responses_over_messages_and_chat() {
        let m = model_with_endpoints(&["/v1/messages", "/responses", "/chat/completions"]);
        assert_eq!(resolve_endpoint(&m), CopilotEndpoint::Responses);
    }

    #[test]
    fn prefers_messages_over_chat_completions() {
        let m = model_with_endpoints(&["/v1/messages", "/chat/completions"]);
        assert_eq!(resolve_endpoint(&m), CopilotEndpoint::Messages);
    }

    #[test]
    fn falls_back_to_chat_when_no_endpoints_listed() {
        assert_eq!(resolve_endpoint(&ModelInfo::new("m")), CopilotEndpoint::ChatCompletions);
    }

    #[test]
    fn endpoint_matching_is_case_insensitive() {
        let m = model_with_endpoints(&["/RESPONSES"]);
        assert_eq!(resolve_endpoint(&m), CopilotEndpoint::Responses);
        let m2 = model_with_endpoints(&["/V1/MESSAGES"]);
        assert_eq!(resolve_endpoint(&m2), CopilotEndpoint::Messages);
    }

    #[test]
    fn accepts_v1_prefixed_responses_endpoint() {
        let m = model_with_endpoints(&["/v1/responses"]);
        assert_eq!(resolve_endpoint(&m), CopilotEndpoint::Responses);
    }

    #[test]
    fn normalizes_dashed_claude_minor_version_to_dotted() {
        assert_eq!(normalize_model_id("claude-opus-4-8"), "claude-opus-4.8");
        assert_eq!(normalize_model_id("claude-sonnet-4-5"), "claude-sonnet-4.5");
        assert_eq!(normalize_model_id("claude-haiku-3-5"), "claude-haiku-3.5");
    }

    #[test]
    fn leaves_already_dotted_claude_ids_unchanged() {
        assert_eq!(normalize_model_id("claude-opus-4.8"), "claude-opus-4.8");
    }

    #[test]
    fn leaves_non_claude_models_unchanged() {
        assert_eq!(normalize_model_id("gpt-4o"), "gpt-4o");
        assert_eq!(normalize_model_id("o4-mini"), "o4-mini");
    }

    #[test]
    fn does_not_normalize_multi_segment_ids() {
        // The regex matches only `claude-<word>-<digits>-<digits>` at the end.
        assert_eq!(
            normalize_model_id("claude-sonnet-4-5-20250514"),
            "claude-sonnet-4-5-20250514"
        );
    }

    #[test]
    fn parse_models_keeps_chat_capable_only() {
        let value = json!({
            "data": [
                { "id": "gpt-4o", "capabilities": { "type": "chat" } },
                { "id": "dall-e", "capabilities": { "type": "image_generation" } },
            ]
        });
        let models = parse_models(&value);
        assert_eq!(models.len(), 1);
        assert_eq!(models[0].id, "gpt-4o");
    }

    #[test]
    fn parse_models_includes_models_without_capability_type() {
        let value = json!({ "data": [{ "id": "gpt-4o" }] });
        assert_eq!(parse_models(&value).len(), 1);
    }

    #[test]
    fn parse_models_respects_picker_disabled_flag() {
        let value = json!({
            "data": [
                { "id": "visible" },
                { "id": "hidden", "model_picker_enabled": false },
            ]
        });
        let models = parse_models(&value);
        assert_eq!(models.len(), 1);
        assert_eq!(models[0].id, "visible");
    }

    #[test]
    fn parse_models_deduplicates_case_insensitively() {
        let value = json!({ "data": [{ "id": "gpt-4o" }, { "id": "GPT-4O" }] });
        assert_eq!(parse_models(&value).len(), 1);
    }

    #[test]
    fn parse_models_reads_display_name_and_context_limit() {
        let value = json!({
            "data": [{
                "id": "gpt-4o",
                "name": "GPT-4o",
                "capabilities": { "limits": { "max_context_window_tokens": 128000 } }
            }]
        });
        let models = parse_models(&value);
        assert_eq!(models[0].display_name.as_deref(), Some("GPT-4o"));
        assert_eq!(models[0].context_limit, Some(128_000));
    }

    #[test]
    fn parse_models_falls_back_to_max_prompt_tokens() {
        let value = json!({
            "data": [{
                "id": "m",
                "capabilities": { "limits": { "max_prompt_tokens": 65536 } }
            }]
        });
        assert_eq!(parse_models(&value)[0].context_limit, Some(65_536));
    }

    #[test]
    fn parse_models_reads_supported_endpoints() {
        let value = json!({
            "data": [{ "id": "m", "supported_endpoints": ["/chat/completions", "/responses"] }]
        });
        let models = parse_models(&value);
        assert_eq!(models[0].supported_endpoints, ["/chat/completions", "/responses"]);
    }

    #[test]
    fn parse_models_skips_entries_without_id() {
        let value = json!({ "data": [{ "name": "nameless" }] });
        assert!(parse_models(&value).is_empty());
    }

    #[test]
    fn parse_models_handles_unexpected_payload_shapes() {
        assert!(parse_models(&json!({})).is_empty());
        assert!(parse_models(&json!({ "data": [] })).is_empty());
        assert!(parse_models(&json!({ "data": "not-array" })).is_empty());
    }

    #[test]
    fn cache_returns_none_when_not_populated() {
        assert!(cache_get("http://not-cached.test").is_none());
    }

    #[test]
    fn cache_does_not_store_empty_lists() {
        let url = "http://empty-list.test";
        cache_set(url, Vec::new());
        assert!(cache_get(url).is_none());
    }

    #[test]
    fn cache_stores_and_retrieves_models() {
        let url = "http://cache-store.test";
        cache_set(url, vec![ModelInfo::new("gpt-4o")]);
        let retrieved = cache_get(url).expect("should be cached");
        assert_eq!(retrieved[0].id, "gpt-4o");
    }

    #[test]
    fn cache_invalidate_removes_entry() {
        let url = "http://cache-invalidate.test";
        cache_set(url, vec![ModelInfo::new("gpt-4o")]);
        cache_invalidate(url);
        assert!(cache_get(url).is_none());
    }
}
