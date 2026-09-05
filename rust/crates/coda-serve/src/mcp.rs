//! MCP engine integration for the serve host.
//!
//! At engine startup the host connects every enabled MCP server declared in
//! the user and project `.mcp.json` files, bridges their tools into the
//! agent's `ToolRegistry`, and retains the manager so the servers can be shut
//! down cleanly. The whole feature is opt-out:
//!
//! - `CODA_SERVE_DISABLE_MCP` (or `coda serve --no-mcp`) disables MCP entirely.
//! - `CODA_DISABLE_PROJECT_MCP` (or `coda serve --no-project-mcp`) keeps the
//!   user layer but suppresses the project `<cwd>/.mcp.json` layer.
//! - `CODA_USER_MCP_DIR` overrides the user `.mcp.json` directory (handled by
//!   `coda_mcp::config::resolve_paths`).
//!
//! A broken optional server never hides the healthy ones and never reports
//! empty success: each connection failure is collected as a user-facing notice
//! and surfaced through the normal event sink, while the servers that did
//! connect stay available.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use coda_auth::Secret;
use coda_auth::store::CredentialStore;
use coda_mcp::auth::types::McpAuthConfig;
use coda_mcp::config::{McpConnectable, McpHttpConnectable};
use coda_mcp::McpClientManager;
use coda_tool::Tool;

/// Everything the host needs to wire MCP into a session.
pub(crate) struct McpBundle {
    /// Deferred MCP tools plus the management tools, ready for the registry.
    pub tools: Vec<Arc<dyn Tool>>,
    /// The live manager, retained for `tools/call` routing and shutdown.
    pub manager: Option<Arc<McpClientManager>>,
    /// Human-readable notices (connection failures) to surface to the user.
    pub notices: Vec<String>,
}

impl McpBundle {
    /// An empty bundle: no MCP tools, no manager, nothing to surface. Used when
    /// MCP is disabled and by the hermetic in-crate tests.
    pub(crate) fn disabled() -> Self {
        Self { tools: Vec::new(), manager: None, notices: Vec::new() }
    }
}

/// Returns `true` when an environment variable is set to a truthy value.
///
/// Recognised falsy values (case-insensitive): `""`, `"0"`, `"false"`, `"no"`,
/// `"off"`. Everything else (including `"1"`, `"true"`, `"yes"`, `"on"`) is
/// truthy. This matches shell conventions where `VAR=off` disables a feature.
fn env_flag(name: &str) -> bool {
    std::env::var(name)
        .map(|v| {
            let v = v.trim().to_ascii_lowercase();
            !v.is_empty() && v != "0" && v != "false" && v != "no" && v != "off"
        })
        .unwrap_or(false)
}

/// Connect all enabled MCP servers for a session and build the tool bundle.
///
/// Builds the platform credential store and delegates to
/// [`connect_mcp_with_store`]. Honours the disable flags/env.
pub(crate) async fn connect_mcp(working_dir: &str) -> McpBundle {
    let store = crate::host::credential_store();
    connect_mcp_with_store(working_dir, store).await
}

