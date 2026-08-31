//! The MCP prompt and resource tools, plus server restart.
//!
//! Mirrors the C# `ListMcpPromptsTool`, `GetMcpPromptTool`,
//! `ListMcpResourcesTool`, `ReadMcpResourceTool` and `RestartMcpServerTool`.
//! The transport and manager were ported first; these are the model-facing
//! surface, without which MCP prompts and resources are unreachable however
//! well the transport works.

use std::sync::Arc;

use async_trait::async_trait;
use coda_tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::manager::McpClientManager;

/// Optional `server` filter, shared by the two listing tools.
const LIST_SCHEMA: &str = r#"{
  "type": "object",
  "properties": {
    "server": {
      "type": "string",
      "description": "Optional name of a specific MCP server. Omit to list from all servers."
    }
  }
}"#;

fn server_filter(input: &Value) -> Option<&str> {
    input.get("server").and_then(Value::as_str).filter(|s| !s.is_empty())
}

/// Reads a required string argument, or returns the C#'s error wording.
fn required<'a>(input: &'a Value, key: &str) -> Result<&'a str, ToolResult> {
    match input.get(key).and_then(Value::as_str).filter(|s| !s.is_empty()) {
        Some(v) => Ok(v),
        None => Err(ToolResult::error(format!("Missing required argument: {key}"))),
    }
}

// ── list_mcp_prompts ─────────────────────────────────────────────────────────

pub struct ListMcpPromptsTool {
    manager: Arc<McpClientManager>,
}

impl ListMcpPromptsTool {
    pub fn new(manager: Arc<McpClientManager>) -> Self {
        Self { manager }
    }
}

#[async_trait]
impl Tool for ListMcpPromptsTool {
    fn name(&self) -> &str {
        "list_mcp_prompts"
    }
    fn description(&self) -> &str {
        "List prompts available from connected MCP servers."
    }
    fn input_schema_json(&self) -> &str {
        LIST_SCHEMA
    }
    /// Listing is read-only, but note this is *our* tool describing itself —
    /// unlike `McpTool`, whose read-only claim comes from the remote server and
    /// is therefore never trusted.
    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(&self, input: &Value, _ctx: &ToolContext, _c: CancellationToken) -> ToolOutcome {
        let filter = server_filter(input);
        let prompts = self.manager.list_prompts().await;
        let mut lines = Vec::new();
        for (server, prompt) in prompts {
            if filter.is_some_and(|f| f != server) {
                continue;
            }
            if prompt.description.is_empty() {
                lines.push(format!("{server}: {}", prompt.name));
            } else {
                lines.push(format!("{server}: {} — {}", prompt.name, prompt.description));
            }
        }
        if lines.is_empty() {
            return ToolResult::ok("No MCP prompts available.");
        }
        ToolResult::ok(lines.join("\n"))
    }
}

// ── get_mcp_prompt ───────────────────────────────────────────────────────────

pub struct GetMcpPromptTool {
    manager: Arc<McpClientManager>,
}

impl GetMcpPromptTool {
    pub fn new(manager: Arc<McpClientManager>) -> Self {
        Self { manager }
    }
}

const GET_PROMPT_SCHEMA: &str = r#"{
  "type": "object",
  "properties": {
    "server": { "type": "string", "description": "Name of the MCP server" },
    "name":   { "type": "string", "description": "Name of the prompt to fetch" }
  },
  "required": ["server", "name"]
}"#;

#[async_trait]
impl Tool for GetMcpPromptTool {
    fn name(&self) -> &str {
        "get_mcp_prompt"
    }
    fn description(&self) -> &str {
        "Get the rendered text of a prompt from a connected MCP server."
    }
    fn input_schema_json(&self) -> &str {
        GET_PROMPT_SCHEMA
    }
    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(&self, input: &Value, _ctx: &ToolContext, _c: CancellationToken) -> ToolOutcome {
        let server = match required(input, "server") {
            Ok(v) => v,
            Err(e) => return e,
        };
        let name = match required(input, "name") {
            Ok(v) => v,
            Err(e) => return e,
        };
        match self.manager.get_prompt(server, name).await {
            Ok(text) => ToolResult::ok(text),
            Err(e) => ToolResult::error(format!("MCP prompt error: {e}")),
        }
    }
}

// ── list_mcp_resources ───────────────────────────────────────────────────────

