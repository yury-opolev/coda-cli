//! `McpClientManager` — connects all configured MCP servers and aggregates
//! their tools into the agent registry.
//!
//! # Design
//!
//! Each server gets its own `McpClient` (stdio) or `McpHttpClient` (HTTP).
//! The manager runs all connects concurrently; a server that fails to start
//! is logged and skipped without blocking the others or crashing the session.
//!
//! Tools are namespaced `mcp__{server}__{tool}` to guarantee no collision
//! with built-in names, and are returned as `Arc<dyn Tool>` ready for
//! insertion into any `ToolRegistry`.
//!
//! # Thread safety
//!
//! The manager is `Send + Sync`. Client handles are stored behind an
//! `Arc<RwLock<…>>` so `call_tool` can be called concurrently from the
//! agent loop's parallel tool execution paths.

use std::collections::HashMap;
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use tokio::sync::RwLock;
use tokio_util::sync::CancellationToken;

use coda_auth::store::CredentialStore;
use coda_tool::{Tool, ToolSource};

use crate::auth::{McpAuthMode, McpAuthProvider, McpOAuthProvider, StaticBearerAuthProvider};
use crate::client::{McpClient, McpServerInfo, McpToolCallError, McpToolInfo, DEFAULT_CONNECT_TIMEOUT};
use crate::config::{self, McpConnectable, McpHttpConnectable};
use crate::error::{McpConnectError, McpError};
use crate::http_client::{McpHttpClient, DEFAULT_HTTP_CONNECT_TIMEOUT};
use crate::process::{McpProcess, ProcessExit, TerminationReport};
use crate::tool::McpTool;

/// Default shutdown grace period when the manager is dropped.
const SHUTDOWN_GRACE: Duration = Duration::from_secs(3);

/// Unified client handle: either a stdio or HTTP MCP client.
enum AnyClient {
    Stdio(McpClient),
    Http(McpHttpClient),
}

impl AnyClient {
    fn server_info(&self) -> &McpServerInfo {
        match self {
            Self::Stdio(c) => &c.server_info,
            Self::Http(c) => &c.server_info,
        }
    }

    fn tools(&self) -> &[McpToolInfo] {
        match self {
            Self::Stdio(c) => &c.tools,
            Self::Http(c) => &c.tools,
        }
    }

    async fn call_tool(
        &self,
        name: &str,
        arguments: &serde_json::Value,
    ) -> Result<(String, bool), McpToolCallError> {
        match self {
            Self::Stdio(c) => c.call_tool(name, arguments).await,
            Self::Http(c) => c.call_tool(name, arguments).await.map_err(|e| {
                // Map the HTTP-specific error string into a Transport variant so
                // the manager's unified error path produces a good message.
                McpToolCallError::Transport(crate::error::McpTransportError::Other(e))
            }),
        }
    }

    async fn shutdown(self, deadline: tokio::time::Instant) -> Result<(), String> {
        match self {
            Self::Stdio(c) => c.shutdown(deadline).await,
            Self::Http(c) => {
                c.shutdown().await;
                Ok(())
            }
        }
    }

    async fn list_prompts(&self) -> Vec<crate::client::McpPromptInfo> {
        match self {
            Self::Stdio(c) => c.list_prompts().await,
            Self::Http(c) => c.list_prompts().await,
        }
    }

    async fn list_resources(&self) -> Vec<crate::client::McpResourceInfo> {
        match self {
            Self::Stdio(c) => c.list_resources().await,
            Self::Http(c) => c.list_resources().await,
        }
    }

    async fn get_prompt(&self, name: &str) -> Result<String, String> {
        match self {
            Self::Stdio(c) => c.get_prompt(name).await,
            Self::Http(c) => c.get_prompt(name).await,
        }
    }

    async fn read_resource(&self, uri: &str) -> Result<String, String> {
        match self {
            Self::Stdio(c) => c.read_resource(uri).await,
            Self::Http(c) => c.read_resource(uri).await,
        }
    }
}

/// Per-server state held by the manager.
struct ServerEntry {
    client: AnyClient,
    /// Child process kept alive as long as the entry exists; `None` for HTTP.
    _process: Option<McpProcess>,
}

impl ServerEntry {
    async fn shutdown(self, deadline: tokio::time::Instant) -> Result<(), String> {
        let process = async {
            match self._process {
                Some(process) => process
                    .shutdown(deadline)
                    .await
                    .map_err(|error| error.to_string()),
                None => Ok(()),
            }
        };
        let (client, process) = tokio::join!(self.client.shutdown(deadline), process);
        let errors: Vec<String> = [client, process]
            .into_iter()
            .filter_map(Result::err)
            .collect();
        if errors.is_empty() {
            Ok(())
        } else {
            Err(errors.join("; "))
        }
    }

    /// Stops the server for a restart. `None` when there was no process to
    /// observe (HTTP, or an injected client).
    async fn terminate(self, deadline: tokio::time::Instant) -> Option<TerminationReport> {
        let process = async {
            match self._process {
                Some(process) => Some(process.terminate(deadline).await),
                None => None,
            }
        };
        let client = async {
            if let Err(error) = self.client.shutdown(deadline).await {
                tracing::debug!(%error, "MCP client shutdown reported an error");
            }
        };
        let ((), report) = tokio::join!(client, process);
        report
    }
}

/// Enough to reconnect a server after a restart.
///
/// Retained for the life of the manager so a restart can relaunch without
/// re-reading and re-resolving the config, which would silently pick up an
/// edit the user has not approved — and would fail entirely for a server that
/// is no longer present in the file.
#[derive(Clone)]
enum ServerSpec {
    Stdio(Box<McpConnectable>),
    Http(Box<config::McpHttpConnectable>, Option<Arc<dyn CredentialStore>>),
}

/// What the running manager knows about one server. Names and counts only —
/// never a command line, a URL, an env value or a header.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpServerStatus {
    pub name: String,
    pub tool_count: usize,
}

/// A configured server that is not currently connected, and why.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpServerFault {
    pub name: String,
    pub last_error: String,
}

/// What happened to the old process during a restart.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum OldProcessOutcome {
    NotRunning,
    /// HTTP server: there is no process, only a session.
    NoProcess,
    ExitedCleanly { leftovers_killed: Option<usize> },
    AlreadyExited(String),
    ForceKilled { grace: Duration, leftovers_killed: Option<usize> },
    Unconfirmed(String),
}

impl OldProcessOutcome {
    fn from_report(report: Option<TerminationReport>) -> Self {
        let Some(report) = report else {
            return Self::NoProcess;
        };
        match report.exit {
            ProcessExit::Graceful => Self::ExitedCleanly { leftovers_killed: report.leftovers_killed },
            ProcessExit::Exited(status) => Self::AlreadyExited(status),
            ProcessExit::ForceKilled => Self::ForceKilled {
                grace: report.grace,
                leftovers_killed: report.leftovers_killed,
            },
            ProcessExit::Unconfirmed(error) => Self::Unconfirmed(error),
        }
    }
}

/// Result of a successful restart.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RestartReport {
    pub tool_count: usize,
    pub old_process: OldProcessOutcome,
}