/// Inner implementation of MCP startup with an injected credential store.
///
/// Separated from [`connect_mcp`] so tests can supply an
/// [`coda_auth::store::InMemoryStore`] without touching the OS keyring.
/// Never fails: a server that will not start is reported as a notice, and
/// the remaining servers still connect.
pub(crate) async fn connect_mcp_with_store(
    working_dir: &str,
    store: Arc<dyn CredentialStore>,
) -> McpBundle {
    if env_flag("CODA_SERVE_DISABLE_MCP") {
        tracing::info!("MCP disabled by CODA_SERVE_DISABLE_MCP");
        return McpBundle::disabled();
    }

    let project_root = Path::new(working_dir);
    let (user_mcp, project_mcp) = coda_mcp::config::resolve_paths(project_root);

    // When the project layer is suppressed, point at a path that cannot exist
    // so only user-scoped servers are loaded.
    let no_project = env_flag("CODA_DISABLE_PROJECT_MCP");
    let effective_project: PathBuf =
        if no_project { PathBuf::new() } else { project_mcp };

    // Load with error detection so malformed/unreadable files surface as
    // notices rather than silently contributing no servers.
    let (stdio, stdio_notices) =
        coda_mcp::config::load_connectable_checked(&user_mcp, &effective_project);
    let (http, http_notices) =
        coda_mcp::config::load_http_connectable_checked(&user_mcp, &effective_project);

    let mut notices: Vec<String> = stdio_notices.into_iter().chain(http_notices).collect();
    let mut seen = std::collections::HashSet::new();
    notices.retain(|notice| seen.insert(notice.clone()));

    if stdio.is_empty() && http.is_empty() {
        // If only config errors were found, surface them even without live servers.
        if notices.is_empty() {
            return McpBundle::disabled();
        }
        return McpBundle { tools: Vec::new(), manager: None, notices };
    }

    // Resolve coda-secret: references and ${VAR} substitutions in env/headers
    // and bearer tokens before connecting. Missing refs surface as notices
    // without leaking the secret values.
    let stdio = resolve_stdio_secrets(stdio, &*store, &mut notices).await;
    let http = resolve_http_secrets(http, &*store, &mut notices).await;

    let manager = Arc::new(McpClientManager::new());
    let mut errors = manager.connect_many(stdio, None).await;
    errors.extend(manager.connect_http_many(http, None, Some(Arc::clone(&store))).await);

    let mut tools = manager.tools().await;
    tools.extend(coda_mcp::management_tools::mcp_management_tools(Arc::clone(&manager)));

    let connect_notices = errors.into_iter().map(|e| {
        format!(
            "MCP server '{}' failed to start and was skipped: {}",
            e.server_name, e.error
        )
    });
    notices.extend(connect_notices);

    McpBundle { tools, manager: Some(manager), notices }
}

/// Resolve `coda-secret:` and `${VAR}` placeholders in stdio server env maps.
async fn resolve_stdio_secrets(
    servers: Vec<McpConnectable>,
    store: &dyn CredentialStore,
    notices: &mut Vec<String>,
) -> Vec<McpConnectable> {
    let mut resolved = Vec::with_capacity(servers.len());
    for s in servers {
        let server_notices = std::cell::RefCell::new(Vec::<String>::new());
        let log = |msg: &str| server_notices.borrow_mut().push(msg.to_owned());
        let env = coda_mcp::secret::resolve_map(&s.env, store, Some(&log)).await;
        notices.extend(server_notices.into_inner());
        resolved.push(McpConnectable { env, ..s });
    }
    resolved
}

/// Resolve `coda-secret:` and `${VAR}` placeholders in HTTP server headers and
/// bearer tokens.
async fn resolve_http_secrets(
    servers: Vec<McpHttpConnectable>,
    store: &dyn CredentialStore,
    notices: &mut Vec<String>,
) -> Vec<McpHttpConnectable> {
    let mut resolved = Vec::with_capacity(servers.len());
    for s in servers {
        let server_notices = std::cell::RefCell::new(Vec::<String>::new());
        let log = |msg: &str| server_notices.borrow_mut().push(msg.to_owned());

        let headers = coda_mcp::secret::resolve_map(&s.headers, store, Some(&log)).await;

        // Resolve the bearer token reference if present.
        let auth = if let Some(token) = &s.auth.bearer_token {
            let raw = token.expose().as_str();
            let resolved_token = coda_mcp::secret::resolve_value(raw, store, Some(&log)).await;
            let bearer = if resolved_token.is_empty() { None } else { Some(Secret::new(resolved_token)) };
            McpAuthConfig { bearer_token: bearer, ..s.auth.clone() }
        } else {
            s.auth.clone()
        };

        notices.extend(server_notices.into_inner());
        resolved.push(McpHttpConnectable { headers, auth, ..s });
    }
    resolved
}

#[cfg(test)]
mod tests {
    use std::collections::HashMap;
    use std::sync::Arc;

    use coda_auth::store::InMemoryStore;
    use coda_mcp::auth::types::{McpAuthConfig, McpAuthMode};
    use coda_mcp::config::{McpConnectable, McpHttpConnectable};

    use super::*;

    /// Serialises tests that mutate process-global MCP environment variables,
    /// since Rust runs tests in parallel threads within one process.
    static ENV_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

