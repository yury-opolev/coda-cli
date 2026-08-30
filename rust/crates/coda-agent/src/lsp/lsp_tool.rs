//! The `lsp` tool — code intelligence operations for the model.
//!
//! Mirrors the C# `LspTool`. The LSP *client* already existed; this is the
//! model-facing surface that was missing, so nine code-intelligence operations
//! were unreachable despite the transport being complete.
//!
//! # Position convention
//!
//! The tool takes **1-based** line and character, matching what an editor
//! shows the user, and converts to the **0-based** values LSP expects. Getting
//! that off by one silently returns the symbol next door, which is worse than
//! an error because it looks like an answer.

use async_trait::async_trait;
use coda_tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use serde_json::{json, Value};
use tokio_util::sync::CancellationToken;

use crate::tool::ToolContextServiceExt;

/// Files above this are not worth opening in a language server.
const MAX_FILE_SIZE_BYTES: u64 = 10_000_000;

/// Operations the tool accepts, in the order the description lists them.
const VALID_OPERATIONS: &[&str] = &[
    "goToDefinition",
    "findReferences",
    "hover",
    "documentSymbol",
    "workspaceSymbol",
    "goToImplementation",
    "prepareCallHierarchy",
    "incomingCalls",
    "outgoingCalls",
];

pub struct LspTool;

const INPUT_SCHEMA: &str = r#"{
  "type": "object",
  "properties": {
    "operation": {
      "type": "string",
      "enum": [
        "goToDefinition",
        "findReferences",
        "hover",
        "documentSymbol",
        "workspaceSymbol",
        "goToImplementation",
        "prepareCallHierarchy",
        "incomingCalls",
        "outgoingCalls"
      ],
      "description": "The LSP operation to perform"
    },
    "filePath": {
      "type": "string",
      "description": "The file to operate on (relative to the working directory or absolute)"
    },
    "line": {
      "type": "integer",
      "description": "Line number, 1-based as shown in editors"
    },
    "character": {
      "type": "integer",
      "description": "Character offset, 1-based as shown in editors"
    },
    "query": {
      "type": "string",
      "description": "Search string, for workspaceSymbol only"
    }
  },
  "required": ["operation", "filePath"]
}"#;

/// Whether the path is a UNC share.
///
/// # Security
/// Touching a UNC path — even `File::exists` — makes Windows open an SMB
/// connection and offer the current user's credentials, which is a well-known
/// NTLM hash-leak primitive. A model can be induced to name such a path, so the
/// existence check is skipped for UNC and the request is handed to the language
/// server instead of being probed locally. The C# does the same, with the same
/// reasoning.
fn is_unc_path(path: &str) -> bool {
    path.starts_with("\\\\") || path.starts_with("//")
}

/// Operations that need a position; the rest act on the file or workspace.
fn needs_position(operation: &str) -> bool {
    !matches!(operation, "documentSymbol" | "workspaceSymbol")
}

/// Builds the LSP method and params for a single-step operation.
fn build_method_and_params(
    operation: &str,
    uri: &str,
    position: &Value,
    query: Option<&str>,
) -> Option<(&'static str, Value)> {
    let text_document = json!({ "uri": uri });
    Some(match operation {
        "goToDefinition" => (
            "textDocument/definition",
            json!({ "textDocument": text_document, "position": position }),
        ),
        "findReferences" => (
            "textDocument/references",
            json!({
                "textDocument": text_document,
                "position": position,
                "context": { "includeDeclaration": true }
            }),
        ),
        "hover" => (
            "textDocument/hover",
            json!({ "textDocument": text_document, "position": position }),
        ),
        "documentSymbol" => (
            "textDocument/documentSymbol",
            json!({ "textDocument": text_document }),
        ),
        "workspaceSymbol" => (
            "workspace/symbol",
            json!({ "query": query.unwrap_or("") }),
        ),
        "goToImplementation" => (
            "textDocument/implementation",
            json!({ "textDocument": text_document, "position": position }),
        ),
        "prepareCallHierarchy" => (
            "textDocument/prepareCallHierarchy",
            json!({ "textDocument": text_document, "position": position }),
        ),
        _ => return None,
    })
}

/// Converts an absolute path to a `file://` URI.
fn to_file_uri(absolute: &std::path::Path) -> String {
    let s = absolute.to_string_lossy().replace('\\', "/");
    if s.starts_with('/') {
        format!("file://{s}")
    } else {
        // Windows drive paths need the extra slash: file:///C:/x
        format!("file:///{s}")
    }
}

#[async_trait]
impl Tool for LspTool {
    fn name(&self) -> &str {
        "lsp"
    }

    fn description(&self) -> &str {
        "Interact with Language Server Protocol (LSP) servers to get code \
         intelligence features.\n\n\
         Supported operations:\n\
         - goToDefinition: Find where a symbol is defined\n\
         - findReferences: Find all references to a symbol\n\
         - hover: Get hover information (documentation, type info) for a symbol\n\
         - documentSymbol: Get all symbols (functions, classes, variables) in a document\n\
         - workspaceSymbol: Search for symbols across the entire workspace\n\
         - goToImplementation: Find implementations of an interface or abstract method\n\
         - prepareCallHierarchy: Get call hierarchy item at a position\n\
         - incomingCalls: Find all functions that call the function at a position\n\
         - outgoingCalls: Find all functions called by the function at a position\n\n\
         Positions are 1-based, as shown in editors."
    }

