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
//! | enabled | no client | `enabled` + `notConnected` (failed, or added after startup); `lastError` when the manager recorded a fault |
//! | disabled / shadowed | — | `disabled`/`shadowed` + `notAttempted` |
//! | — | has a client | still `connected` (deleted from the file after startup) |
//!
//! `lastError` is only ever set on an enabled, unshadowed, not-connected
//! server, and is run through [`sanitise_error`] first: URLs in it lose
//! userinfo/query/fragment, control characters are flattened and the length is
//! capped.
//!
//! With MCP switched off for the process there is no manager at all, so every
//! runtime status is `unknown` and `toolCount` is omitted — never a
//! confident `0`.

use std::collections::{HashMap, HashSet};
use std::path::Path;

use coda_mcp::config::{McpRawServer, McpScope};
use coda_mcp::{McpServerFault, McpServerStatus};
use coda_proto::mcp::{
    McpConfiguredState, McpListResult, McpRuntimeStatus, McpSecretRefDto, McpServerDto,
    sanitise_url,
};

/// Prefix that marks a value as a reference into the credential store rather
/// than a literal (`coda-tui/src/config.rs`).
const SECRET_PREFIX: &str = "coda-secret:";

/// Longest `lastError` published, in characters, before the ellipsis.
const MAX_ERROR_CHARS: usize = 200;

/// Builds the wire inventory from the file layer plus whatever the running
/// manager knows.
///
/// `runtime` is `None` when there is no manager (MCP disabled for this
/// process, or no server ever connected). `faults` are the manager's recorded
/// connection failures.
pub fn build(
    configured: &[McpRawServer],
    runtime: Option<&[McpServerStatus]>,
    faults: &[McpServerFault],
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

            let last_error = (runtime_status == McpRuntimeStatus::NotConnected)
                .then(|| faults.iter().find(|f| f.name == raw.name))
                .flatten()
                .filter(|f| f.last_error != "not connected")
                .map(|f| sanitise_error(&redact_configured(raw, &f.last_error)));

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
                last_error,
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
            last_error: None,
        });
    }

    servers.sort_by(|a, b| (&a.scope, &a.name).cmp(&(&b.scope, &b.name)));
    McpListResult { servers, enabled: mcp_enabled, manager_available: runtime.is_some() }
}

/// Makes a connection error safe to publish: URLs are reduced to scheme + host
/// + path, control characters become spaces, whitespace is collapsed and the
/// length capped.
///
/// Also masks the token after `Bearer`/`Basic` and the value of
/// credential-named `k=v` / `k:v` tokens, and drops the query of schemeless
/// URL-like tokens.
fn sanitise_error(raw: &str) -> String {
    const LEADING: &[char] = &['(', '\'', '"', '<', '['];
    const TRAILING: &[char] = &[',', '.', ';', ')', '\'', '"', '>', ']'];
    const SENSITIVE_KEYS: &[&str] =
        &["token", "key", "secret", "pass", "auth", "sig", "credential"];

    let flat: String = raw.chars().map(|c| if c.is_control() { ' ' } else { c }).collect();
    let mut mask_next = false;
    let cleaned = flat
        .split_whitespace()
        .map(|token| {
            let rest = token.trim_start_matches(LEADING);
            let core = rest.trim_end_matches(TRAILING);
            let head = &token[..token.len() - rest.len()];
            let tail = &rest[core.len()..];

            let body = if mask_next && !core.is_empty() {
                mask_next = false;
                "***".to_string()
            } else {
                mask_next = core.eq_ignore_ascii_case("bearer") || core.eq_ignore_ascii_case("basic");
                if core.contains("://") {
                    sanitise_url(core)
                } else if let Some(sep) = core.find(['=', ':']).filter(|&i| i + 1 < core.len()) {
                    let key = core[..sep].trim_start_matches(['-', '?', '&']).to_ascii_lowercase();
                    if SENSITIVE_KEYS.iter().any(|k| key.contains(k)) {
                        format!("{}***", &core[..=sep])
                    } else {
                        drop_query(core)
                    }
                } else {
                    drop_query(core)
                }
            };
            format!("{head}{body}{tail}")
        })
        .collect::<Vec<_>>()
        .join(" ");
    match cleaned.char_indices().nth(MAX_ERROR_CHARS) {
        Some((end, _)) => format!("{}…", &cleaned[..end]),
        None => cleaned,
    }
}

fn drop_query(token: &str) -> String {
    token.split('?').next().unwrap_or_default().to_string()
}