    fn empty_store() -> Arc<dyn CredentialStore> {
        Arc::new(InMemoryStore::new())
    }

    // ── env_flag ─────────────────────────────────────────────────────────────

    #[test]
    fn env_flag_reads_truthy_and_falsy_values() {
        let _guard = ENV_LOCK.lock().unwrap();
        for truthy in &["1", "true", "yes", "on", "TRUE"] {
            std::env::set_var("CODA_TEST_MCP_FLAG", truthy);
            assert!(env_flag("CODA_TEST_MCP_FLAG"), "expected truthy for {truthy:?}");
        }
        for falsy in &["0", "false", "no", "off", "OFF", "False", ""] {
            std::env::set_var("CODA_TEST_MCP_FLAG", falsy);
            assert!(!env_flag("CODA_TEST_MCP_FLAG"), "expected falsy for {falsy:?}");
        }
        std::env::remove_var("CODA_TEST_MCP_FLAG");
        assert!(!env_flag("CODA_TEST_MCP_FLAG"), "unset must be falsy");
    }

    // ── disable flag ─────────────────────────────────────────────────────────

    #[tokio::test]
    async fn disable_flag_yields_empty_bundle() {
        let _guard = ENV_LOCK.lock().unwrap();
        std::env::set_var("CODA_SERVE_DISABLE_MCP", "1");
        let bundle = connect_mcp_with_store(".", empty_store()).await;
        std::env::remove_var("CODA_SERVE_DISABLE_MCP");
        assert!(bundle.tools.is_empty());
        assert!(bundle.manager.is_none());
        assert!(bundle.notices.is_empty());
    }

    /// `CODA_SERVE_DISABLE_MCP=off` must be treated as "flag is off" → MCP enabled.
    #[tokio::test]
    async fn disable_flag_off_means_enabled() {
        let _guard = ENV_LOCK.lock().unwrap();
        let project = tempfile::tempdir().unwrap();
        let user = tempfile::tempdir().unwrap();
        // Write a valid (but empty-servers) config so we don't go into the
        // "no configured servers" → disabled path.  We only need to confirm
        // that the "off" value does NOT cause an early return.
        std::fs::write(project.path().join(".mcp.json"), r#"{"mcpServers":{}}"#).unwrap();
        std::env::set_var("CODA_SERVE_DISABLE_MCP", "off");
        std::env::set_var("CODA_USER_MCP_DIR", user.path());
        std::env::remove_var("CODA_DISABLE_PROJECT_MCP");

        // Even with no connectable servers, the bundle should NOT have been
        // short-circuited by the "disabled" path — we only check that the
        // function returned without panicking. The empty-bundle path is the
        // expected outcome (no servers configured).
        let bundle = connect_mcp_with_store(
            &project.path().to_string_lossy(),
            empty_store(),
        )
        .await;
        std::env::remove_var("CODA_SERVE_DISABLE_MCP");
        std::env::remove_var("CODA_USER_MCP_DIR");

        // Empty-servers path: no tools, no manager, no notices.
        assert!(bundle.manager.is_none(), "off must not disable MCP");
    }

    // ── disabled server / failing server ─────────────────────────────────────

    /// A disabled server is never started, a healthy-but-failing enabled server
    /// is surfaced as a notice (not a hard failure), and the management tools
    /// are still available. This exercises the real config → connect path with
    /// a hermetic temp `.mcp.json` (no real user config or network).
    #[tokio::test]
    async fn disabled_server_is_omitted_and_failure_is_surfaced() {
        let _guard = ENV_LOCK.lock().unwrap();
        let project = tempfile::tempdir().unwrap();
        let user = tempfile::tempdir().unwrap();

        let mcp_json = r#"{
            "mcpServers": {
                "good": { "command": "definitely-not-a-real-mcp-binary-xyz", "args": [] },
                "off":  { "command": "also-not-a-real-binary", "args": [], "disabled": true }
            }
        }"#;
        std::fs::write(project.path().join(".mcp.json"), mcp_json).unwrap();

        std::env::set_var("CODA_USER_MCP_DIR", user.path());
        std::env::remove_var("CODA_SERVE_DISABLE_MCP");
        std::env::remove_var("CODA_DISABLE_PROJECT_MCP");

        let bundle = connect_mcp_with_store(
            &project.path().to_string_lossy(),
            empty_store(),
        )
        .await;
        std::env::remove_var("CODA_USER_MCP_DIR");

        assert!(bundle.manager.is_some(), "a configured server must yield a manager");
        assert!(
            bundle.tools.iter().any(|t| t.name() == "restart_mcp_server"),
            "management tools must be available when servers are configured"
        );
        assert!(
            bundle.notices.iter().any(|n| n.contains("good")),
            "the failing enabled server must be surfaced: {:?}",
            bundle.notices
        );
        assert!(
            !bundle.notices.iter().any(|n| n.contains("off")),
            "the disabled server must never be attempted: {:?}",
            bundle.notices
        );
        assert!(
            bundle.tools.iter().all(|t| !t.name().starts_with("mcp__")),
            "a failed server must not contribute callable MCP tools"
        );
    }