impl RestartReport {
    pub fn summary(&self, server: &str) -> String {
        fn leftovers(count: Option<usize>) -> String {
            match count {
                Some(k) if k > 0 => format!("; {k} leftover process(es) terminated"),
                _ => String::new(),
            }
        }
        let old = match &self.old_process {
            OldProcessOutcome::NotRunning => "server was not running".to_string(),
            OldProcessOutcome::NoProcess => "HTTP session reopened".to_string(),
            OldProcessOutcome::ExitedCleanly { leftovers_killed } => {
                format!("old process exited cleanly{}", leftovers(*leftovers_killed))
            }
            OldProcessOutcome::AlreadyExited(status) => {
                format!("old process had already exited: {status}")
            }
            OldProcessOutcome::ForceKilled { grace, leftovers_killed } => format!(
                "old process force-killed after {}s{}",
                grace.as_secs(),
                leftovers(*leftovers_killed)
            ),
            OldProcessOutcome::Unconfirmed(error) => {
                format!("warning: termination of the old process could not be confirmed: {error}")
            }
        };
        format!(
            "Restarted MCP server '{server}' ({old}). {} tool(s) available.",
            self.tool_count
        )
    }
}

/// One configured server: how to launch it, its live connection (if any) and
/// what is known about its last failure.
struct ServerSlot {
    spec: ServerSpec,
    live: Option<ServerEntry>,
    last_error: Option<String>,
    /// Last known tools, kept while disconnected.
    tools: Vec<McpToolInfo>,
    /// Bumped on every successful (re)connect so a stale failure report
    /// cannot undo a newer connection.
    generation: u64,
}

/// Connects configured MCP servers and exposes their tools.
pub struct McpClientManager {
    /// Per-server slot, protected so `call_tool` is lock-free while the
    /// manager is stable and only serialised during connect/disconnect.
    servers: RwLock<HashMap<String, ServerSlot>>,
    restart_locks: std::sync::Mutex<HashMap<String, Arc<tokio::sync::Mutex<()>>>>,
    /// `(server, tool)` for every slot, sorted; rebuilt whenever `servers` changes.
    tool_snapshot: std::sync::RwLock<Vec<(String, McpToolInfo)>>,
    tool_timeout: Duration,
    stopping: CancellationToken,
}

impl McpClientManager {
    /// Create an empty manager.
    pub fn new() -> Self {
        Self {
            servers: RwLock::new(HashMap::new()),
            restart_locks: std::sync::Mutex::new(HashMap::new()),
            tool_snapshot: std::sync::RwLock::new(Vec::new()),
            tool_timeout: McpClient::resolve_tool_timeout(),
            stopping: CancellationToken::new(),
        }
    }

    fn rebuild_snapshot(&self, map: &HashMap<String, ServerSlot>) {
        let mut snapshot: Vec<(String, McpToolInfo)> = map
            .iter()
            .flat_map(|(name, slot)| slot.tools.iter().map(|tool| (name.clone(), tool.clone())))
            .collect();
        snapshot.sort_by(|a, b| (&a.0, &a.1.name).cmp(&(&b.0, &b.1.name)));
        *self.tool_snapshot.write().unwrap_or_else(|e| e.into_inner()) = snapshot;
    }

    fn record_connected(map: &mut HashMap<String, ServerSlot>, name: String, spec: ServerSpec, entry: ServerEntry) {
        let generation = map.get(&name).map_or(0, |slot| slot.generation) + 1;
        let tools = entry.client.tools().to_vec();
        map.insert(
            name,
            ServerSlot { spec, live: Some(entry), last_error: None, tools, generation },
        );
    }

    fn record_failed(map: &mut HashMap<String, ServerSlot>, name: String, spec: ServerSpec, error: String) {
        let generation = map.get(&name).map_or(0, |slot| slot.generation);
        map.insert(
            name,
            ServerSlot { spec, live: None, last_error: Some(error), tools: Vec::new(), generation },
        );
    }

    fn unknown_server(name: &str, map: &HashMap<String, ServerSlot>) -> String {
        let mut names: Vec<&str> = map.keys().map(String::as_str).collect();
        names.sort_unstable();
        if names.is_empty() {
            format!("unknown MCP server '{name}'; no MCP servers are configured")
        } else {
            format!("unknown MCP server '{name}'; configured servers: {}", names.join(", "))
        }
    }

    fn not_connected(name: &str, slot: &ServerSlot) -> String {
        let reason = slot
            .last_error
            .as_ref()
            .map(|e| format!(" (last error: {e})"))
            .unwrap_or_default();
        format!(
            "MCP server '{name}' is not connected{reason}. \
             Call restart_mcp_server with server='{name}' to relaunch it."
        )
    }

    /// Marks a server disconnected after its transport died, unless a newer
    /// connection has replaced the one that failed.
    async fn mark_disconnected(&self, name: &str, generation: u64, reason: String) {
        let old = {
            let mut guard = self.servers.write().await;
            let old = match guard.get_mut(name) {
                Some(slot) if slot.generation == generation => {
                    let old = slot.live.take();
                    if old.is_some() {
                        slot.last_error = Some(reason);
                    }
                    old
                }
                _ => None,
            };
            if old.is_some() {
                self.rebuild_snapshot(&guard);
            }
            old
        };
        if let Some(entry) = old {
            let server = name.to_string();
            tokio::spawn(async move {
                let report = entry.terminate(tokio::time::Instant::now() + SHUTDOWN_GRACE).await;
                tracing::debug!(%server, ?report, "terminated MCP server after transport loss");
            });
        }
    }

    async fn until_shutdown<T>(&self, work: impl std::future::Future<Output = T>) -> Result<T, String> {
        tokio::select! {
            biased;
            _ = self.stopping.cancelled() => Err("MCP manager is shutting down".into()),
            result = work => Ok(result),
        }
    }

    /// Connect all enabled stdio and HTTP servers from the given `.mcp.json` paths.
    ///
    /// Servers are connected concurrently. Failures are collected and returned
    /// alongside the successfully connected servers; each failure is already
    /// logged at warn level so the caller does not have to re-log.
    ///
    /// This method is designed to be called once at startup; calling it again
    /// adds more servers without removing existing ones.
    ///
    /// Resolve secret references before calling this method. Pass a
    /// `credential_store` to enable cached OAuth credentials for HTTP servers.
    pub async fn connect_all(
        &self,
        user_mcp: &Path,
        project_mcp: &Path,
        credential_store: Option<Arc<dyn CredentialStore>>,
    ) -> Vec<McpConnectError> {
        let connectable = config::load_connectable(user_mcp, project_mcp);
        let http_connectable = config::load_http_connectable(user_mcp, project_mcp);
        let mut errors = self.connect_many(connectable, None).await;
        errors.extend(self.connect_http_many(http_connectable, None, credential_store).await);
        errors
    }