/// Replaces values known from the configuration (URL, command, env values)
/// with their safe forms, longest first so overlapping values cannot leave
/// a partial remainder.
fn redact_configured(raw: &McpRawServer, message: &str) -> String {
    let mut subs: Vec<(String, String)> = Vec::new();
    if let Some(url) = raw.url.as_deref().filter(|u| !u.is_empty()) {
        subs.push((url.to_string(), sanitise_url(url)));
    }
    if let Some(cmd) = raw.command.as_deref().filter(|c| !c.trim().is_empty()) {
        subs.push((cmd.to_string(), command_label(cmd)));
    }
    for value in raw.env.values().filter(|v| v.chars().count() >= 4) {
        subs.push((value.clone(), "<redacted>".to_string()));
    }
    subs.sort_by(|a, b| b.0.len().cmp(&a.0.len()));
    subs.iter().fold(message.to_string(), |acc, (from, to)| acc.replace(from.as_str(), to))
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

        let result = build(&[server], None, &[], true);
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
        let result = build(&[raw("fs", McpScope::Project)], None, &[], true);
        let json = serde_json::to_string(&result).unwrap();
        assert!(!json.contains("SUPER-SECRET"), "SECURITY: arguments must not be echoed: {json}");
        assert_eq!(find(&result, "fs").target_display, "mcp-server-filesystem");
    }

    #[test]
    fn a_url_is_reduced_to_scheme_host_and_path() {
        let result = build(
            &[http("remote", "https://bot:hunter2@mcp.example.com/sse?apiKey=LEAKED#frag")],
            None,
            &[],
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
        let result = build(&[server], None, &[], true);
        let json = serde_json::to_string(&result).unwrap();
        assert!(!json.contains("tok_live_DO_NOT_LEAK"), "SECURITY: {json}");
        assert!(!json.contains("hello"), "SECURITY: even a benign-looking env value is not ours to publish: {json}");
        assert_eq!(find(&result, "fs").env_var_names, vec!["API_TOKEN", "PLAIN"]);
    }

    #[test]
    fn a_secret_reference_reports_its_variable_name_never_its_storage_target() {
        let mut server = raw("fs", McpScope::User);
        server.env.insert("API_TOKEN".into(), "coda-secret:mcp/fs/token".into());
        let result = build(&[server], None, &[], true);
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
        let result = build(&[server], None, &[], true);
        assert_eq!(find(&result, "fs").target_display, "mcp-fs");
    }

    // ── Configured vs runtime ────────────────────────────────────────────

    #[test]
    fn an_enabled_server_the_manager_never_started_is_not_connected_not_unknown() {
        let result = build(&[raw("fs", McpScope::User)], Some(&[]), &[], true);
        let dto = find(&result, "fs");
        assert_eq!(dto.configured, McpConfiguredState::Enabled);
        assert_eq!(dto.runtime_status, McpRuntimeStatus::NotConnected);
        assert!(dto.tool_count.is_none(), "an unknown tool count must be absent, not zero");
    }

    #[test]
    fn a_connected_server_reports_its_real_tool_count() {
        let runtime = vec![McpServerStatus { name: "fs".into(), tool_count: 5 }];
        let result = build(&[raw("fs", McpScope::User)], Some(&runtime), &[], true);
        let dto = find(&result, "fs");
        assert_eq!(dto.runtime_status, McpRuntimeStatus::Connected);
        assert_eq!(dto.tool_count, Some(5));
    }

    #[test]
    fn with_no_manager_every_runtime_status_is_unknown() {
        let result = build(&[raw("fs", McpScope::User)], None, &[], false);
        assert_eq!(find(&result, "fs").runtime_status, McpRuntimeStatus::Unknown);
        assert!(find(&result, "fs").tool_count.is_none());
        assert!(!result.manager_available);
        assert!(!result.enabled, "an empty list must not be mistaken for 'nothing configured'");
    }

    #[test]
    fn a_disabled_server_is_listed_as_disabled_and_never_attempted() {
        let mut server = raw("fs", McpScope::User);
        server.disabled = true;
        let result = build(&[server], Some(&[]), &[], true);
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
            &[],
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
        let result = build(&[], Some(&runtime), &[], true);
        let dto = find(&result, "ghost");
        assert_eq!(dto.runtime_status, McpRuntimeStatus::Connected);
        assert_eq!(dto.tool_count, Some(2));
        assert_eq!(dto.target_display, "<no longer in configuration>");
    }

    #[test]
    fn no_configured_servers_and_no_manager_is_an_honest_empty_list() {
        let result = build(&[], None, &[], true);
        assert!(result.servers.is_empty());
        assert!(result.enabled);
        assert!(!result.manager_available);
    }

    fn fault(name: &str, error: &str) -> McpServerFault {
        McpServerFault { name: name.into(), last_error: error.into() }
    }

    #[test]
    fn a_disconnected_server_reports_its_sanitised_last_error() {
        let faults = [fault(
            "fs",
            "spawning failed\nsee https://u:p@h.example/x?token=SECRET, retry",
        )];
        let result = build(&[raw("fs", McpScope::User)], Some(&[]), &faults, true);
        let error = find(&result, "fs").last_error.clone().expect("last error");
        assert!(!error.contains("SECRET"), "SECURITY: {error}");
        assert!(!error.contains("u:p"), "SECURITY: {error}");
        assert!(!error.contains('\n'), "{error:?}");
        assert!(error.contains("https://h.example/x"), "{error}");
        assert!(error.contains("retry"), "{error}");
    }

    #[test]
    fn a_long_last_error_is_capped() {
        let faults = [fault("fs", &"é".repeat(500))];
        let result = build(&[raw("fs", McpScope::User)], Some(&[]), &faults, true);
        let error = find(&result, "fs").last_error.clone().expect("last error");
        assert!(error.chars().count() <= 201, "{}", error.chars().count());
        assert!(error.ends_with('…'));
    }

    #[test]
    fn a_connected_server_has_no_last_error() {
        let runtime = vec![McpServerStatus { name: "fs".into(), tool_count: 1 }];
        let faults = [fault("fs", "stale failure"), fault("other", "boom")];
        let result = build(&[raw("fs", McpScope::User)], Some(&runtime), &faults, true);
        assert!(find(&result, "fs").last_error.is_none());
        assert!(result.servers.iter().all(|s| s.last_error.is_none()));
    }

    #[test]
    fn a_disabled_or_shadowed_server_never_shows_a_last_error() {
        let mut off = raw("off", McpScope::User);
        off.disabled = true;
        let faults = [fault("off", "boom"), fault("fs", "boom")];
        let result = build(
            &[off, raw("fs", McpScope::User), raw("fs", McpScope::Project)],
            Some(&[]),
            &faults,
            true,
        );
        let user_fs = result.servers.iter().find(|s| s.scope == "user" && s.name == "fs").unwrap();
        assert!(find(&result, "off").last_error.is_none());
        assert!(user_fs.last_error.is_none());
    }

    fn error_for(server: McpRawServer, message: &str) -> String {
        let faults = [fault(&server.name, message)];
        let result = build(&[server], Some(&[]), &faults, true);
        result.servers[0].last_error.clone().expect("last error")
    }

    #[test]
    fn a_schemeless_configured_url_is_redacted_from_the_error() {
        let url = "mcp.example.com/mcp?api_key=sk-live-123";
        let error = error_for(
            http("remote", url),
            &format!("invalid MCP server URL: {url}"),
        );
        assert!(!error.contains("sk-live-123"), "SECURITY: {error}");
    }

    #[test]
    fn a_configured_command_path_is_reduced_to_its_file_name_in_the_error() {
        let cmd = r"C:\Users\alice\bin\mcp.exe";
        let mut server = raw("fs", McpScope::User);
        server.command = Some(cmd.into());
        let error = error_for(server, &format!("spawning MCP server '{cmd}' failed: not found"));
        assert!(!error.contains("alice"), "SECURITY: {error}");
        assert!(error.contains("mcp.exe"), "{error}");
    }

    #[test]
    fn a_configured_env_value_echoed_in_the_error_is_redacted() {
        let mut server = raw("fs", McpScope::User);
        server.env.insert("API_TOKEN".into(), "tok_live_ABCD".into());
        let error = error_for(server, "failed with API_TOKEN value tok_live_ABCD rejected");
        assert!(!error.contains("tok_live_ABCD"), "SECURITY: {error}");
    }

    #[test]
    fn bearer_and_basic_credentials_are_masked() {
        let error = error_for(raw("fs", McpScope::User), "Authorization: Bearer abc.def.ghi");
        assert!(!error.contains("abc.def.ghi"), "SECURITY: {error}");
        let error = error_for(raw("fs", McpScope::User), "sent basic dXNlcjpwdw== denied");
        assert!(!error.contains("dXNlcjpwdw"), "SECURITY: {error}");
    }

    #[test]
    fn a_credential_shaped_key_value_token_is_masked() {
        let error = error_for(raw("fs", McpScope::User), "bad arg --token=XYZ123 given");
        assert!(!error.contains("XYZ123"), "SECURITY: {error}");
        let error = error_for(raw("fs", McpScope::User), "password:hunter22 rejected");
        assert!(!error.contains("hunter22"), "SECURITY: {error}");
    }

    #[test]
    fn a_parenthesised_url_keeps_its_path_but_loses_its_query() {
        let error = error_for(
            raw("fs", McpScope::User),
            "error sending request for url (https://h.example/x?k=S)",
        );
        assert!(error.contains("https://h.example/x"), "{error}");
        assert!(!error.contains("k=S"), "SECURITY: {error}");
        let error = error_for(raw("fs", McpScope::User), "bad host.example/x?zzz=1 here");
        assert!(!error.contains("zzz"), "SECURITY: {error}");
    }

    #[test]
    fn a_fault_that_only_says_not_connected_carries_no_last_error() {
        let faults = [fault("fs", "not connected")];
        let result = build(&[raw("fs", McpScope::User)], Some(&[]), &faults, true);
        assert!(find(&result, "fs").last_error.is_none());
    }
}
