//! Reading `.mcp.json` on this machine.
//!
//! This is the one place in the terminal front-end that parses an
//! engine-adjacent configuration file, and it lives under `src/local/`
//! because that is what it is: local maintenance of files that only matter
//! when this client started the core itself.
//!
//! The *display* path for an ordinary session is `mcp/list` — the engine's own
//! read-only, secret-free inventory, which also knows the runtime status this
//! file cannot. This reader exists for the editor: to edit a server you have
//! to see what is actually written, including the unresolved
//! `coda-secret:store/key` references the engine deliberately never returns.
//!
//! `coda_mcp::config` is used only for the file *format*, so one JSON schema
//! has one parser. It executes nothing and starts no server; the guard in
//! `tests/conventions.rs` scopes that dependency to this directory.

use crate::config::{ConfigError, McpServer, Paths, Scope};

/// Reads MCP servers from the project and user configuration files.
///
/// Project definitions shadow user definitions of the same name, which is the
/// same precedence the engine applies.
pub fn load_mcp_servers(paths: &Paths) -> Result<Vec<McpServer>, ConfigError> {
    let raw = coda_mcp::config::load_all(&paths.user_mcp(), &paths.project_mcp());
    let mut servers: Vec<McpServer> = raw.into_iter().map(raw_to_display).collect();
    servers.sort_by(|a, b| a.name.cmp(&b.name));
    Ok(servers)
}

fn raw_to_display(raw: coda_mcp::config::McpRawServer) -> McpServer {
    let scope = match raw.scope {
        coda_mcp::config::McpScope::Project => Scope::Project,
        coda_mcp::config::McpScope::User => Scope::User,
    };
    let transport = raw.transport(); // call before moving fields
    let mut env_pairs: Vec<(String, String)> = raw.env.into_iter().collect();
    env_pairs.sort_by(|a, b| a.0.cmp(&b.0)); // stable order
    let env_keys: Vec<String> = env_pairs.iter().map(|(k, _)| k.clone()).collect();
    McpServer {
        name: raw.name,
        scope,
        transport,
        command: raw.command,
        args: raw.args,
        url: raw.url,
        enabled: !raw.disabled,
        env_raw: env_pairs,
        env_keys,
    }
}
