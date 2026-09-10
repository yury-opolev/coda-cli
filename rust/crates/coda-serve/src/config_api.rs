//! `config/describe` and `config/set`.
//!
//! # Honesty rules
//!
//! - `appliesAt` is derived from what the engine *does*. There is no
//!   pending-change scheduler: `model`/`effort`/`systemPrompt`/`goal` are read
//!   when the next turn is built, `permissionMode` at the next permission
//!   check, and `provider` only when a new engine process starts. The catalog
//!   says exactly that, and `session/getState` derives `config.differing` from
//!   the same table.
//! - `mutable` is `true` only where `config/set` genuinely delegates to an
//!   existing, already-validated engine method. It never means "in principle".
//! - Nothing here dumps a settings file. Each entry is an explicit, named key
//!   with a documented owner — a raw settings dump would leak provider
//!   credentials, MCP `env` values and custom headers by construction.
//! - Absent values are omitted, never defaulted. "The engine has not resolved
//!   a provider yet" is reported as an absent value, not as `"anthropic"`.
//!
//! # Delegation
//!
//! `config/set` never writes anything itself. It maps a key onto the existing
//! RPC (`session/setModel`, `session/setEffort`, `session/setPermissionMode`,
//! `session/setSystemPrompt`) and reports what the engine actually holds
//! afterwards — read back, never echoed from the request.

use coda_proto::config::{AllowedValue, AppliesWhen, ConfigEntryDto, ConfigOwner};
use serde_json::Value;

/// The keys `config/set` accepts, each bound to the validated method it
/// delegates to. A key not in this table is refused with the reason from the
/// catalog rather than silently ignored.
pub const MUTABLE_KEYS: &[&str] = &["model", "effort", "permissionMode", "systemPrompt"];

pub fn applies_at(key: &str) -> AppliesWhen {
    match key {
        "model" | "effort" | "systemPrompt" | "goal" => AppliesWhen::NextTurn,
        "permissionMode" => AppliesWhen::NextPermissionCheck,
        "provider" | "mcpServers" | "hooks" | "plugins" => AppliesWhen::NewEngineInstance,
        _ => AppliesWhen::ClientLocal,
    }
}

/// Everything the engine can state about its own configuration, gathered by
/// the host and turned into DTOs here.
pub struct ConfigFacts {
    pub provider_id: Option<String>,
    pub model: String,
    pub effort: Option<String>,
    pub effort_is_auto: bool,
    /// The effort levels the *active* model actually supports, when the
    /// engine knows them. `None` means "not resolved" — never an empty list
    /// pretending the model supports nothing.
    pub effort_levels: Option<Vec<String>>,
    pub effort_supports_auto: bool,
    pub permission_mode: String,
    pub system_prompt: Option<String>,
    pub goal: Option<String>,
    /// Captured into the running turn, when one is running.
    pub active_model: Option<String>,
    pub active_effort: Option<String>,
    pub active_permission_mode: Option<String>,
    pub mcp_enabled: bool,
    pub mcp_server_count: Option<i64>,
    /// Built-in + plugin output-style names, published so a client does not
    /// have to link `coda-agent` to enumerate them.
    pub output_styles: Vec<AllowedValue>,
}

/// The permission modes the engine accepts, spelled exactly as
/// `session/setPermissionMode` parses them.
pub fn permission_modes() -> Vec<AllowedValue> {
    vec![
        AllowedValue::described("default", "Ask before each non-read-only tool."),
        AllowedValue::described("acceptEdits", "Auto-approve edits within the workspace."),
        AllowedValue::described("plan", "Plan only; tool execution stays gated."),
        AllowedValue::described("bypassPermissions", "Run every tool without asking (YOLO)."),
    ]
}