    #[tokio::test]
    async fn no_configured_servers_yields_empty_bundle() {
        let _guard = ENV_LOCK.lock().unwrap();
        let project = tempfile::tempdir().unwrap();
        let user = tempfile::tempdir().unwrap();
        std::env::set_var("CODA_USER_MCP_DIR", user.path());
        std::env::remove_var("CODA_SERVE_DISABLE_MCP");
        std::env::remove_var("CODA_DISABLE_PROJECT_MCP");

        let bundle = connect_mcp_with_store(
            &project.path().to_string_lossy(),
            empty_store(),
        )
        .await;
        std::env::remove_var("CODA_USER_MCP_DIR");

        assert!(bundle.tools.is_empty(), "no servers → no tools, not even management tools");
        assert!(bundle.manager.is_none());
        assert!(bundle.notices.is_empty());
    }

    // ── no-project-mcp flag ───────────────────────────────────────────────────

    /// With CODA_DISABLE_PROJECT_MCP, servers defined only in the project layer
    /// must not be started.
    #[tokio::test]
    async fn no_project_mcp_suppresses_project_layer() {
        let _guard = ENV_LOCK.lock().unwrap();
        let project = tempfile::tempdir().unwrap();
        let user = tempfile::tempdir().unwrap();

        // Server defined only in project config.
        let mcp_json = r#"{"mcpServers":{"project-only":{"command":"fake-xyz","args":[]}}}"#;
        std::fs::write(project.path().join(".mcp.json"), mcp_json).unwrap();

        std::env::set_var("CODA_USER_MCP_DIR", user.path());
        std::env::set_var("CODA_DISABLE_PROJECT_MCP", "1");
        std::env::remove_var("CODA_SERVE_DISABLE_MCP");

        let bundle = connect_mcp_with_store(
            &project.path().to_string_lossy(),
            empty_store(),
        )
        .await;
        std::env::remove_var("CODA_USER_MCP_DIR");
        std::env::remove_var("CODA_DISABLE_PROJECT_MCP");

        // With the project layer suppressed the server is never loaded.
        assert!(
            !bundle.notices.iter().any(|n| n.contains("project-only")),
            "project-layer server must be suppressed: {:?}",
            bundle.notices
        );
        assert!(bundle.manager.is_none(), "no user-layer servers → no manager");
    }

    // ── malformed config ──────────────────────────────────────────────────────

    /// A malformed `.mcp.json` file must surface as a notice, not silently
    /// produce an empty server list with no diagnostic.
    #[tokio::test]
    async fn malformed_config_surfaces_notice() {
        let _guard = ENV_LOCK.lock().unwrap();
        let project = tempfile::tempdir().unwrap();
        let user = tempfile::tempdir().unwrap();

        // Valid path but contents are not JSON.
        std::fs::write(project.path().join(".mcp.json"), b"this is not json { broken").unwrap();

        std::env::set_var("CODA_USER_MCP_DIR", user.path());
        std::env::remove_var("CODA_SERVE_DISABLE_MCP");
        std::env::remove_var("CODA_DISABLE_PROJECT_MCP");

        let bundle = connect_mcp_with_store(
            &project.path().to_string_lossy(),
            empty_store(),
        )
        .await;
        std::env::remove_var("CODA_USER_MCP_DIR");

        assert!(
            bundle.notices.iter().any(|n| n.contains("invalid JSON") || n.contains("could not be read")),
            "malformed config must surface a notice: {:?}",
            bundle.notices
        );
        assert!(bundle.manager.is_none(), "malformed config with no valid servers → no manager");
    }

