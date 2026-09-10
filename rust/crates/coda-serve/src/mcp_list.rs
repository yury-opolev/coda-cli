//! `mcp/list` — a read-only, secret-free inventory of MCP servers.
//!
//! # What this deliberately does not do
//!
//! - It never reads a credential store. Reporting a status must not probe an
//!   auth store, wake a keyring prompt or attempt a connection. Resolution
//!   status for `coda-secret:` references is therefore *omitted* (unknown),
//!   never reported as `false`, which would be a claim rather than a
//!   non-answer.
//! - It never echoes an `env` value, a header value, a bearer token or a
//!   `coda-secret:` target. Only *names*.
//! - It never echoes a raw URL: `targetDisplay` is scheme + host + path, with
//!   userinfo, query and fragment removed (all of which routinely carry
//!   tokens).
//!
//! # Configured vs runtime
//!
//! `.mcp.json` can be edited while the manager keeps running the servers it
//! connected at startup, so the two are reported separately:
//!
//! | file says | manager says | reported as |
//! |---|---|---|
//! | enabled | has a client | `enabled` + `connected` |
//! | enabled | no client | `enabled` + `notConnected` (failed, or added after startup) |
//! | disabled / shadowed | — | `disabled`/`shadowed` + `notAttempted` |
//! | — | has a client | still `connected` (deleted from the file after startup) |
//!
//! With MCP switched off for the process there is no manager at all, so every
//! runtime status is `unknown` and `toolCount` is omitted — never a
//! confident `0`.

use std::collections::{HashMap, HashSet};
use std::path::Path;

use coda_mcp::McpServerStatus;
use coda_mcp::config::{McpRawServer, McpScope};
use coda_proto::mcp::{
    McpConfiguredState, McpListResult, McpRuntimeStatus, McpSecretRefDto, McpServerDto,
    sanitise_url,
};

/// Prefix that marks a value as a reference into the credential store rather
/// than a literal (`coda-tui/src/config.rs`).
const SECRET_PREFIX: &str = "coda-secret:";

/// Builds the wire inventory from the file layer plus whatever the running
/// manager knows.
///
/// `runtime` is `None` when there is no manager (MCP disabled for this
/// process, or no server ever connected).
pub fn build(
    configured: &[McpRawServer],
    runtime: Option<&[McpServerStatus]>,
    mcp_enabled: bool,
) -> McpListResult {
    // Project entries shadow user entries of the same name (`load_all` sorts
    // user first, then project). A user entry whose name also appears at
    // project scope is not the one in use, and saying so is more useful than
    // listing two servers that look interchangeable.
    let project_names: HashSet<&str> = configured
        .iter()
        .filter(|s| s.scope == McpScope::Project)
        .map(|s| s.name.as_str())
        .collect();

    let runtime_by_name: HashMap<&str, &McpServerStatus> = runtime
        .unwrap_or(&[])
        .iter()
        .map(|s| (s.name.as_str(), s))
        .collect();

    let mut servers: Vec<McpServerDto> = configured
        .iter()
        .map(|raw| {
            let shadowed = raw.scope == McpScope::User && project_names.contains(raw.name.as_str());
            let configured_state = if shadowed {
                McpConfiguredState::Shadowed
            } else if raw.disabled {
                McpConfiguredState::Disabled
            } else {
                McpConfiguredState::Enabled
            };

            // A shadowed user entry is not the one the manager connected, so
            // it must never claim the project entry's runtime state.
            let live = (!shadowed).then(|| runtime_by_name.get(raw.name.as_str())).flatten();

            let runtime_status = match (runtime, live, configured_state) {
                (None, _, _) => McpRuntimeStatus::Unknown,
                (Some(_), Some(_), _) => McpRuntimeStatus::Connected,
                (Some(_), None, McpConfiguredState::Enabled) => McpRuntimeStatus::NotConnected,
                (Some(_), None, _) => McpRuntimeStatus::NotAttempted,
            };

            McpServerDto {
                name: raw.name.clone(),
                scope: raw.scope.label().to_string(),
                transport: raw.transport().to_string(),
                target_kind: if raw.command.is_some() { "command".into() } else { "url".into() },
                target_display: sanitised_target(raw),
                configured: configured_state,
                runtime_status,
                tool_count: live.map(|s| s.tool_count as i64),
                env_var_names: sorted_names(raw.env.keys()),
                secret_refs: secret_refs(raw),
            }
        })
        .collect();

    // A server the manager holds a client for but the file no longer declares
    // is still running. Omitting it would report a fiction.
    let configured_names: HashSet<&str> = configured.iter().map(|s| s.name.as_str()).collect();
    for status in runtime.unwrap_or(&[]) {
        if configured_names.contains(status.name.as_str()) {
            continue;
        }
        servers.push(McpServerDto {
            name: status.name.clone(),
            scope: "user".into(),
            transport: "stdio".into(),
            target_kind: "command".into(),
            // Nothing is known about how it is reached any more, and echoing
            // a stale guess would be worse than saying so.
            target_display: "<no longer in configuration>".into(),
            configured: McpConfiguredState::Disabled,
            runtime_status: McpRuntimeStatus::Connected,
            tool_count: Some(status.tool_count as i64),
            env_var_names: Vec::new(),
            secret_refs: Vec::new(),
        });
    }

    servers.sort_by(|a, b| (&a.scope, &a.name).cmp(&(&b.scope, &b.name)));
    McpListResult { servers, enabled: mcp_enabled, manager_available: runtime.is_some() }
}