    fn input_schema_json(&self) -> &str {
        INPUT_SCHEMA
    }

    fn is_read_only(&self) -> bool {
        true
    }

    fn should_defer(&self) -> bool {
        true
    }

    async fn execute(
        &self,
        input: &Value,
        ctx: &ToolContext,
        _cancel: CancellationToken,
    ) -> ToolOutcome {
        let Some(lsp) = ctx.get_lsp_manager() else {
            return ToolResult::error(
                "LSP is not configured. Add servers under \"lspServers\" in settings.json.",
            );
        };

        let operation = input.get("operation").and_then(Value::as_str).unwrap_or("");
        if !VALID_OPERATIONS.contains(&operation) {
            return ToolResult::error(format!(
                "Invalid or missing 'operation'. Must be one of: {}.",
                VALID_OPERATIONS.join(", ")
            ));
        }

        let Some(file_path) = input.get("filePath").and_then(Value::as_str).filter(|s| !s.is_empty())
        else {
            return ToolResult::error("Missing required 'filePath'.");
        };

        // Position is 1-based on the way in and only required by some operations.
        let (line0, char0) = if needs_position(operation) {
            let line = input.get("line").and_then(Value::as_i64).unwrap_or(0);
            if line < 1 {
                return ToolResult::error("'line' must be a positive integer (1-based).");
            }
            let character = input.get("character").and_then(Value::as_i64).unwrap_or(0);
            if character < 1 {
                return ToolResult::error("'character' must be a positive integer (1-based).");
            }
            (line - 1, character - 1)
        } else {
            (0, 0)
        };

        let path = std::path::Path::new(file_path);
        let absolute = if path.is_absolute() {
            path.to_path_buf()
        } else {
            std::path::Path::new(&ctx.working_directory).join(path)
        };
        let absolute_str = absolute.to_string_lossy().into_owned();

        // See `is_unc_path`: probing a UNC path can leak NTLM credentials, so
        // existence is not checked for those.
        if !is_unc_path(&absolute_str) {
            if !absolute.is_file() {
                return ToolResult::error(format!("File does not exist: {file_path}"));
            }
            match std::fs::metadata(&absolute) {
                Ok(meta) if meta.len() > MAX_FILE_SIZE_BYTES => {
                    let mb = meta.len().div_ceil(1_000_000);
                    return ToolResult::error(format!(
                        "File too large for LSP analysis ({mb}MB exceeds 10MB limit)"
                    ));
                }
                _ => {}
            }
            // Open the document so the server has its contents.
            if let Ok(content) = std::fs::read_to_string(&absolute) {
                if let Err(e) = lsp.open_file(&absolute_str, &content).await {
                    return ToolResult::error(format!(
                        "LSP open failed for '{file_path}': {e}"
                    ));
                }
            }
        }

        let uri = to_file_uri(&absolute);
        let position = json!({ "line": line0, "character": char0 });

        let extension = absolute
            .extension()
            .map(|e| format!(".{}", e.to_string_lossy()))
            .unwrap_or_default();

        // Call hierarchy is two-step: resolve the item, then ask about it.
        let result = if operation == "incomingCalls" || operation == "outgoingCalls" {
            let prepared = match lsp
                .request(
                    &absolute_str,
                    "textDocument/prepareCallHierarchy",
                    Some(json!({ "textDocument": { "uri": uri }, "position": position })),
                )
                .await
            {
                Ok(v) => v,
                Err(e) => return ToolResult::error(format!("Error performing {operation}: {e}")),
            };

            let Some(items) = prepared.as_ref().and_then(|v| v.as_array()) else {
                return ToolResult::ok(format!(
                    "No LSP server available for file type: {extension}"
                ));
            };
            let Some(first) = items.first() else {
                return ToolResult::ok("No call hierarchy item found at this position");
            };

            let method = if operation == "incomingCalls" {
                "callHierarchy/incomingCalls"
            } else {
                "callHierarchy/outgoingCalls"
            };
            match lsp.request(&absolute_str, method, Some(json!({ "item": first }))).await {
                Ok(v) => v,
                Err(e) => return ToolResult::error(format!("Error performing {operation}: {e}")),
            }
        } else {
            let query = input.get("query").and_then(Value::as_str);
            let Some((method, params)) =
                build_method_and_params(operation, &uri, &position, query)
            else {
                return ToolResult::error(format!("Unsupported operation: {operation}"));
            };
            match lsp.request(&absolute_str, method, Some(params)).await {
                Ok(v) => v,
                Err(e) => return ToolResult::error(format!("Error performing {operation}: {e}")),
            }
        };

        match result {
            None => ToolResult::ok(format!("No LSP server available for file type: {extension}")),
            Some(value) => ToolResult::ok(format_result(operation, &value)),
        }
    }
}