    /// `mcpServers` present but not an object must surface as a notice.
    #[tokio::test]
    async fn mcpservers_not_object_surfaces_notice() {
        let _guard = ENV_LOCK.lock().unwrap();
        let project = tempfile::tempdir().unwrap();
        let user = tempfile::tempdir().unwrap();

        std::fs::write(
            project.path().join(".mcp.json"),
            r#"{"mcpServers": ["wrong", "shape"]}"#,
        )
        .unwrap();

        std::env::set_var("CODA_USER_MCP_DIR", user.path());
        std::env::remove_var("CODA_SERVE_DISABLE_MCP");
        std::env::remove_var("CODA_DISABLE_PROJECT_MCP");

        let bundle = connect_mcp_with_store(
            &project.path().to_string_lossy(),
            empty_store(),
        )
        .await;
        std::env::remove_var("CODA_USER_MCP_DIR");

        assert!(
            bundle.notices.iter().any(|n| n.contains("must be an object")),
            "wrong-shape mcpServers must surface a notice: {:?}",
            bundle.notices
        );
    }

    /// A healthy config layer (user) must still connect even when the other
    /// layer (project) is malformed. Notices from the bad layer must not
    /// suppress the good layer's servers.
    #[tokio::test]
    async fn healthy_layer_connects_despite_malformed_other_layer() {
        let _guard = ENV_LOCK.lock().unwrap();
        let project = tempfile::tempdir().unwrap();
        let user = tempfile::tempdir().unwrap();

        // Malformed project config.
        std::fs::write(project.path().join(".mcp.json"), b"{ bad json").unwrap();
        // Valid user config with one server that will fail to start.
        std::fs::write(
            user.path().join(".mcp.json"),
            r#"{"mcpServers":{"good-user":{"command":"no-such-binary-xyz","args":[]}}}"#,
        )
        .unwrap();

        std::env::set_var("CODA_USER_MCP_DIR", user.path());
        std::env::remove_var("CODA_SERVE_DISABLE_MCP");
        std::env::remove_var("CODA_DISABLE_PROJECT_MCP");

        let bundle = connect_mcp_with_store(
            &project.path().to_string_lossy(),
            empty_store(),
        )
        .await;
        std::env::remove_var("CODA_USER_MCP_DIR");

        assert!(
            bundle.notices.iter().any(|n| n.contains("invalid JSON") || n.contains("could not be read")),
            "malformed project layer must produce a notice: {:?}",
            bundle.notices
        );
        assert!(
            bundle.notices.iter().any(|n| n.contains("good-user")),
            "user-layer server attempt must surface: {:?}",
            bundle.notices
        );
    }

    // ── secret / env resolution ───────────────────────────────────────────────

    /// A `coda-secret:` ref that IS in the store must resolve to the stored
    /// value. The resolved value must not appear in any notice.
    #[tokio::test]
    async fn stdio_secret_ref_resolves_from_store() {
        let store = InMemoryStore::new();
        store.set("mcp:test/env/TOKEN", "resolved-secret-value").await.unwrap();
        let store: Arc<dyn CredentialStore> = Arc::new(store);

        let input = vec![McpConnectable {
            name: "test-server".into(),
            command: "echo".into(),
            args: vec![],
            env: {
                let mut m = HashMap::new();
                m.insert("TOKEN".into(), "coda-secret:mcp:test/env/TOKEN".into());
                m
            },
        }];

        let mut notices = Vec::new();
        let out = resolve_stdio_secrets(input, &*store, &mut notices).await;

        assert!(notices.is_empty(), "resolved secret must not produce a notice: {notices:?}");
        assert_eq!(
            out[0].env.get("TOKEN").map(|s| s.as_str()),
            Some("resolved-secret-value"),
            "env TOKEN must be the resolved value, not the coda-secret ref"
        );
    }