pub struct ListMcpResourcesTool {
    manager: Arc<McpClientManager>,
}

impl ListMcpResourcesTool {
    pub fn new(manager: Arc<McpClientManager>) -> Self {
        Self { manager }
    }
}

#[async_trait]
impl Tool for ListMcpResourcesTool {
    fn name(&self) -> &str {
        "list_mcp_resources"
    }
    fn description(&self) -> &str {
        "List resources available from connected MCP servers."
    }
    fn input_schema_json(&self) -> &str {
        LIST_SCHEMA
    }
    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(&self, input: &Value, _ctx: &ToolContext, _c: CancellationToken) -> ToolOutcome {
        let filter = server_filter(input);
        let resources = self.manager.list_resources().await;
        let mut lines = Vec::new();
        for (server, resource) in resources {
            if filter.is_some_and(|f| f != server) {
                continue;
            }
            let label = if resource.name.is_empty() { &resource.uri } else { &resource.name };
            if resource.description.is_empty() {
                lines.push(format!("{server}: {label} ({})", resource.uri));
            } else {
                lines.push(format!(
                    "{server}: {label} ({}) — {}",
                    resource.uri, resource.description
                ));
            }
        }
        if lines.is_empty() {
            return ToolResult::ok("No MCP resources available.");
        }
        ToolResult::ok(lines.join("\n"))
    }
}

// ── read_mcp_resource ────────────────────────────────────────────────────────

pub struct ReadMcpResourceTool {
    manager: Arc<McpClientManager>,
}

impl ReadMcpResourceTool {
    pub fn new(manager: Arc<McpClientManager>) -> Self {
        Self { manager }
    }
}

const READ_RESOURCE_SCHEMA: &str = r#"{
  "type": "object",
  "properties": {
    "server": { "type": "string", "description": "Name of the MCP server" },
    "uri":    { "type": "string", "description": "URI of the resource to read" }
  },
  "required": ["server", "uri"]
}"#;

#[async_trait]
impl Tool for ReadMcpResourceTool {
    fn name(&self) -> &str {
        "read_mcp_resource"
    }
    fn description(&self) -> &str {
        "Read the content of a resource from a connected MCP server."
    }
    fn input_schema_json(&self) -> &str {
        READ_RESOURCE_SCHEMA
    }
    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(&self, input: &Value, _ctx: &ToolContext, _c: CancellationToken) -> ToolOutcome {
        let server = match required(input, "server") {
            Ok(v) => v,
            Err(e) => return e,
        };
        let uri = match required(input, "uri") {
            Ok(v) => v,
            Err(e) => return e,
        };
        match self.manager.read_resource(server, uri).await {
            Ok(text) => ToolResult::ok(text),
            Err(e) => ToolResult::error(format!("MCP resource error: {e}")),
        }
    }
}

// ── restart_mcp_server ───────────────────────────────────────────────────────

pub struct RestartMcpServerTool {
    manager: Arc<McpClientManager>,
}

impl RestartMcpServerTool {
    pub fn new(manager: Arc<McpClientManager>) -> Self {
        Self { manager }
    }
}

const RESTART_SCHEMA: &str = r#"{
  "type": "object",
  "properties": {
    "server": { "type": "string", "description": "Name of the MCP server to restart" }
  },
  "required": ["server"]
}"#;

#[async_trait]
impl Tool for RestartMcpServerTool {
    fn name(&self) -> &str {
        "restart_mcp_server"
    }
    fn description(&self) -> &str {
        "Restart a connected MCP server, reconnecting and refreshing its tool list."
    }
    fn input_schema_json(&self) -> &str {
        RESTART_SCHEMA
    }
    /// Restarting launches a process, so it is emphatically not read-only —
    /// it must take the normal permission decision.
    fn is_read_only(&self) -> bool {
        false
    }

    async fn execute(&self, input: &Value, _ctx: &ToolContext, _c: CancellationToken) -> ToolOutcome {
        let server = match required(input, "server") {
            Ok(v) => v,
            Err(e) => return e,
        };
        match self.manager.restart_server(server).await {
            Ok(tool_count) => ToolResult::ok(format!(
                "Restarted MCP server '{server}'. {tool_count} tool(s) available."
            )),
            Err(e) => ToolResult::error(format!("MCP restart error: {e}")),
        }
    }
}

