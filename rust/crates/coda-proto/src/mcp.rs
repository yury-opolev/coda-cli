//! `mcp/list` DTOs — read-only, secret-free by construction.
//!
//! `.mcp.json` carries `env` values, custom headers and `coda-secret:`
//! references. **None of them appear here.** This module exposes:
//!
//! - the server's *name* and scope,
//! - the transport and a sanitised target label (a bare command name, or a
//!   URL reduced to scheme + host + path — never userinfo, query or
//!   fragment, all of which routinely carry tokens),
//! - the *names* of the environment variables a server declares, never their
//!   values,
//! - the *names* of secret references and whether they resolved when the
//!   engine already knows, never the secret's storage target and never the
//!   secret. "Not determined by this call" is an *omitted* `resolved`, not a
//!   `false`.
//! - the configured-vs-runtime distinction, because a file can be edited
//!   while the manager keeps running the servers it connected at startup.

use serde::{Deserialize, Serialize};

/// What the file says about a server.
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum McpConfiguredState {
    /// Present and not disabled.
    Enabled,
    /// Present with `"disabled": true`.
    Disabled,
    /// Present in the user file but overridden by a project entry of the
    /// same name. The project entry is the one that is used.
    Shadowed,
}

/// What the *running manager* knows about a server.
///
/// Deliberately separate from [`McpConfiguredState`]: an enabled entry that
/// was added to the file after startup is `Enabled` + `NotStarted`, and a
/// server that was connected at startup and then deleted from the file is
/// still `Connected`. Collapsing the two would report a fiction.
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub enum McpRuntimeStatus {
    /// The manager holds a live client for this server.
    Connected,
    /// The server is configured and enabled, but this engine process has no
    /// client for it — it failed to start, or the file changed after startup
    /// and the manager needs a restart to pick it up.
    NotConnected,
    /// The engine never attempted a connection (MCP disabled for this
    /// process, or the entry is disabled/shadowed).
    NotAttempted,
    /// There is no manager to ask. Never reported as a confident zero.
    Unknown,
}