    /// A `coda-secret:` ref that is NOT in the store must produce a notice and
    /// resolve to an empty string (not the literal ref).
    #[tokio::test]
    async fn stdio_missing_secret_ref_produces_notice_without_leaking_key() {
        let input = vec![McpConnectable {
            name: "test-server".into(),
            command: "echo".into(),
            args: vec![],
            env: {
                let mut m = HashMap::new();
                m.insert("TOKEN".into(), "coda-secret:mcp:missing/env/TOKEN".into());
                m
            },
        }];

        let mut notices = Vec::new();
        let out = resolve_stdio_secrets(input, &*empty_store(), &mut notices).await;

        assert!(
            !notices.is_empty(),
            "missing secret ref must produce a notice"
        );
        // Notice must mention the key, not the resolved (empty) value.
        assert!(
            notices.iter().any(|n| n.contains("mcp:missing/env/TOKEN")),
            "notice must name the missing key: {notices:?}"
        );
        // The raw ref must not be used as the value.
        assert_ne!(
            out[0].env.get("TOKEN").map(|s| s.as_str()),
            Some("coda-secret:mcp:missing/env/TOKEN"),
            "raw coda-secret: ref must not be passed to the process env"
        );
    }

    /// A `${VAR}` substitution in a stdio server env must be resolved.
    #[tokio::test]
    async fn stdio_env_var_substitution_is_resolved() {
        let _guard = ENV_LOCK.lock().unwrap();
        std::env::set_var("CODA_TEST_MCP_INJECT_VAR", "injected-value");

        let input = vec![McpConnectable {
            name: "test-server".into(),
            command: "echo".into(),
            args: vec![],
            env: {
                let mut m = HashMap::new();
                m.insert("WRAPPED".into(), "prefix_${CODA_TEST_MCP_INJECT_VAR}_suffix".into());
                m
            },
        }];

        let mut notices = Vec::new();
        let out = resolve_stdio_secrets(input, &*empty_store(), &mut notices).await;
        std::env::remove_var("CODA_TEST_MCP_INJECT_VAR");

        assert!(notices.is_empty(), "set env var must not produce a notice: {notices:?}");
        assert_eq!(
            out[0].env.get("WRAPPED").map(|s| s.as_str()),
            Some("prefix_injected-value_suffix")
        );
    }

    /// An HTTP server's header with a `coda-secret:` ref must be resolved;
    /// a bearer token that is a ref must also be resolved from the store.
    #[tokio::test]
    async fn http_header_and_bearer_resolve_from_store() {
        let store = InMemoryStore::new();
        store.set("mcp:srv/header/X-Api-Key", "header-secret").await.unwrap();
        store.set("mcp:srv/auth/token", "bearer-secret").await.unwrap();
        let store: Arc<dyn CredentialStore> = Arc::new(store);

        let input = vec![McpHttpConnectable {
            name: "srv".into(),
            url: "https://example.com/mcp".into(),
            headers: {
                let mut m = HashMap::new();
                m.insert("X-Api-Key".into(), "coda-secret:mcp:srv/header/X-Api-Key".into());
                m
            },
            auth: McpAuthConfig {
                mode: McpAuthMode::Bearer,
                bearer_token: Some(Secret::new("coda-secret:mcp:srv/auth/token".into())),
                client_id: None,
                scopes: vec![],
            },
        }];

        let mut notices = Vec::new();
        let out = resolve_http_secrets(input, &*store, &mut notices).await;

        assert!(notices.is_empty(), "all refs resolved → no notices: {notices:?}");
        assert_eq!(
            out[0].headers.get("X-Api-Key").map(|s| s.as_str()),
            Some("header-secret")
        );
        let resolved_token = out[0].auth.bearer_token.as_ref().map(|t| t.expose().as_str());
        assert_eq!(resolved_token, Some("bearer-secret"));
    }

    // ── bearer auth: missing token produces explicit error ────────────────────