    /// Connect all servers from a supplied list, optionally overriding the
    /// per-server connect timeout (used in tests).
    pub async fn connect_many(
        &self,
        servers: Vec<McpConnectable>,
        connect_timeout_override: Option<Duration>,
    ) -> Vec<McpConnectError> {
        let connect_timeout = connect_timeout_override.unwrap_or(DEFAULT_CONNECT_TIMEOUT);
        let tool_timeout = self.tool_timeout;

        let futures: Vec<_> = servers
            .into_iter()
            .map(|cfg| {
                let name = cfg.name.clone();
                async move {
                    let result = connect_one_stdio(&cfg, connect_timeout, tool_timeout).await;
                    (name, ServerSpec::Stdio(Box::new(cfg)), result)
                }
            })
            .collect();

        let results = futures::future::join_all(futures).await;
        let mut errors = Vec::new();
        let mut guard = self.servers.write().await;

        for (name, spec, result) in results {
            match result {
                Ok(entry) => {
                    tracing::info!(
                        server = %name,
                        server_info = %entry.client.server_info().name,
                        tools = entry.client.tools().len(),
                        "MCP stdio server connected"
                    );
                    Self::record_connected(&mut guard, name, spec, entry);
                }
                Err(error) => {
                    tracing::warn!(server = %name, %error, "MCP stdio server failed to start");
                    Self::record_failed(&mut guard, name.clone(), spec, error.to_string());
                    errors.push(McpConnectError { server_name: name, error });
                }
            }
        }
        self.rebuild_snapshot(&guard);
        errors
    }

    /// Connect HTTP servers from a supplied list.
    ///
    /// Pass a `credential_store` to enable OAuth (cached-token mode) and to
    /// build static bearer providers from resolved secret references.
    /// `None` is accepted but results in an explicit error for servers that
    /// require authentication — never silently unauthenticated.
    pub async fn connect_http_many(
        &self,
        servers: Vec<McpHttpConnectable>,
        connect_timeout_override: Option<Duration>,
        credential_store: Option<Arc<dyn CredentialStore>>,
    ) -> Vec<McpConnectError> {
        let connect_timeout = connect_timeout_override.unwrap_or(DEFAULT_HTTP_CONNECT_TIMEOUT);
        let tool_timeout = self.tool_timeout;

        let futures: Vec<_> = servers
            .into_iter()
            .map(|cfg| {
                let name = cfg.name.clone();
                let store = credential_store.clone();
                async move {
                    let result = connect_one_http(&cfg, connect_timeout, tool_timeout, store.as_ref()).await;
                    (name, ServerSpec::Http(Box::new(cfg), store), result)
                }
            })
            .collect();

        let results = futures::future::join_all(futures).await;
        let mut errors = Vec::new();
        let mut guard = self.servers.write().await;

        for (name, spec, result) in results {
            match result {
                Ok(entry) => {
                    tracing::info!(
                        server = %name,
                        server_info = %entry.client.server_info().name,
                        tools = entry.client.tools().len(),
                        "MCP HTTP server connected"
                    );
                    Self::record_connected(&mut guard, name, spec, entry);
                }
                Err(error) => {
                    tracing::warn!(server = %name, %error, "MCP HTTP server failed to start");
                    Self::record_failed(&mut guard, name.clone(), spec, error.to_string());
                    errors.push(McpConnectError { server_name: name, error });
                }
            }
        }
        self.rebuild_snapshot(&guard);
        errors
    }

    /// Per-server runtime status: which servers this manager actually holds a
    /// live client for, and how many tools each advertises.
    ///
    /// Deliberately *only* what the manager already knows. It performs no
    /// connection attempt, no `tools/list` round-trip and — critically — no
    /// credential-store read: reading a status must never probe an auth store
    /// or wake a keyring prompt.
    ///
    /// A server absent from the returned map is one this manager has no
    /// client for. That is not the same as "not configured": the caller
    /// combines this with the file inventory, so a file edited after startup
    /// reads as `configured + not connected` rather than as a fiction.
    pub async fn connected_status(&self) -> Vec<McpServerStatus> {
        let guard = self.servers.read().await;
        let mut out: Vec<McpServerStatus> = guard
            .iter()
            .filter_map(|(name, slot)| {
                slot.live.as_ref().map(|entry| McpServerStatus {
                    name: name.clone(),
                    tool_count: entry.client.tools().len(),
                })
            })
            .collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }

    /// Configured servers that are not connected, with the last error.
    pub async fn server_faults(&self) -> Vec<McpServerFault> {
        let guard = self.servers.read().await;
        let mut out: Vec<McpServerFault> = guard
            .iter()
            .filter(|(_, slot)| slot.live.is_none())
            .map(|(name, slot)| McpServerFault {
                name: name.clone(),
                last_error: slot.last_error.clone().unwrap_or_else(|| "not connected".into()),
            })
            .collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }

    /// Return all tools across all configured servers as `Arc<dyn Tool>`,
    /// including the last known tools of disconnected servers.
    ///
    /// Called after `connect_all`; the returned vector is fed into
    /// `ToolRegistry::new` alongside the built-in tools.
    pub async fn tools(self: &Arc<Self>) -> Vec<Arc<dyn Tool>> {
        let snapshot = self.tool_snapshot.read().unwrap_or_else(|e| e.into_inner());
        snapshot
            .iter()
            .map(|(server_name, info)| {
                let tool: Arc<dyn Tool> = Arc::new(McpTool::new(
                    server_name.clone(),
                    info.clone(),
                    Arc::clone(self),
                ));
                tool
            })
            .collect()
    }

    /// A live view of this manager's tools for `ToolRegistry::with_source`.
    /// Follows restarts and keeps the last-known tools of disconnected servers.
    pub fn tool_source(self: &Arc<Self>) -> Arc<dyn ToolSource> {
        Arc::new(McpToolSource { manager: Arc::clone(self) })
    }

    /// Call a tool on the named server.
    ///
    /// Returns the (text, is_error) pair from the server's `tools/call`
    /// response, or a user-facing error string (wrapped in `Err`) when the
    /// server is not connected or the call fails.
    pub async fn call_tool(
        &self,
        server_name: &str,
        tool_name: &str,
        arguments: &serde_json::Value,
    ) -> Result<(String, bool), String> {
        let guard = self.servers.read().await;
        let Some(slot) = guard.get(server_name) else {
            return Err(Self::unknown_server(server_name, &guard));
        };
        let Some(entry) = slot.live.as_ref() else {
            return Err(Self::not_connected(server_name, slot));
        };
        let generation = slot.generation;

        let outcome = self.until_shutdown(entry.client.call_tool(tool_name, arguments)).await?;
        drop(guard);
        match outcome {
            Ok(pair) => Ok(pair),
            Err(McpToolCallError::Timeout) => Err(format!(
                "MCP tool '{tool_name}' on server '{server_name}' timed out. \
                 The server may be unresponsive."
            )),
            Err(McpToolCallError::Transport(e)) if is_transport_loss(&e) => {
                self.mark_disconnected(server_name, generation, e.to_string()).await;
                Err(format!(
                    "MCP tool '{tool_name}' on server '{server_name}' failed: {e}. \
                     The server connection was lost; it is now marked disconnected. \
                     Call restart_mcp_server with server='{server_name}' to relaunch it, \
                     then retry."
                ))
            }
            Err(McpToolCallError::Transport(e)) => Err(format!(
                "MCP tool '{tool_name}' on server '{server_name}' failed: {e}. \
                 The server connection was lost."
            )),
        }
    }