/// A display label that cannot leak a credential.
///
/// stdio: the bare command name (never its arguments — a `--token=` argument
/// is exactly the shape of thing that must not be echoed). http: the URL
/// reduced to scheme + host + path.
fn sanitised_target(raw: &McpRawServer) -> String {
    match (&raw.command, &raw.url) {
        (Some(command), _) => command_label(command),
        (None, Some(url)) => sanitise_url(url),
        (None, None) => "<none>".into(),
    }
}

/// The final path segment of a command, so a home directory or an embedded
/// path does not travel with the label.
fn command_label(command: &str) -> String {
    let trimmed = command.trim();
    if trimmed.is_empty() {
        return "<none>".into();
    }
    Path::new(trimmed)
        .file_name()
        .map(|f| f.to_string_lossy().into_owned())
        .unwrap_or_else(|| trimmed.to_string())
}

fn sorted_names<'a>(names: impl Iterator<Item = &'a String>) -> Vec<String> {
    let mut out: Vec<String> = names.cloned().collect();
    out.sort_unstable();
    out
}

/// Names of env vars whose value is a `coda-secret:` reference.
///
/// `resolved` is **omitted** — the tri-state's "unknown". Determining it would
/// mean reading the credential store, which is exactly the probe this surface
/// must not perform, and reporting `false` would assert as fact that the
/// reference does not resolve. When the engine later carries a startup fact
/// about resolution, that fact (and only that) may fill this in.
fn secret_refs(raw: &McpRawServer) -> Vec<McpSecretRefDto> {
    let mut refs: Vec<McpSecretRefDto> = raw
        .env
        .iter()
        .filter(|(_, value)| value.starts_with(SECRET_PREFIX))
        .map(|(name, _)| McpSecretRefDto { name: name.clone(), resolved: None })
        .collect();
    refs.sort_by(|a, b| a.name.cmp(&b.name));
    refs
}

#[cfg(test)]
mod tests {
    use super::*;

    fn raw(name: &str, scope: McpScope) -> McpRawServer {
        McpRawServer {
            name: name.into(),
            scope,
            command: Some("mcp-server-filesystem".into()),
            args: vec!["--token=SUPER-SECRET".into()],
            env: HashMap::new(),
            url: None,
            disabled: false,
        }
    }

    fn http(name: &str, url: &str) -> McpRawServer {
        McpRawServer {
            name: name.into(),
            scope: McpScope::User,
            command: None,
            args: Vec::new(),
            env: HashMap::new(),
            url: Some(url.into()),
            disabled: false,
        }
    }