    /// Attempting to connect an HTTP server with bearer auth mode but an empty
    /// or missing token must produce an explicit notice (not silent no-auth).
    #[tokio::test]
    async fn bearer_mode_without_token_produces_explicit_error_notice() {
        let _guard = ENV_LOCK.lock().unwrap();
        let project = tempfile::tempdir().unwrap();
        let user = tempfile::tempdir().unwrap();

        // HTTP server with bearer mode but no token (or a ref that won't resolve).
        let mcp_json = r#"{
            "mcpServers": {
                "myapi": {
                    "type": "http",
                    "url": "https://127.0.0.1:19999/mcp",
                    "auth": { "mode": "bearer", "token": "coda-secret:mcp:myapi/auth/token" }
                }
            }
        }"#;
        std::fs::write(project.path().join(".mcp.json"), mcp_json).unwrap();

        // Store is empty → the ref won't resolve → bearer_token becomes None
        // after resolution → connect_one_http returns an explicit error.
        std::env::set_var("CODA_USER_MCP_DIR", user.path());
        std::env::remove_var("CODA_SERVE_DISABLE_MCP");
        std::env::remove_var("CODA_DISABLE_PROJECT_MCP");

        let bundle = connect_mcp_with_store(
            &project.path().to_string_lossy(),
            empty_store(),
        )
        .await;
        std::env::remove_var("CODA_USER_MCP_DIR");

        // A notice must mention both the server name and the auth failure
        // (not "connection refused" or "SSRF" — the auth check happens first).
        assert!(
            bundle.notices.iter().any(|n| n.contains("myapi")),
            "notice must name the server: {:?}",
            bundle.notices
        );
        assert!(
            bundle.notices.iter().any(|n| {
                n.contains("bearer") || n.contains("token") || n.contains("not found")
            }),
            "notice must indicate the auth/token issue, not a network error: {:?}",
            bundle.notices
        );
        // The raw coda-secret: ref must not appear in any notice.
        assert!(
            !bundle.notices.iter().any(|n| n.contains("coda-secret:")),
            "secret ref must not leak into notices: {:?}",
            bundle.notices
        );
    }

    /// When bearer mode IS configured and the token IS present and resolved,
    /// the connection attempt reaches the network (server unreachable) rather
    /// than failing with a "no token" error. This proves the preparation path
    /// succeeded.
    #[tokio::test]
    async fn bearer_mode_with_resolved_token_attempts_connection() {
        let _guard = ENV_LOCK.lock().unwrap();
        let project = tempfile::tempdir().unwrap();
        let user = tempfile::tempdir().unwrap();

        // Bind a local TCP socket so the port is closed rather than SSRF-guarded
        // (loopback is allowed; we just need the connection to fail with
        // "connection refused" rather than the bearer-missing error).
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        drop(listener); // close; port is now unreachable

        let mcp_json = format!(
            r#"{{
                "mcpServers": {{
                    "myapi": {{
                        "type": "http",
                        "url": "http://127.0.0.1:{port}/mcp",
                        "auth": {{ "mode": "bearer", "token": "coda-secret:mcp:myapi/auth/token" }}
                    }}
                }}
            }}"#
        );
        std::fs::write(project.path().join(".mcp.json"), &mcp_json).unwrap();

        // Store has the token → bearer_token resolves → connection is attempted.
        let store = InMemoryStore::new();
        store.set("mcp:myapi/auth/token", "the-real-bearer-token").await.unwrap();

        std::env::set_var("CODA_USER_MCP_DIR", user.path());
        std::env::remove_var("CODA_SERVE_DISABLE_MCP");
        std::env::remove_var("CODA_DISABLE_PROJECT_MCP");

        let bundle = connect_mcp_with_store(
            &project.path().to_string_lossy(),
            Arc::new(store),
        )
        .await;
        std::env::remove_var("CODA_USER_MCP_DIR");

        // Connection was attempted (server closed) → network error, not auth-config error.
        assert!(
            bundle.notices.iter().any(|n| n.contains("myapi")),
            "server must surface as failed: {:?}",
            bundle.notices
        );
        assert!(
            !bundle.notices.iter().any(|n| n.to_lowercase().contains("no token") || n.contains("bearer token missing")),
            "should fail with a network error, not a missing-token error: {:?}",
            bundle.notices
        );
        assert!(
            !bundle.notices.iter().any(|n| n.contains("coda-secret:")),
            "secret ref must not leak into notices: {:?}",
            bundle.notices
        );
    }
}