/// A named reference to a stored secret, and whether it resolved.
///
/// `name` is the *variable or header name* the reference is bound to. The
/// storage target (`coda-secret:<target>`) is deliberately absent: it names a
/// keyring entry, which is a credential locator, not a label.
///
/// `resolved` is a **tri-state**. `None` (the field omitted) means "this call
/// did not determine it", which is the honest answer for any read that must
/// not touch the credential store — and is emphatically not the same claim as
/// `false` ("this reference does not resolve"). Set it only from a fact the
/// engine already holds, never by probing during a read.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct McpSecretRefDto {
    pub name: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub resolved: Option<bool>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct McpServerDto {
    pub name: String,
    /// `"user" | "project"`.
    pub scope: String,
    /// `"stdio" | "http"`.
    pub transport: String,
    /// `"command" | "url"`.
    pub target_kind: String,
    /// Sanitised: a bare command name, or scheme + host + path. Never
    /// userinfo, query string or fragment.
    pub target_display: String,
    pub configured: McpConfiguredState,
    pub runtime_status: McpRuntimeStatus,
    /// Tools the running client advertises. `None` means "not known" (no
    /// manager, or the server is not connected) — never a fake `0`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tool_count: Option<i64>,
    /// Names only. Never values.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub env_var_names: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub secret_refs: Vec<McpSecretRefDto>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
#[cfg_attr(feature = "schema", derive(schemars::JsonSchema))]
pub struct McpListResult {
    pub servers: Vec<McpServerDto>,
    /// `false` when MCP is switched off for this engine process, so an empty
    /// list is not mistaken for "no servers are configured".
    pub enabled: bool,
    /// `true` when a manager exists to ask about runtime state.
    pub manager_available: bool,
}

/// Reduces a URL to `scheme://host[:port]/path`, dropping userinfo, query and
/// fragment — every part of a URL that routinely carries a credential.
///
/// A string that does not parse as a URL is reported as `"<unparsable url>"`
/// rather than echoed: echoing it would defeat the whole point.
pub fn sanitise_url(raw: &str) -> String {
    // Deliberately hand-rolled: `coda-proto` is a dependency-light DTO crate
    // and must not grow a URL parser for one label.
    let (scheme, rest) = match raw.split_once("://") {
        Some((s, r)) if !s.is_empty() && s.chars().all(|c| c.is_ascii_alphanumeric() || c == '+' || c == '-' || c == '.') => (s, r),
        _ => return "<unparsable url>".to_string(),
    };
    // Everything from the first '?' or '#' is dropped.
    let rest = rest.split(['?', '#']).next().unwrap_or("");
    let (authority, path) = match rest.split_once('/') {
        Some((a, p)) => (a, format!("/{p}")),
        None => (rest, String::new()),
    };
    // Drop userinfo (`user:pass@host`).
    let host = authority.rsplit_once('@').map(|(_, h)| h).unwrap_or(authority);
    if host.is_empty() {
        return "<unparsable url>".to_string();
    }
    format!("{scheme}://{host}{path}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_url_loses_its_query_fragment_and_userinfo() {
        assert_eq!(
            sanitise_url("https://user:sup3rsecret@mcp.example.com:8443/sse?token=abc#frag"),
            "https://mcp.example.com:8443/sse"
        );
    }

    #[test]
    fn a_url_without_a_path_keeps_only_scheme_and_host() {
        assert_eq!(sanitise_url("http://127.0.0.1:9000"), "http://127.0.0.1:9000");
    }

    #[test]
    fn a_bare_token_is_never_echoed_back_as_a_label() {
        // Anything that is not a URL must not be reflected verbatim: a
        // malformed entry could be a pasted credential.
        assert_eq!(sanitise_url("sk-live-not-a-url"), "<unparsable url>");
        assert_eq!(sanitise_url("https://"), "<unparsable url>");
    }

    #[test]
    fn the_dto_never_grows_a_value_carrying_field() {
        // Conventions guard: the serialised field names of McpServerDto are
        // an allow-list. A new field that could carry a secret has to be
        // added here consciously.
        let dto = McpServerDto {
            name: "fs".into(),
            scope: "project".into(),
            transport: "stdio".into(),
            target_kind: "command".into(),
            target_display: "mcp-server-filesystem".into(),
            configured: McpConfiguredState::Enabled,
            runtime_status: McpRuntimeStatus::Connected,
            tool_count: Some(4),
            env_var_names: vec!["API_TOKEN".into()],
            secret_refs: vec![McpSecretRefDto { name: "API_TOKEN".into(), resolved: Some(true) }],
        };
        let v = serde_json::to_value(&dto).unwrap();
        let mut keys: Vec<&str> = v.as_object().unwrap().keys().map(|k| k.as_str()).collect();
        keys.sort_unstable();
        assert_eq!(
            keys,
            vec![
                "configured",
                "envVarNames",
                "name",
                "runtimeStatus",
                "scope",
                "secretRefs",
                "targetDisplay",
                "targetKind",
                "toolCount",
                "transport",
            ]
        );
        for banned in ["env", "headers", "url", "command", "args", "token", "apiKey", "secret"] {
            assert!(v.get(banned).is_none(), "{banned} must never appear on an MCP DTO");
        }
    }

    #[test]
    fn an_unknown_tool_count_is_omitted_rather_than_reported_as_zero() {
        let dto = McpServerDto {
            name: "fs".into(),
            scope: "user".into(),
            transport: "http".into(),
            target_kind: "url".into(),
            target_display: "https://x.example/mcp".into(),
            configured: McpConfiguredState::Disabled,
            runtime_status: McpRuntimeStatus::NotAttempted,
            tool_count: None,
            env_var_names: Vec::new(),
            secret_refs: Vec::new(),
        };
        let v = serde_json::to_value(&dto).unwrap();
        assert!(v.get("toolCount").is_none());
        assert_eq!(v["runtimeStatus"], "notAttempted");
    }
}