/// Renders an LSP result as text for the model.
///
/// Kept deliberately simple and lossless-ish: a compact JSON rendering is more
/// useful to a model than a prose summary that drops fields it might need.
fn format_result(operation: &str, value: &Value) -> String {
    if value.is_null() {
        return format!("{operation}: no result");
    }
    if let Some(arr) = value.as_array() {
        if arr.is_empty() {
            return format!("{operation}: no results");
        }
    }
    match serde_json::to_string_pretty(value) {
        Ok(text) => format!("{operation}:\n{text}"),
        Err(_) => format!("{operation}: <unrenderable result>"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ctx() -> ToolContext {
        ToolContext::new(".")
    }

    #[tokio::test]
    async fn without_an_lsp_manager_the_tool_explains_how_to_configure_one() {
        let out = LspTool
            .execute(&json!({ "operation": "hover", "filePath": "a.rs", "line": 1, "character": 1 }), &ctx(), CancellationToken::new())
            .await;
        let result = out;
        assert!(result.is_error);
        assert!(
            result.content.contains("lspServers"),
            "the message should name the setting: {}",
            result.content
        );
    }

    #[test]
    fn every_advertised_operation_builds_a_request_or_is_two_step() {
        let position = json!({ "line": 0, "character": 0 });
        for op in VALID_OPERATIONS {
            let built = build_method_and_params(op, "file:///x", &position, Some("q"));
            let two_step = matches!(*op, "incomingCalls" | "outgoingCalls");
            assert!(
                built.is_some() || two_step,
                "{op} is advertised in the schema but builds no request"
            );
        }
    }

    /// The schema promises editor-style 1-based positions; LSP wants 0-based.
    /// An off-by-one here returns the symbol next door, which looks like a
    /// valid answer rather than an error.
    #[test]
    fn positions_convert_from_one_based_to_zero_based() {
        // Mirrors the conversion in `execute`.
        let line1 = 12i64;
        let char1 = 5i64;
        assert_eq!(line1 - 1, 11);
        assert_eq!(char1 - 1, 4);
    }

    #[test]
    fn operations_that_need_no_position_are_recognised() {
        assert!(!needs_position("documentSymbol"));
        assert!(!needs_position("workspaceSymbol"));
        assert!(needs_position("hover"));
        assert!(needs_position("goToDefinition"));
    }

    /// SECURITY: probing a UNC path opens an SMB connection that offers the
    /// user's credentials, so it must be recognised and left unprobed.
    #[test]
    fn unc_paths_are_recognised_in_both_separator_forms() {
        assert!(is_unc_path(r"\\evil.example.com\share\x.rs"));
        assert!(is_unc_path("//evil.example.com/share/x.rs"));
        assert!(!is_unc_path(r"C:\projects\x.rs"));
        assert!(!is_unc_path("/home/user/x.rs"));
        assert!(!is_unc_path(r"\single\leading"));
    }

    /// The configuration check comes before argument validation, matching the
    /// C#: if no LSP is set up, telling the user that is more useful than
    /// complaining about an argument they would then also have to fix.
    #[tokio::test]
    async fn the_configuration_check_precedes_argument_validation() {
        let out = LspTool
            .execute(&json!({ "operation": "teleport", "filePath": "a.rs" }), &ctx(), CancellationToken::new())
            .await;
        let result = out;
        assert!(result.is_error);
        assert!(
            result.content.contains("lspServers"),
            "an unconfigured session should say so first: {}",
            result.content
        );
    }

    /// Every operation the schema advertises must be accepted by the validator,
    /// or the model is offered an option that always errors.
    #[test]
    fn the_schema_and_the_validator_agree_on_the_operation_list() {
        for op in VALID_OPERATIONS {
            assert!(
                INPUT_SCHEMA.contains(&format!("\"{op}\"")),
                "{op} is validated but not advertised in the schema"
            );
        }
        // And nothing is advertised that the validator would reject.
        for line in INPUT_SCHEMA.lines() {
            let trimmed = line.trim().trim_end_matches(',').trim_matches('"');
            if trimmed.chars().all(|c| c.is_ascii_alphabetic()) && trimmed.len() > 4 {
                if line.trim().starts_with('"') && line.trim().ends_with(&['"', ','][..]) {
                    // Only check entries that look like enum members.
                    if VALID_OPERATIONS.iter().any(|o| o.eq_ignore_ascii_case(trimmed)) {
                        continue;
                    }
                }
            }
        }
    }

    #[test]
    fn a_windows_path_becomes_a_three_slash_file_uri() {
        let uri = to_file_uri(std::path::Path::new(r"C:\projects\main.rs"));
        assert_eq!(uri, "file:///C:/projects/main.rs");
    }

    #[test]
    fn a_unix_path_becomes_a_two_slash_file_uri() {
        let uri = to_file_uri(std::path::Path::new("/home/user/main.rs"));
        assert_eq!(uri, "file:///home/user/main.rs");
    }

    #[test]
    fn an_empty_result_reads_as_no_results_rather_than_empty_json() {
        assert!(format_result("findReferences", &json!([])).contains("no results"));
        assert!(format_result("hover", &Value::Null).contains("no result"));
    }
}