    fn find<'a>(r: &'a McpListResult, name: &str) -> &'a McpServerDto {
        r.servers.iter().find(|s| s.name == name).unwrap_or_else(|| panic!("no `{name}`"))
    }

    // ── Secrets ──────────────────────────────────────────────────────────

    #[test]
    fn a_secret_reference_reports_its_name_and_never_its_value_or_target() {
        let mut env = HashMap::new();
        env.insert("API_TOKEN".to_string(), "coda-secret:prod/mcp/token".to_string());
        env.insert("PLAIN".to_string(), "not-a-secret".to_string());
        let mut server = raw("fs", McpScope::Project);
        server.env = env;

        let result = build(&[server], None, true);
        let dto = find(&result, "fs");
        assert_eq!(dto.secret_refs.len(), 1);
        assert_eq!(dto.secret_refs[0].name, "API_TOKEN");
        assert_eq!(
            dto.secret_refs[0].resolved, None,
            "resolution is genuinely unknown: this call never reads the credential store, and \
             `false` would state as fact that the reference does not resolve"
        );
        let v = serde_json::to_value(&result).unwrap();
        assert!(
            v["servers"][0]["secretRefs"][0].get("resolved").is_none(),
            "unknown must be omitted, never rendered as a confident boolean: {v}"
        );
        let json = serde_json::to_string(&result).unwrap();
        assert!(!json.contains("prod/mcp/token"), "SECURITY: the storage target is a locator");
        assert!(!json.contains("not-a-secret"), "SECURITY: env values are never carried");
    }

    #[test]
    fn a_command_argument_carrying_a_token_is_never_echoed() {
        let result = build(&[raw("fs", McpScope::Project)], None, true);
        let json = serde_json::to_string(&result).unwrap();
        assert!(!json.contains("SUPER-SECRET"), "SECURITY: arguments must not be echoed: {json}");
        assert_eq!(find(&result, "fs").target_display, "mcp-server-filesystem");
    }

    #[test]
    fn a_url_is_reduced_to_scheme_host_and_path() {
        let result = build(
            &[http("remote", "https://bot:hunter2@mcp.example.com/sse?apiKey=LEAKED#frag")],
            None,
            true,
        );
        let json = serde_json::to_string(&result).unwrap();
        assert!(!json.contains("hunter2"), "SECURITY: userinfo must be dropped: {json}");
        assert!(!json.contains("LEAKED"), "SECURITY: the query string must be dropped: {json}");
        assert_eq!(find(&result, "remote").target_display, "https://mcp.example.com/sse");
    }

    #[test]
    fn env_values_are_never_carried_only_their_names() {
        let mut server = raw("fs", McpScope::User);
        server.env.insert("API_TOKEN".into(), "tok_live_DO_NOT_LEAK".into());
        server.env.insert("PLAIN".into(), "hello".into());
        let result = build(&[server], None, true);
        let json = serde_json::to_string(&result).unwrap();
        assert!(!json.contains("tok_live_DO_NOT_LEAK"), "SECURITY: {json}");
        assert!(!json.contains("hello"), "SECURITY: even a benign-looking env value is not ours to publish: {json}");
        assert_eq!(find(&result, "fs").env_var_names, vec!["API_TOKEN", "PLAIN"]);
    }

    #[test]
    fn a_secret_reference_reports_its_variable_name_never_its_storage_target() {
        let mut server = raw("fs", McpScope::User);
        server.env.insert("API_TOKEN".into(), "coda-secret:mcp/fs/token".into());
        let result = build(&[server], None, true);
        let json = serde_json::to_string(&result).unwrap();
        assert!(!json.contains("mcp/fs/token"), "SECURITY: the keyring locator is not a label: {json}");
        assert_eq!(
            find(&result, "fs").secret_refs,
            vec![McpSecretRefDto { name: "API_TOKEN".into(), resolved: None }]
        );
    }

    #[test]
    fn a_command_with_a_home_directory_path_reports_only_its_file_name() {
        let mut server = raw("fs", McpScope::User);
        server.command = Some("/home/alice/.local/bin/mcp-fs".into());
        let result = build(&[server], None, true);
        assert_eq!(find(&result, "fs").target_display, "mcp-fs");
    }

    // ── Configured vs runtime ────────────────────────────────────────────

    #[test]
    fn an_enabled_server_the_manager_never_started_is_not_connected_not_unknown() {
        let result = build(&[raw("fs", McpScope::User)], Some(&[]), true);
        let dto = find(&result, "fs");
        assert_eq!(dto.configured, McpConfiguredState::Enabled);
        assert_eq!(dto.runtime_status, McpRuntimeStatus::NotConnected);
        assert!(dto.tool_count.is_none(), "an unknown tool count must be absent, not zero");
    }

    #[test]
    fn a_connected_server_reports_its_real_tool_count() {
        let runtime = vec![McpServerStatus { name: "fs".into(), tool_count: 5 }];
        let result = build(&[raw("fs", McpScope::User)], Some(&runtime), true);
        let dto = find(&result, "fs");
        assert_eq!(dto.runtime_status, McpRuntimeStatus::Connected);
        assert_eq!(dto.tool_count, Some(5));
    }

    #[test]
    fn with_no_manager_every_runtime_status_is_unknown() {
        let result = build(&[raw("fs", McpScope::User)], None, false);
        assert_eq!(find(&result, "fs").runtime_status, McpRuntimeStatus::Unknown);
        assert!(find(&result, "fs").tool_count.is_none());
        assert!(!result.manager_available);
        assert!(!result.enabled, "an empty list must not be mistaken for 'nothing configured'");
    }

    #[test]
    fn a_disabled_server_is_listed_as_disabled_and_never_attempted() {
        let mut server = raw("fs", McpScope::User);
        server.disabled = true;
        let result = build(&[server], Some(&[]), true);
        let dto = find(&result, "fs");
        assert_eq!(dto.configured, McpConfiguredState::Disabled);
        assert_eq!(dto.runtime_status, McpRuntimeStatus::NotAttempted);
    }

    #[test]
    fn a_user_entry_shadowed_by_a_project_entry_says_so_and_claims_no_runtime_state() {
        let runtime = vec![McpServerStatus { name: "fs".into(), tool_count: 3 }];
        let result = build(
            &[raw("fs", McpScope::User), raw("fs", McpScope::Project)],
            Some(&runtime),
            true,
        );
        let user = result
            .servers
            .iter()
            .find(|s| s.scope == "user" && s.name == "fs")
            .expect("user entry");
        let project = result
            .servers
            .iter()
            .find(|s| s.scope == "project" && s.name == "fs")
            .expect("project entry");
        assert_eq!(user.configured, McpConfiguredState::Shadowed);
        assert_eq!(
            user.runtime_status,
            McpRuntimeStatus::NotAttempted,
            "the shadowed entry must not claim the connection the project entry owns"
        );
        assert!(user.tool_count.is_none());
        assert_eq!(project.runtime_status, McpRuntimeStatus::Connected);
        assert_eq!(project.tool_count, Some(3));
    }

    #[test]
    fn a_server_deleted_from_the_file_after_startup_is_still_reported_as_running() {
        let runtime = vec![McpServerStatus { name: "ghost".into(), tool_count: 2 }];
        let result = build(&[], Some(&runtime), true);
        let dto = find(&result, "ghost");
        assert_eq!(dto.runtime_status, McpRuntimeStatus::Connected);
        assert_eq!(dto.tool_count, Some(2));
        assert_eq!(dto.target_display, "<no longer in configuration>");
    }

    #[test]
    fn no_configured_servers_and_no_manager_is_an_honest_empty_list() {
        let result = build(&[], None, true);
        assert!(result.servers.is_empty());
        assert!(result.enabled);
        assert!(!result.manager_available);
    }
}