    /// Lists prompts across every connected server, tagged with the server name.
    ///
    /// A server that does not support prompts, or fails to answer, contributes
    /// nothing rather than failing the whole listing — one broken server must
    /// not hide every other server's prompts.
    pub async fn list_prompts(&self) -> Vec<(String, crate::client::McpPromptInfo)> {
        let guard = self.servers.read().await;
        let mut out = Vec::new();
        for (name, entry) in guard.iter().filter_map(|(n, s)| s.live.as_ref().map(|e| (n, e))) {
            let prompts = match self.until_shutdown(entry.client.list_prompts()).await {
                Ok(prompts) => prompts,
                Err(error) => {
                    tracing::warn!(%error, "MCP prompt listing interrupted");
                    break;
                }
            };
            for prompt in prompts {
                out.push((name.clone(), prompt));
            }
        }
        out.sort_by(|a, b| (&a.0, &a.1.name).cmp(&(&b.0, &b.1.name)));
        out
    }

    /// Lists resources across every connected server, tagged with the server name.
    pub async fn list_resources(&self) -> Vec<(String, crate::client::McpResourceInfo)> {
        let guard = self.servers.read().await;
        let mut out = Vec::new();
        for (name, entry) in guard.iter().filter_map(|(n, s)| s.live.as_ref().map(|e| (n, e))) {
            let resources = match self.until_shutdown(entry.client.list_resources()).await {
                Ok(resources) => resources,
                Err(error) => {
                    tracing::warn!(%error, "MCP resource listing interrupted");
                    break;
                }
            };
            for resource in resources {
                out.push((name.clone(), resource));
            }
        }
        out.sort_by(|a, b| (&a.0, &a.1.uri).cmp(&(&b.0, &b.1.uri)));
        out
    }

    /// Fetches one prompt's rendered text from a named server.
    pub async fn get_prompt(&self, server_name: &str, prompt_name: &str) -> Result<String, String> {
        let guard = self.servers.read().await;
        let Some(slot) = guard.get(server_name) else {
            return Err(Self::unknown_server(server_name, &guard));
        };
        let Some(entry) = slot.live.as_ref() else {
            return Err(Self::not_connected(server_name, slot));
        };
        self.until_shutdown(entry.client.get_prompt(prompt_name)).await?
    }

    /// Reads one resource from a named server.
    pub async fn read_resource(&self, server_name: &str, uri: &str) -> Result<String, String> {
        let guard = self.servers.read().await;
        let Some(slot) = guard.get(server_name) else {
            return Err(Self::unknown_server(server_name, &guard));
        };
        let Some(entry) = slot.live.as_ref() else {
            return Err(Self::not_connected(server_name, slot));
        };
        self.until_shutdown(entry.client.read_resource(uri)).await?
    }

    /// Restarts one server from its retained launch spec, whatever state it is
    /// in: connected, crashed, hung, failed at startup or already disconnected.
    ///
    /// The old process tree is terminated first, so a server holding an
    /// exclusive resource can rebind. How that went is reported, never fatal.
    /// A failed relaunch keeps the slot so the restart can be retried.
    pub async fn restart_server(&self, server_name: &str) -> Result<RestartReport, String> {
        let lock = {
            let mut locks = self.restart_locks.lock().unwrap_or_else(|e| e.into_inner());
            Arc::clone(locks.entry(server_name.to_string()).or_default())
        };
        let _restarting = lock.lock().await;

        let (old, spec) = {
            let mut guard = self.servers.write().await;
            let Some(slot) = guard.get_mut(server_name) else {
                return Err(Self::unknown_server(server_name, &guard));
            };
            (slot.live.take(), slot.spec.clone())
        };

        let old_process = match old {
            Some(entry) => {
                let outcome = OldProcessOutcome::from_report(
                    entry.terminate(tokio::time::Instant::now() + SHUTDOWN_GRACE).await,
                );
                match &outcome {
                    OldProcessOutcome::ForceKilled { .. }
                    | OldProcessOutcome::AlreadyExited(_)
                    | OldProcessOutcome::Unconfirmed(_) => {
                        tracing::warn!(server = %server_name, ?outcome, "old MCP server did not exit cleanly");
                    }
                    _ => {}
                }
                outcome
            }
            None => OldProcessOutcome::NotRunning,
        };

        let connect_timeout = DEFAULT_CONNECT_TIMEOUT;
        let tool_timeout = self.tool_timeout;
        let reconnected = match &spec {
            ServerSpec::Stdio(cfg) => {
                connect_one_stdio(cfg, connect_timeout, tool_timeout).await
            }
            ServerSpec::Http(cfg, store) => {
                connect_one_http(cfg, connect_timeout, tool_timeout, store.as_ref()).await
            }
        };

        let mut guard = self.servers.write().await;
        match reconnected {
            Ok(entry) => {
                let tool_count = entry.client.tools().len();
                if let Some(slot) = guard.get_mut(server_name) {
                    slot.tools = entry.client.tools().to_vec();
                    slot.live = Some(entry);
                    slot.last_error = None;
                    slot.generation += 1;
                }
                self.rebuild_snapshot(&guard);
                Ok(RestartReport { tool_count, old_process })
            }
            Err(e) => {
                if let Some(slot) = guard.get_mut(server_name) {
                    slot.last_error = Some(e.to_string());
                }
                Err(format!(
                    "failed to restart '{server_name}': {e}. The server stays configured; \
                     fix the cause and call restart_mcp_server again."
                ))
            }
        }
    }

    /// Gracefully shut down all servers.
    pub async fn shutdown(&self) -> Result<(), String> {
        let deadline = tokio::time::Instant::now() + SHUTDOWN_GRACE;
        self.stopping.cancel();
        let mut guard = self.servers.write().await;
        let entries: Vec<_> = guard
            .drain()
            .filter_map(|(name, slot)| slot.live.map(|entry| (name, entry)))
            .collect();
        self.rebuild_snapshot(&guard);
        drop(guard);

        let results =
            futures::future::join_all(entries.into_iter().map(|(name, entry)| async move {
                entry
                    .shutdown(deadline)
                    .await
                    .map_err(|error| format!("{name}: {error}"))
            }))
            .await;
        let errors: Vec<String> = results.into_iter().filter_map(Result::err).collect();
        if errors.is_empty() {
            Ok(())
        } else {
            Err(errors.join("; "))
        }
    }
}

struct McpToolSource {
    manager: Arc<McpClientManager>,
}

impl ToolSource for McpToolSource {
    fn tools(&self) -> Vec<Arc<dyn Tool>> {
        let snapshot: Vec<(String, McpToolInfo)> = self
            .manager
            .tool_snapshot
            .read()
            .unwrap_or_else(|e| e.into_inner())
            .clone();
        snapshot
            .into_iter()
            .map(|(server, info)| {
                Arc::new(McpTool::new(server, info, Arc::clone(&self.manager))) as Arc<dyn Tool>
            })
            .collect()
    }
}

impl Default for McpClientManager {
    fn default() -> Self {
        Self::new()
    }
}

async fn connect_one_stdio(
    config: &McpConnectable,
    connect_timeout: Duration,
    tool_timeout: Duration,
) -> Result<ServerEntry, McpError> {
    let (proc, stdin, stdout) = McpProcess::spawn(config)?;
    let client = McpClient::connect(stdout, stdin, connect_timeout, tool_timeout).await?;
    Ok(ServerEntry { client: AnyClient::Stdio(client), _process: Some(proc) })
}