pub fn describe(facts: &ConfigFacts) -> Vec<ConfigEntryDto> {
    let mut entries = Vec::new();

    entries.push(ConfigEntryDto {
        key: "model".into(),
        owner: ConfigOwner::Session,
        applies_at: AppliesWhen::NextTurn,
        mutable: true,
        reason: None,
        value: Some(Value::String(facts.model.clone())),
        active_value: facts.active_model.clone().map(Value::String),
        // Deliberately no `allowedValues`: the real list is provider-live and
        // paginated, and `session/models` is the method that owns it. A stale
        // snapshot of it here would be a promise the engine cannot keep.
        allowed_values: None,
        allowed_values_from: Some(coda_proto::messages::method::MODELS.to_string()),
        description: "The model subsequent turns are built with. `session/models` lists what the connected provider offers.".into(),
    });

    entries.push(ConfigEntryDto {
        key: "effort".into(),
        owner: ConfigOwner::Session,
        applies_at: AppliesWhen::NextTurn,
        mutable: true,
        reason: None,
        value: match (&facts.effort, facts.effort_is_auto) {
            (Some(e), _) => Some(Value::String(e.clone())),
            (None, true) => Some(Value::String("auto".into())),
            (None, false) => None,
        },
        active_value: facts.active_effort.clone().map(Value::String),
        allowed_values: facts.effort_levels.as_ref().map(|levels| {
            let mut values: Vec<AllowedValue> = Vec::new();
            if facts.effort_supports_auto {
                values.push(AllowedValue::described(
                    "auto",
                    "Let the provider choose; clears any explicit level.",
                ));
            }
            values.extend(levels.iter().map(AllowedValue::new));
            values
        }),
        // The set is per-model and re-resolved on `session/setModel`, so even
        // when it is known here it is a snapshot. `model/reasoningCapability`
        // is the live authority; naming it means an *absent* `allowedValues`
        // is still actionable rather than a dead end.
        allowed_values_from: Some(coda_proto::messages::method::REASONING_CAPABILITY.to_string()),
        description: "Reasoning effort for the active model. Per-model: switching models re-resolves it rather than carrying a stale level.".into(),
    });

    entries.push(ConfigEntryDto {
        key: "permissionMode".into(),
        owner: ConfigOwner::Session,
        applies_at: AppliesWhen::NextPermissionCheck,
        mutable: true,
        reason: None,
        value: Some(Value::String(facts.permission_mode.clone())),
        active_value: facts.active_permission_mode.clone().map(Value::String),
        allowed_values: Some(permission_modes()),
        allowed_values_from: None,
        description: "How tool permissions are decided. The running turn keeps the mode it captured; the next check reads this one.".into(),
    });

    entries.push(ConfigEntryDto {
        key: "systemPrompt".into(),
        owner: ConfigOwner::Session,
        applies_at: AppliesWhen::NextTurn,
        mutable: true,
        reason: None,
        // Session-only and never persisted. Reported as its *source* plus the
        // override text when one is set — the operator authored it, so it is
        // theirs to read back, but a `null` here means "no override", not
        // "hidden".
        value: Some(serde_json::json!({
            "source": if facts.system_prompt.is_some() { "sessionOverride" } else { "default" },
            "text": facts.system_prompt,
        })),
        active_value: None,
        allowed_values: None,
        allowed_values_from: None,
        description: "A session-only system prompt override. Never written to disk. Set an empty value to clear it.".into(),
    });

    entries.push(ConfigEntryDto {
        key: "goal".into(),
        owner: ConfigOwner::Session,
        applies_at: AppliesWhen::NextTurn,
        mutable: false,
        reason: Some(
            "goals carry a budget and continuation policy; use session/setGoal, which validates them together"
                .into(),
        ),
        value: facts.goal.clone().map(Value::String),
        active_value: None,
        allowed_values: None,
        allowed_values_from: None,
        description: "The goal supervisor's objective, when one is set.".into(),
    });

    entries.push(ConfigEntryDto {
        key: "provider".into(),
        owner: ConfigOwner::EngineStartup,
        applies_at: AppliesWhen::NewEngineInstance,
        mutable: false,
        reason: Some(
            "the provider and its credential are resolved when the engine process starts; changing it needs a new engine instance"
                .into(),
        ),
        // Absent until a client is actually wired — "not resolved yet" is not
        // the same as a default.
        value: facts.provider_id.clone().map(Value::String),
        active_value: None,
        allowed_values: None,
        allowed_values_from: None,
        description: "The connected LLM provider. Never reports the credential itself.".into(),
    });

    entries.push(ConfigEntryDto {
        key: "outputStyle".into(),
        owner: ConfigOwner::ClientLocal,
        applies_at: AppliesWhen::ClientLocal,
        mutable: false,
        reason: Some(
            "this engine does not apply an output style; the value lives in the client's own settings. The allowed values are published here so a client need not link coda-agent to enumerate them"
                .into(),
        ),
        value: None,
        active_value: None,
        allowed_values: Some(facts.output_styles.clone()),
        allowed_values_from: None,
        description: "Named response persona. Client-owned; the allowed values are engine-published.".into(),
    });

    entries.push(ConfigEntryDto {
        key: "mcpServers".into(),
        owner: ConfigOwner::LocalFile,
        applies_at: AppliesWhen::NewEngineInstance,
        mutable: false,
        reason: Some(
            "Coda never writes a client's .mcp.json on its behalf; MCP servers are connected when the engine starts, so an edit needs a new engine instance"
                .into(),
        ),
        value: Some(serde_json::json!({
            "enabled": facts.mcp_enabled,
            "serverCount": facts.mcp_server_count,
        })),
        active_value: None,
        allowed_values: None,
        allowed_values_from: None,
        description: "MCP servers, read-only. `mcp/list` describes them without exposing env values, headers or secret targets.".into(),
    });

    for (key, description) in [
        ("plugins", "Installed plugins."),
        ("marketplaces", "Configured plugin marketplaces."),
    ] {
        entries.push(ConfigEntryDto {
            key: key.into(),
            owner: ConfigOwner::LocalFile,
            applies_at: AppliesWhen::NewEngineInstance,
            mutable: false,
            reason: Some(
                "installing or enabling these writes the machine's filesystem, which Coda never does on a remote client's behalf; this stays local maintenance UX"
                    .into(),
            ),
            value: None,
            active_value: None,
            allowed_values: None,
            allowed_values_from: None,
            description: description.into(),
        });
    }

    for (key, description) in [
        ("theme", "Colour theme."),
        ("toolDisplayMode", "How tool calls are rendered."),
        ("keybindings", "Key bindings."),
    ] {
        entries.push(ConfigEntryDto {
            key: key.into(),
            owner: ConfigOwner::ClientLocal,
            applies_at: AppliesWhen::ClientLocal,
            mutable: false,
            reason: Some("a presentation setting the engine neither reads nor applies".into()),
            value: None,
            active_value: None,
            allowed_values: None,
            allowed_values_from: None,
            description: description.into(),
        });
    }

    entries
}