/// Every MCP prompt/resource/admin tool, bound to one manager.
pub fn mcp_management_tools(manager: Arc<McpClientManager>) -> Vec<Arc<dyn Tool>> {
    vec![
        Arc::new(ListMcpPromptsTool::new(Arc::clone(&manager))),
        Arc::new(GetMcpPromptTool::new(Arc::clone(&manager))),
        Arc::new(ListMcpResourcesTool::new(Arc::clone(&manager))),
        Arc::new(ReadMcpResourceTool::new(Arc::clone(&manager))),
        Arc::new(RestartMcpServerTool::new(manager)),
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    fn manager() -> Arc<McpClientManager> {
        Arc::new(McpClientManager::new())
    }

    fn ctx() -> ToolContext {
        ToolContext::new(".")
    }

    #[tokio::test]
    async fn listing_with_no_servers_says_so_rather_than_returning_nothing() {
        let out = ListMcpPromptsTool::new(manager())
            .execute(&Value::Object(Default::default()), &ctx(), CancellationToken::new())
            .await;
        assert!(!out.is_error);
        assert_eq!(out.content, "No MCP prompts available.");

        let out = ListMcpResourcesTool::new(manager())
            .execute(&Value::Object(Default::default()), &ctx(), CancellationToken::new())
            .await;
        assert_eq!(out.content, "No MCP resources available.");
    }

    #[tokio::test]
    async fn a_missing_required_argument_names_the_argument() {
        let cases: Vec<(Arc<dyn Tool>, &str, serde_json::Value)> = vec![
            (Arc::new(GetMcpPromptTool::new(manager())), "server", serde_json::json!({})),
            (
                Arc::new(GetMcpPromptTool::new(manager())),
                "name",
                serde_json::json!({ "server": "s" }),
            ),
            (Arc::new(ReadMcpResourceTool::new(manager())), "server", serde_json::json!({})),
            (
                Arc::new(ReadMcpResourceTool::new(manager())),
                "uri",
                serde_json::json!({ "server": "s" }),
            ),
            (Arc::new(RestartMcpServerTool::new(manager())), "server", serde_json::json!({})),
        ];

        for (tool, missing, input) in cases {
            let out = tool.execute(&input, &ctx(), CancellationToken::new()).await;
            assert!(out.is_error, "{} should reject missing {missing}", tool.name());
            assert!(
                out.content.contains(missing),
                "{} should name the missing argument, got: {}",
                tool.name(),
                out.content
            );
        }
    }

    /// A call against a server that is not connected must explain that, not
    /// return an empty result that reads as "the prompt is empty".
    #[tokio::test]
    async fn an_unknown_server_is_an_error_not_an_empty_result() {
        let out = GetMcpPromptTool::new(manager())
            .execute(
                &serde_json::json!({ "server": "nope", "name": "p" }),
                &ctx(),
                CancellationToken::new(),
            )
            .await;
        assert!(out.is_error);
        assert!(out.content.contains("not connected"), "{}", out.content);
    }

    /// SECURITY: restarting launches a process, so it must not be able to skip
    /// the permission chain the way a read-only tool does.
    #[test]
    fn restarting_a_server_is_not_read_only() {
        assert!(
            !RestartMcpServerTool::new(manager()).is_read_only(),
            "restart spawns a process and must take the permission decision"
        );
    }

    #[test]
    fn the_listing_tools_are_read_only() {
        assert!(ListMcpPromptsTool::new(manager()).is_read_only());
        assert!(ListMcpResourcesTool::new(manager()).is_read_only());
        assert!(GetMcpPromptTool::new(manager()).is_read_only());
        assert!(ReadMcpResourceTool::new(manager()).is_read_only());
    }

    #[test]
    fn the_management_set_contains_every_tool_exactly_once() {
        let tools = mcp_management_tools(manager());
        let mut names: Vec<&str> = tools.iter().map(|t| t.name()).collect();
        names.sort();
        assert_eq!(
            names,
            vec![
                "get_mcp_prompt",
                "list_mcp_prompts",
                "list_mcp_resources",
                "read_mcp_resource",
                "restart_mcp_server"
            ]
        );
    }

    #[test]
    fn an_empty_server_filter_is_treated_as_absent() {
        assert!(server_filter(&serde_json::json!({ "server": "" })).is_none());
        assert_eq!(server_filter(&serde_json::json!({ "server": "x" })), Some("x"));
        assert!(server_filter(&serde_json::json!({})).is_none());
    }
}