/// A dead pipe, or an HTTP-style "session not found" answer, means the
/// connection cannot recover without a restart.
fn is_transport_loss(error: &crate::error::McpTransportError) -> bool {
    use crate::error::McpTransportError;
    match error {
        McpTransportError::ConnectionClosed => true,
        McpTransportError::Rpc(e) => {
            let message = e.message.to_ascii_lowercase();
            message.contains("session not found") || message.contains("connection closed before responding")
        }
        _ => false,
    }
}

async fn connect_one_http(
    config: &McpHttpConnectable,
    connect_timeout: Duration,
    tool_timeout: Duration,
    credential_store: Option<&Arc<dyn CredentialStore>>,
) -> Result<ServerEntry, McpError> {
    let auth: Option<Arc<dyn McpAuthProvider>> = match config.auth.mode {
        McpAuthMode::None => None,
        McpAuthMode::Bearer => {
            match config.auth.bearer_token.as_ref().filter(|t| !t.expose().is_empty()) {
                Some(token) => {
                    Some(Arc::new(StaticBearerAuthProvider::new(token.expose().clone())))
                }
                None => {
                    return Err(McpError::Http(
                        "auth.mode is 'bearer' but no token is configured or the token \
                         resolved to an empty value; check the auth.token field or the \
                         credential store entry it references"
                            .into(),
                    ));
                }
            }
        }
        McpAuthMode::OAuth => {
            match credential_store {
                Some(store) => {
                    // Non-interactive: use cached tokens from the credential store.
                    // A 401 that cannot be resolved silently fails without opening a
                    // browser; the user must authenticate via /mcp or `coda auth`.
                    match reqwest::Url::parse(&config.url) {
                        Ok(url) => {
                            let http = reqwest::Client::new();
                            let provider = McpOAuthProvider::new(
                                http,
                                &url,
                                Arc::clone(store),
                                config.auth.clone(),
                                false, // headless: no browser
                                None,
                                None,
                            );
                            Some(Arc::new(provider) as Arc<dyn McpAuthProvider>)
                        }
                        Err(_) => {
                            // URL will be validated (and will fail) in McpHttpClient::connect;
                            // skip building the provider rather than duplicating the error.
                            None
                        }
                    }
                }
                None => {
                    return Err(McpError::Http(
                        "auth.mode is 'oauth' but no credential store is available; \
                         authenticate via `coda auth` or the /mcp commands first, then \
                         restart the engine"
                            .into(),
                    ));
                }
            }
        }
    };

    let client = McpHttpClient::connect(
        &config.name,
        &config.url,
        &config.headers,
        auth,
        connect_timeout,
        tool_timeout,
    )
    .await?;
    Ok(ServerEntry { client: AnyClient::Http(client), _process: None })
}

// ── Test support (hermetic in-memory fake server) ────────────────────────────

/// In-memory fake MCP server for hermetic integration tests, including in
/// downstream crates via the `test-support` feature.
///
/// No process is spawned and no network is used: a fake server task is wired to
/// a real [`McpClient`] over in-memory duplex pipes, so the full transport,
/// handshake, `tools/list` and `tools/call` paths are exercised for real.
#[cfg(any(test, feature = "test-support"))]
pub mod test_support {
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::Arc;
    use std::time::Duration;

    use serde_json::{json, Value};
    use tokio::io::{
        duplex, AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt,
    };

    use super::{AnyClient, McpClientManager, ServerEntry, ServerSpec};
    use crate::client::McpClient;
    use crate::config::McpConnectable;

    /// Handle to a running fake server, reporting how many `tools/call`
    /// requests actually reached it. Used to prove that a denied tool never
    /// causes a remote call.
    #[derive(Clone)]
    pub struct FakeServerHandle {
        call_count: Arc<AtomicUsize>,
    }

    impl FakeServerHandle {
        /// The number of `tools/call` requests the fake server has received.
        pub fn call_count(&self) -> usize {
            self.call_count.load(Ordering::SeqCst)
        }
    }

    /// Connect a fake in-memory stdio MCP server to `manager` under
    /// `server_name`, advertising `tools` (a JSON array in MCP `tools/list`
    /// shape). Every `tools/call` returns `result_text` and increments the
    /// returned handle's counter.
    pub async fn connect_fake_server(
        manager: &Arc<McpClientManager>,
        server_name: &str,
        tools: Value,
        result_text: impl Into<String>,
    ) -> FakeServerHandle {
        let call_count = Arc::new(AtomicUsize::new(0));
        let result_text = result_text.into();

        let (server_out, client_in) = duplex(64 * 1024);
        let (client_out, server_in) = duplex(64 * 1024);

        let counter = Arc::clone(&call_count);
        tokio::spawn(async move {
            fake_server_loop(server_in, server_out, tools, result_text, counter).await;
        });

        let client = McpClient::connect(
            client_in,
            client_out,
            Duration::from_secs(5),
            Duration::from_secs(5),
        )
        .await
        .expect("fake MCP server handshake must succeed");

        let mut guard = manager.servers.write().await;
        McpClientManager::record_connected(
            &mut guard,
            server_name.to_string(),
            ServerSpec::Stdio(Box::new(McpConnectable {
                name: server_name.to_string(),
                command: "fake-in-memory".into(),
                args: Vec::new(),
                env: Default::default(),
            })),
            ServerEntry { client: AnyClient::Stdio(client), _process: None },
        );
        manager.rebuild_snapshot(&guard);

        FakeServerHandle { call_count }
    }

    async fn fake_server_loop<R, W>(
        mut reader: R,
        mut writer: W,
        tools: Value,
        result_text: String,
        counter: Arc<AtomicUsize>,
    ) where
        R: AsyncRead + Unpin,
        W: AsyncWrite + Unpin,
    {
        while let Some(msg) = read_line(&mut reader).await {
            let method = msg.get("method").and_then(Value::as_str).unwrap_or("");
            let id = msg.get("id").cloned();
            match method {
                "initialize" => {
                    write_line(
                        &mut writer,
                        json!({
                            "jsonrpc": "2.0",
                            "id": id,
                            "result": {
                                "protocolVersion": "2025-06-18",
                                "serverInfo": { "name": "fake", "version": "1" },
                                "capabilities": {}
                            }
                        }),
                    )
                    .await;
                }
                // Client notification; no response expected.
                "notifications/initialized" => {}
                "tools/list" => {
                    write_line(
                        &mut writer,
                        json!({ "jsonrpc": "2.0", "id": id, "result": { "tools": tools } }),
                    )
                    .await;
                }
                "tools/call" => {
                    counter.fetch_add(1, Ordering::SeqCst);
                    write_line(
                        &mut writer,
                        json!({
                            "jsonrpc": "2.0",
                            "id": id,
                            "result": {
                                "content": [{ "type": "text", "text": result_text }],
                                "isError": false
                            }
                        }),
                    )
                    .await;
                }
                "shutdown" => {
                    if id.is_some() {
                        write_line(
                            &mut writer,
                            json!({ "jsonrpc": "2.0", "id": id, "result": {} }),
                        )
                        .await;
                    }
                    break;
                }
                _ => {
                    // Answer any other request so the client never blocks.
                    if id.is_some() {
                        write_line(
                            &mut writer,
                            json!({ "jsonrpc": "2.0", "id": id, "result": {} }),
                        )
                        .await;
                    }
                }
            }
        }
    }