#[cfg(test)]
mod tests {
    use super::*;

    fn facts() -> ConfigFacts {
        ConfigFacts {
            provider_id: Some("anthropic".into()),
            model: "claude-opus-4-5".into(),
            effort: Some("high".into()),
            effort_is_auto: false,
            effort_levels: Some(vec!["low".into(), "medium".into(), "high".into()]),
            effort_supports_auto: true,
            permission_mode: "default".into(),
            system_prompt: None,
            goal: None,
            active_model: Some("claude-sonnet-4-5".into()),
            active_effort: None,
            active_permission_mode: Some("plan".into()),
            mcp_enabled: true,
            mcp_server_count: Some(2),
            output_styles: vec![AllowedValue::described("concise", "Terse.")],
        }
    }

    fn entry<'a>(entries: &'a [ConfigEntryDto], key: &str) -> &'a ConfigEntryDto {
        entries.iter().find(|e| e.key == key).unwrap_or_else(|| panic!("no `{key}` entry"))
    }

    #[test]
    fn every_immutable_entry_states_why() {
        for e in describe(&facts()) {
            if !e.mutable {
                assert!(
                    e.reason.as_deref().is_some_and(|r| r.len() > 20),
                    "`{}` is immutable but does not explain itself",
                    e.key
                );
            }
        }
    }

    #[test]
    fn only_genuinely_delegated_keys_are_reported_mutable() {
        let entries = describe(&facts());
        let mutable: Vec<&str> =
            entries.iter().filter(|e| e.mutable).map(|e| e.key.as_str()).collect();
        assert_eq!(mutable, MUTABLE_KEYS, "mutable must mean 'wired to a validated method'");
    }

    #[test]
    fn scopes_match_what_the_engine_actually_implements() {
        let entries = describe(&facts());
        assert_eq!(entry(&entries, "model").applies_at, AppliesWhen::NextTurn);
        assert_eq!(entry(&entries, "effort").applies_at, AppliesWhen::NextTurn);
        assert_eq!(
            entry(&entries, "permissionMode").applies_at,
            AppliesWhen::NextPermissionCheck,
            "permission mode is read at the next check, not at the next turn"
        );
        assert_eq!(entry(&entries, "systemPrompt").applies_at, AppliesWhen::NextTurn);
        assert_eq!(entry(&entries, "provider").applies_at, AppliesWhen::NewEngineInstance);
        assert_eq!(entry(&entries, "theme").applies_at, AppliesWhen::ClientLocal);
    }

    #[test]
    fn the_running_turns_captured_values_are_reported_separately_from_the_next_ones() {
        let entries = describe(&facts());
        assert_eq!(entry(&entries, "model").value, Some(Value::String("claude-opus-4-5".into())));
        assert_eq!(
            entry(&entries, "model").active_value,
            Some(Value::String("claude-sonnet-4-5".into())),
            "a mid-turn model change must not claim the running turn switched"
        );
        assert_eq!(
            entry(&entries, "permissionMode").active_value,
            Some(Value::String("plan".into()))
        );
    }

    #[test]
    fn an_unresolved_provider_is_absent_rather_than_defaulted() {
        let mut f = facts();
        f.provider_id = None;
        let entries = describe(&f);
        assert!(
            entry(&entries, "provider").value.is_none(),
            "'not resolved yet' must not be reported as a plausible default"
        );
    }

    #[test]
    fn unknown_effort_levels_are_absent_rather_than_an_empty_allowed_list() {
        let mut f = facts();
        f.effort_levels = None;
        let entries = describe(&f);
        assert!(
            entry(&entries, "effort").allowed_values.is_none(),
            "an empty list would say 'this model supports nothing'"
        );
    }

    /// An indeterminate `allowedValues` must still name the authority that
    /// *can* determine it, or a client is left guessing which levels exist.
    #[test]
    fn an_indeterminate_allowed_set_points_at_the_method_that_owns_it() {
        let mut f = facts();
        f.effort_levels = None;
        let entries = describe(&f);
        assert_eq!(
            entry(&entries, "effort").allowed_values_from.as_deref(),
            Some(coda_proto::messages::method::REASONING_CAPABILITY),
            "the dynamic source must be discoverable, not folklore"
        );
        assert_eq!(
            entry(&entries, "model").allowed_values_from.as_deref(),
            Some(coda_proto::messages::method::MODELS),
            "`model` deliberately publishes no static list either"
        );
        // A closed set names no dynamic source: there is nothing to look up.
        assert!(entry(&entries, "permissionMode").allowed_values_from.is_none());
    }

    /// Even when the levels *are* known they are per-model and can change
    /// with `session/setModel`, so the pointer stays.
    #[test]
    fn a_known_effort_set_still_names_its_live_authority() {
        let entries = describe(&facts());
        let e = entry(&entries, "effort");
        assert!(e.allowed_values.is_some());
        assert_eq!(
            e.allowed_values_from.as_deref(),
            Some(coda_proto::messages::method::REASONING_CAPABILITY)
        );
    }

    #[test]
    fn effort_offers_auto_only_when_the_model_supports_it() {
        let mut f = facts();
        f.effort_supports_auto = false;
        let values = entry(&describe(&f), "effort").allowed_values.clone().unwrap();
        assert!(!values.iter().any(|v| v.value == "auto"));

        let values = entry(&describe(&facts()), "effort").allowed_values.clone().unwrap();
        assert_eq!(values[0].value, "auto");
    }

    #[test]
    fn output_styles_are_published_so_a_client_need_not_link_the_agent_crate() {
        let entries = describe(&facts());
        let styles = entry(&entries, "outputStyle").allowed_values.clone().unwrap();
        assert!(styles.iter().any(|s| s.value == "concise"));
        assert!(!entry(&entries, "outputStyle").mutable, "the engine does not apply it");
    }

    #[test]
    fn no_entry_carries_anything_credential_shaped() {
        // Scanned over *values*, not prose: the descriptions deliberately
        // talk about headers and secrets, and a substring match on the whole
        // document would flag its own documentation.
        let mut entries = describe(&facts());
        for e in &mut entries {
            e.description.clear();
            e.reason = None;
        }
        let json = serde_json::to_string(&entries).unwrap();
        for banned in ["apiKey", "api_key", "token", "Authorization", "coda-secret:", "headers", "env"] {
            assert!(!json.contains(banned), "SECURITY: `{banned}` must never appear: {json}");
        }
    }

    #[test]
    fn the_mcp_entry_reports_a_count_not_the_servers_themselves() {
        let entries = describe(&facts());
        let value = entry(&entries, "mcpServers").value.clone().unwrap();
        assert_eq!(value["serverCount"], 2);
        assert_eq!(value["enabled"], true);
        assert!(value.get("servers").is_none(), "mcp/list owns the inventory");
    }

    #[test]
    fn the_mcp_entry_distinguishes_disabled_from_unknown() {
        let mut f = facts();
        f.mcp_enabled = false;
        f.mcp_server_count = None;
        let value = entry(&describe(&f), "mcpServers").value.clone().unwrap();
        assert_eq!(value["enabled"], false);
        assert!(value["serverCount"].is_null(), "unknown must not become a confident zero");
    }
}