    /// Read one newline-delimited JSON value. `None` on EOF.
    async fn read_line<R: AsyncRead + Unpin>(reader: &mut R) -> Option<Value> {
        let mut line = Vec::new();
        let mut byte = [0u8; 1];
        loop {
            match reader.read_exact(&mut byte).await {
                Ok(_) => {
                    if byte[0] == b'\n' {
                        break;
                    }
                    line.push(byte[0]);
                }
                Err(_) => return None, // EOF or closed pipe
            }
        }
        serde_json::from_slice::<Value>(line.trim_ascii()).ok()
    }

    /// Write one newline-delimited JSON value.
    async fn write_line<W: AsyncWrite + Unpin>(writer: &mut W, value: Value) {
        let s = serde_json::to_string(&value).expect("serialise fake response");
        let _ = writer.write_all(format!("{s}\n").as_bytes()).await;
        let _ = writer.flush().await;
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::time::Duration;
    use tokio::io::{duplex, AsyncWriteExt};

    use crate::client::McpClient;
    use crate::config::McpConnectable;
    use crate::test_fixture::{fake_server_connectable, is_process_alive, wait_until, PID_FILE_ENV};

    /// Injects a pre-connected `McpClient` (built from in-memory streams)
    /// directly into the manager, bypassing the process layer. Used in unit
    /// tests that do not want to spawn real processes.
    async fn inject_client(
        manager: &McpClientManager,
        server_name: &str,
        client: McpClient,
    ) {
        let mut guard = manager.servers.write().await;
        McpClientManager::record_connected(
            &mut guard,
            server_name.to_string(),
            // An injected client has no real launch spec; a restart of it
            // would legitimately fail unless a test replaces the spec.
            ServerSpec::Stdio(Box::new(McpConnectable {
                name: server_name.to_string(),
                command: "does-not-exist".into(),
                args: Vec::new(),
                env: Default::default(),
            })),
            ServerEntry { client: AnyClient::Stdio(client), _process: None },
        );
        manager.rebuild_snapshot(&guard);
    }

    /// Builds a minimal McpClient from in-memory streams with the given tools.
    async fn make_client(tools: serde_json::Value) -> McpClient {
        make_client_with_streams(tools).await.0
    }

    async fn make_client_with_streams(
        tools: serde_json::Value,
    ) -> (McpClient, tokio::io::DuplexStream, tokio::io::DuplexStream) {
        let (server_out, client_in) = duplex(64 * 1024);
        let (client_out, server_in) = duplex(64 * 1024);

        let connect = tokio::spawn(async move {
            McpClient::connect(client_in, client_out, Duration::from_secs(5), Duration::from_secs(5)).await
        });

        // Run handshake from the server side.
        let mut srv_out = server_out;
        let mut srv_in = server_in;

        macro_rules! read_line {
            ($stream:ident) => {{
                use tokio::io::AsyncReadExt;
                let mut line = Vec::new();
                let mut byte = [0u8; 1];
                loop {
                    $stream.read_exact(&mut byte).await.unwrap();
                    if byte[0] == b'\n' { break; }
                    line.push(byte[0]);
                }
                serde_json::from_slice::<serde_json::Value>(line.trim_ascii()).unwrap()
            }};
        }

        macro_rules! write_line {
            ($stream:ident, $val:expr) => {{
                let s = serde_json::to_string(&$val).unwrap();
                $stream.write_all(format!("{s}\n").as_bytes()).await.unwrap();
                $stream.flush().await.unwrap();
            }};
        }

        let init = read_line!(srv_in);
        write_line!(
            srv_out,
            json!({
                "jsonrpc": "2.0",
                "id": init["id"],
                "result": {
                    "protocolVersion": "2025-06-18",
                    "serverInfo": { "name": "test", "version": "1" },
                    "capabilities": {}
                }
            })
        );

        let _notif = read_line!(srv_in); // notifications/initialized

        let tl = read_line!(srv_in);
        write_line!(srv_out, json!({ "jsonrpc": "2.0", "id": tl["id"], "result": { "tools": tools } }));

        (connect.await.unwrap().unwrap(), srv_in, srv_out)
    }

    #[tokio::test]
    async fn shutdown_closes_every_server_before_waiting_for_any_exit() {
        use tokio::io::AsyncReadExt;
        let manager = McpClientManager::new();
        let barrier = Arc::new(tokio::sync::Barrier::new(9));
        let mut servers = Vec::new();
        for i in 0..9 {
            let (client, mut input, mut output) = make_client_with_streams(json!([])).await;
            inject_client(&manager, &format!("server-{i}"), client).await;
            let barrier = barrier.clone();
            servers.push(tokio::spawn(async move {
                input.read_to_end(&mut Vec::new()).await.unwrap();
                barrier.wait().await;
                output
                    .write_all(b"{\"jsonrpc\":\"2.0\",\"method\":\"notifications/done\"}\n")
                    .await
                    .expect("stdout must remain open through EOF cleanup");
            }));
        }
        tokio::time::timeout(Duration::from_millis(500), async {
            let (result, servers) =
                tokio::join!(manager.shutdown(), futures::future::join_all(servers),);
            result.expect("all servers must receive the same graceful shutdown window");
            for server in servers {
                server.expect("server cleanup");
            }
        })
        .await
        .expect("EOF delivery must not queue behind other servers' exits");
    }

    #[tokio::test]
    async fn shutdown_cancels_requests_holding_the_server_registry_lock() {
        use tokio::io::{AsyncBufReadExt, AsyncReadExt, BufReader};
        let manager = Arc::new(McpClientManager::new());
        let (client, input, output) = make_client_with_streams(json!([])).await;
        inject_client(&manager, "server", client).await;
        let caller = manager.clone();
        let request = tokio::spawn(async move { caller.get_prompt("server", "pending").await });
        let mut input = BufReader::new(input);
        let mut line = String::new();
        input.read_line(&mut line).await.unwrap();
        assert!(line.contains("prompts/get"));
        let server = async move {
            input.read_to_end(&mut Vec::new()).await.unwrap();
            drop(output);
        };
        tokio::time::timeout(Duration::from_millis(500), async {
            let (result, ()) = tokio::join!(manager.shutdown(), server);
            result.expect("active calls must release the registry before process cleanup");
            let error = request.await.unwrap().unwrap_err();
            assert!(error.contains("shutting down"), "{error}");
        }).await.expect("a slow MCP call must not delay stdin EOF");
    }

    #[tokio::test]
    async fn restart_waits_for_old_process_cleanup_before_reconnecting() {
        let marker =
            std::env::temp_dir().join(format!("coda-mcp-restart-{}", uuid::Uuid::new_v4()));
        let (process, stdin, stdout) = if cfg!(windows) {
            let script = format!(
                "[IO.File]::WriteAllText('{}', 'running'); $null = [Console]::In.ReadToEnd(); \
                 Start-Sleep -Milliseconds 200; [IO.File]::Delete('{}')",
                marker.display(),
                marker.display(),
            );
            McpProcess::spawn_raw(
                "powershell.exe",
                vec![
                    "-NoProfile".to_string(),
                    "-NonInteractive".into(),
                    "-Command".into(),
                    script,
                ],
            )
        } else {
            McpProcess::spawn_raw(
                "sh",
                vec![
                    "-c".to_string(),
                    "touch \"$1\"; cat >/dev/null; sleep 0.2; rm \"$1\"".into(),
                    "fixture".into(),
                    marker.to_string_lossy().into_owned(),
                ],
            )
        }
        .expect("spawn old server");
        tokio::time::timeout(Duration::from_secs(3), async {
            while !marker.exists() {
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        })
        .await
        .expect("old server starts");
        let manager = McpClientManager::new();
        inject_client(&manager, "old", make_client(json!([])).await).await;
        manager
            .servers
            .write()
            .await
            .get_mut("old")
            .unwrap()
            .live
            .as_mut()
            .unwrap()
            ._process = Some(process);
        drop(stdin);
        drop(stdout);
        manager.servers.write().await.get_mut("old").unwrap().spec =
            ServerSpec::Stdio(Box::new(fake_server_connectable("old", "serve", &[])));
        restart_ok(&manager, "old").await;
        let cleaned_up = !marker.exists();
        if !cleaned_up {
            std::fs::remove_file(&marker).unwrap();
        }
        assert!(
            cleaned_up,
            "replacement started before old process released its resource"
        );
    }

    async fn restart_ok(manager: &McpClientManager, name: &str) -> RestartReport {
        tokio::time::timeout(Duration::from_secs(30), manager.restart_server(name))
            .await
            .expect("restart must not hang")
            .expect("restart must succeed")
    }

    async fn connect_fake(manager: &McpClientManager, name: &str, mode: &str, env: &[(&str, &str)]) {
        let errors = manager
            .connect_many(vec![fake_server_connectable(name, mode, env)], Some(Duration::from_secs(10)))
            .await;
        assert!(errors.is_empty(), "{errors:?}");
    }

    fn pid_of(text: &str) -> u32 {
        text.strip_prefix("pid:").expect("pid reply").parse().expect("numeric pid")
    }

    async fn echo_pid(manager: &McpClientManager, name: &str) -> u32 {
        let (text, is_error) = tokio::time::timeout(
            Duration::from_secs(10),
            manager.call_tool(name, "echo", &json!({})),
        )
        .await
        .expect("call must not hang")
        .expect("call must succeed");
        assert!(!is_error);
        pid_of(&text)
    }

    async fn set_command(manager: &McpClientManager, name: &str, command: &str) -> String {
        let mut guard = manager.servers.write().await;
        let ServerSpec::Stdio(cfg) = &mut guard.get_mut(name).unwrap().spec else {
            panic!("stdio spec expected");
        };
        std::mem::replace(&mut cfg.command, command.to_string())
    }

    const MISSING: &str = "definitely-not-a-real-mcp-executable-xyz";

    #[tokio::test]
    async fn restart_force_kills_a_hung_server_and_reconnects() {
        let manager = McpClientManager::new();
        connect_fake(&manager, "s", "hang", &[]).await;
        let old_pid = echo_pid(&manager, "s").await;
        let report = restart_ok(&manager, "s").await;
        assert!(matches!(report.old_process, OldProcessOutcome::ForceKilled { .. }), "{report:?}");
        assert!(report.summary("s").contains("force-killed"));
        assert!(wait_until(Duration::from_secs(5), || !is_process_alive(old_pid)).await);
        let new_pid = echo_pid(&manager, "s").await;
        assert_ne!(old_pid, new_pid);
    }

    #[tokio::test]
    async fn restart_kills_the_old_grandchild() {
        let pid_file = std::env::temp_dir().join(format!("coda-mcp-gc-{}", uuid::Uuid::new_v4()));
        let manager = McpClientManager::new();
        connect_fake(&manager, "s", "grandchild", &[(PID_FILE_ENV, &pid_file.to_string_lossy())]).await;
        assert!(wait_until(Duration::from_secs(10), || pid_file.exists()).await);
        let grandchild: u32 = std::fs::read_to_string(&pid_file).unwrap().trim().parse().unwrap();
        std::fs::remove_file(&pid_file).unwrap();
        assert!(is_process_alive(grandchild));
        restart_ok(&manager, "s").await;
        let gone = wait_until(Duration::from_secs(5), || !is_process_alive(grandchild)).await;
        if let Ok(new) = std::fs::read_to_string(&pid_file) {
            if let Ok(pid) = new.trim().parse::<u32>() {
                let _ = std::process::Command::new(if cfg!(windows) { "taskkill" } else { "kill" })
                    .args(if cfg!(windows) { vec!["/F".to_string(), "/PID".into(), pid.to_string()] } else { vec![pid.to_string()] })
                    .output();
            }
        }
        manager.shutdown().await.ok();
        assert!(gone, "old grandchild survived the restart");
    }

    #[tokio::test]
    async fn restart_of_a_crashed_server_succeeds() {
        let manager = McpClientManager::new();
        connect_fake(&manager, "s", "crash", &[]).await;
        restart_ok(&manager, "s").await;
        assert_eq!(manager.connected_status().await.len(), 1);
    }

    #[tokio::test]
    async fn a_failed_reconnect_keeps_the_server_restartable() {
        let manager = McpClientManager::new();
        connect_fake(&manager, "s", "serve", &[]).await;
        let real = set_command(&manager, "s", MISSING).await;
        let error = tokio::time::timeout(Duration::from_secs(30), manager.restart_server("s"))
            .await
            .unwrap()
            .unwrap_err();
        assert!(error.contains("restart_mcp_server again"), "{error}");
        let faults = manager.server_faults().await;
        assert_eq!(faults.len(), 1);
        assert_eq!(faults[0].name, "s");
        assert!(manager.connected_status().await.is_empty());
        set_command(&manager, "s", &real).await;
        restart_ok(&manager, "s").await;
        assert!(manager.server_faults().await.is_empty());
        echo_pid(&manager, "s").await;
    }

    #[tokio::test]
    async fn restart_twice_in_a_row_succeeds() {
        let manager = McpClientManager::new();
        connect_fake(&manager, "s", "serve", &[]).await;
        restart_ok(&manager, "s").await;
        restart_ok(&manager, "s").await;
        echo_pid(&manager, "s").await;
    }

    #[tokio::test]
    async fn a_server_that_failed_at_startup_can_be_started_by_restart() {
        let manager = Arc::new(McpClientManager::new());
        let mut cfg = fake_server_connectable("s", "serve", &[]);
        let real = std::mem::replace(&mut cfg.command, MISSING.to_string());
        let errors = manager.connect_many(vec![cfg], Some(Duration::from_secs(5))).await;
        assert_eq!(errors.len(), 1);
        let faults = manager.server_faults().await;
        assert_eq!(faults.iter().map(|f| f.name.as_str()).collect::<Vec<_>>(), ["s"]);
        assert!(manager.connected_status().await.is_empty());

        set_command(&manager, "s", &real).await;
        let report = restart_ok(&manager, "s").await;
        assert_eq!(report.old_process, OldProcessOutcome::NotRunning);
        assert_eq!(report.tool_count, 1);
        assert_eq!(manager.connected_status().await[0].name, "s");
        assert_eq!(manager.tools().await.len(), 1);
    }

    #[tokio::test]
    async fn restart_refreshes_the_tool_list() {
        let manager = Arc::new(McpClientManager::new());
        connect_fake(&manager, "s", "serve", &[]).await;
        assert_eq!(manager.tools().await.len(), 1);
        manager.servers.write().await.get_mut("s").unwrap().spec =
            ServerSpec::Stdio(Box::new(fake_server_connectable("s", "tools2", &[])));
        assert_eq!(restart_ok(&manager, "s").await.tool_count, 2);
        assert_eq!(manager.tools().await.len(), 2);
    }

    #[tokio::test]
    async fn restarting_an_unknown_server_lists_configured_names() {
        let manager = McpClientManager::new();
        let error = manager.restart_server("nope").await.unwrap_err();
        assert!(error.contains("no MCP servers are configured"), "{error}");
        inject_client(&manager, "known", make_client(json!([])).await).await;
        let error = manager.restart_server("nope").await.unwrap_err();
        assert!(error.contains("unknown MCP server 'nope'"), "{error}");
        assert!(error.contains("known"), "{error}");
    }

    #[tokio::test]
    async fn a_lost_connection_marks_the_server_disconnected() {
        let manager = McpClientManager::new();
        connect_fake(&manager, "s", "serve", &[]).await;
        {
            let mut guard = manager.servers.write().await;
            let live = guard.get_mut("s").unwrap().live.as_mut().unwrap();
            live._process.as_mut().unwrap().child.start_kill().unwrap();
            let _ = tokio::time::timeout(Duration::from_secs(5), live._process.as_mut().unwrap().child.wait()).await;
        }
        let error = tokio::time::timeout(Duration::from_secs(10), manager.call_tool("s", "echo", &json!({})))
            .await
            .unwrap()
            .unwrap_err();
        assert!(error.contains("restart_mcp_server"), "{error}");
        assert_eq!(manager.server_faults().await[0].name, "s");
        assert!(manager.connected_status().await.is_empty());
        let error = manager.call_tool("s", "echo", &json!({})).await.unwrap_err();
        assert!(error.contains("is not connected (last error:"), "{error}");
        restart_ok(&manager, "s").await;
        echo_pid(&manager, "s").await;
    }

    #[tokio::test]
    async fn the_tool_source_reflects_a_restart_with_a_new_tool_list() {
        let manager = Arc::new(McpClientManager::new());
        let errors = manager
            .connect_many(vec![fake_server_connectable("s", "serve", &[])], Some(Duration::from_secs(10)))
            .await;
        assert!(errors.is_empty(), "{errors:?}");
        let source = manager.tool_source();
        let names = |s: &Arc<dyn ToolSource>| -> Vec<String> {
            s.tools().iter().map(|t| t.name().to_owned()).collect()
        };
        assert_eq!(names(&source), [crate::tool::namespaced_name("s", "echo")]);

        manager.servers.write().await.get_mut("s").unwrap().spec =
            ServerSpec::Stdio(Box::new(fake_server_connectable("s", "tools2", &[])));
        restart_ok(&manager, "s").await;
        let after = names(&source);
        assert!(after.contains(&crate::tool::namespaced_name("s", "extra")), "{after:?}");
    }

    #[tokio::test]
    async fn a_disconnected_server_keeps_its_tools_routable() {
        let manager = Arc::new(McpClientManager::new());
        let errors = manager
            .connect_many(vec![fake_server_connectable("s", "serve", &[])], Some(Duration::from_secs(10)))
            .await;
        assert!(errors.is_empty(), "{errors:?}");
        {
            let mut guard = manager.servers.write().await;
            let live = guard.get_mut("s").unwrap().live.as_mut().unwrap();
            live._process.as_mut().unwrap().child.start_kill().unwrap();
            let _ = tokio::time::timeout(Duration::from_secs(5), live._process.as_mut().unwrap().child.wait()).await;
        }
        let _ = tokio::time::timeout(Duration::from_secs(10), manager.call_tool("s", "echo", &json!({}))).await;
        assert!(manager.connected_status().await.is_empty());

        let tools = manager.tool_source().tools();
        assert_eq!(tools.len(), 1);
        let outcome = tools[0]
            .execute(&json!({}), &coda_tool::ToolContext::new("."), CancellationToken::new())
            .await;
        assert!(outcome.is_error);
        assert!(outcome.content.contains("restart_mcp_server"), "{}", outcome.content);
    }

    #[tokio::test]
    async fn tools_returns_namespaced_arc_tools() {
        let manager = Arc::new(McpClientManager::new());
        let client = make_client(json!([{
            "name": "my_tool",
            "description": "desc",
            "inputSchema": { "type": "object", "properties": {} }
        }]))
        .await;

        inject_client(&manager, "my-server", client).await;

        let tools = manager.tools().await;
        assert_eq!(tools.len(), 1);
        assert!(tools[0].name().starts_with("mcp__"), "tool name must be namespaced");
    }

    #[tokio::test]
    async fn call_tool_on_missing_server_returns_error() {
        let manager = McpClientManager::new();
        let result = manager.call_tool("no-such-server", "tool", &json!({})).await;
        assert!(result.is_err());
        let msg = result.unwrap_err();
        assert!(msg.contains("no-such-server"));
    }

    #[tokio::test]
    async fn one_server_failing_does_not_block_others() {
        // connect_many with two servers: one valid McpConnectable (bad command)
        // and we inject a good one directly. The bad one fails; the good one stays.
        let manager = Arc::new(McpClientManager::new());

        let bad = McpConnectable {
            name: "bad-server".into(),
            command: "definitely-not-a-real-mcp-executable-xyz".into(),
            args: vec![],
            env: Default::default(),
        };

        // inject a good client first
        let client = make_client(json!([{ "name": "ok_tool", "description": "", "inputSchema": null }])).await;
        inject_client(&manager, "good-server", client).await;

        // Now try to connect the bad server; it should fail.
        let errors = manager.connect_many(vec![bad], Some(Duration::from_millis(500))).await;
        assert_eq!(errors.len(), 1, "bad server must report an error");
        assert_eq!(errors[0].server_name, "bad-server");

        // The good server is still there.
        let tools = manager.tools().await;
        assert_eq!(tools.len(), 1);
        assert!(tools[0].name().contains("good-server") || tools[0].name().contains("ok_tool"));
    }

    #[tokio::test]
    async fn namespacing_makes_names_collision_free() {
        let manager = Arc::new(McpClientManager::new());

        // Two servers with the same tool name.
        let client_a = make_client(json!([{
            "name": "shared_tool",
            "description": "from a",
            "inputSchema": null
        }]))
        .await;
        let client_b = make_client(json!([{
            "name": "shared_tool",
            "description": "from b",
            "inputSchema": null
        }]))
        .await;

        inject_client(&manager, "server-a", client_a).await;
        inject_client(&manager, "server-b", client_b).await;

        let tools = manager.tools().await;
        assert_eq!(tools.len(), 2, "both tools must be present");

        let names: Vec<&str> = tools.iter().map(|t| t.name()).collect();
        // Names must be distinct despite sharing the underlying tool name.
        assert_ne!(names[0], names[1]);
        // Both must be namespaced.
        assert!(names.iter().all(|n| n.starts_with("mcp__")));
    }

    #[tokio::test]
    async fn empty_config_yields_no_errors_and_no_tools() {
        let manager = Arc::new(McpClientManager::new());
        let errors = manager.connect_many(vec![], None).await;
        assert!(errors.is_empty());
        assert!(manager.tools().await.is_empty());
    }
}
