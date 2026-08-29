//! `lsp_diagnostics` tool — opens a file with the LSP server for its language
//! and returns any diagnostics it has published so far.
//!
//! The tool is read-only (the LSP does not modify the file) and deferred
//! (it appears in `tool_search` but not in the default tool list, matching
//! the pattern of other auxiliary tools).

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};
use crate::tool::ToolContextServiceExt as _;

pub struct LspDiagnosticsTool;

/// Schema for the `lsp_diagnostics` tool input.
const INPUT_SCHEMA: &str = r#"{
  "type": "object",
  "properties": {
    "file_path": {
      "type": "string",
      "description": "Path to the file to check (relative to the working directory or absolute)"
    }
  },
  "required": ["file_path"]
}"#;

#[async_trait]
impl Tool for LspDiagnosticsTool {
    fn name(&self) -> &str {
        "lsp_diagnostics"
    }

    fn description(&self) -> &str {
        "Open a source file with its configured LSP server and return any \
         diagnostics (errors, warnings) that the language server has published \
         for it. Requires an LSP server to be configured for the file's \
         extension in settings.json."
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
        let file_path = match input.get("file_path").and_then(Value::as_str) {
            Some(p) => p,
            None => return ToolResult::error("Missing required parameter 'file_path'"),
        };

        let lsp = match ctx.get_lsp_manager() {
            Some(m) => m,
            None => {
                return ToolResult::error(
                    "No LSP manager is configured for this session. \
                     Add language server entries to settings.json under 'lspServers'.",
                )
            }
        };

        // Open the file to let the server publish diagnostics.
        let content = match std::fs::read_to_string(file_path) {
            Ok(c) => c,
            Err(e) => {
                // Try relative to working directory.
                let full = std::path::Path::new(&ctx.working_directory).join(file_path);
                match std::fs::read_to_string(&full) {
                    Ok(c) => c,
                    Err(_) => return ToolResult::error(format!("Cannot read '{file_path}': {e}")),
                }
            }
        };

        if let Err(e) = lsp.open_file(file_path, &content).await {
            return ToolResult::error(format!("LSP open failed for '{file_path}': {e}"));
        }

        // Give the server a brief moment to push publishDiagnostics.
        tokio::time::sleep(std::time::Duration::from_millis(300)).await;

        let diag_result = lsp.diagnostics().check_for_diagnostics();
        match diag_result {
            None => ToolResult::ok(format!("No new diagnostics for '{file_path}'.")),
            Some(files) => {
                let text = format_diagnostics(&files, file_path);
                ToolResult::ok(text)
            }
        }
    }
}

fn format_diagnostics(files: &[crate::lsp::diagnostic::DiagnosticFile], requested: &str) -> String {
    let mut out = String::new();
    let mut total = 0usize;

    for file in files {
        let path = file
            .uri
            .strip_prefix("file:///")
            .or_else(|| file.uri.strip_prefix("file://"))
            .unwrap_or(&file.uri);

        for diag in &file.diagnostics {
            let line = diag.range.start.line + 1;
            let col = diag.range.start.character + 1;
            let sev = diag.severity.label();
            let src = diag.source.as_deref().map(|s| format!("[{s}] ")).unwrap_or_default();
            out.push_str(&format!(
                "{path}:{line}:{col}: {sev}: {src}{}\n",
                diag.message
            ));
            total += 1;
        }
    }

    if out.is_empty() {
        format!("No diagnostics for '{requested}'.")
    } else {
        format!("{total} diagnostic(s):\n{}", out.trim_end())
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::lsp::diagnostic::{
        DiagnosticFile, LspDiagnostic, LspDiagnosticSeverity, LspPosition, LspRange,
    };

    fn error_diag(msg: &str) -> LspDiagnostic {
        LspDiagnostic {
            message: msg.to_string(),
            severity: LspDiagnosticSeverity::Error,
            range: LspRange {
                start: LspPosition { line: 2, character: 4 },
                end: LspPosition { line: 2, character: 10 },
            },
            source: Some("rustc".to_string()),
            code: None,
        }
    }

    #[test]
    fn format_includes_location_severity_source() {
        let files = vec![DiagnosticFile {
            uri: "file:///src/main.rs".to_string(),
            diagnostics: vec![error_diag("cannot find value `x`")],
        }];
        let text = format_diagnostics(&files, "src/main.rs");
        assert!(text.contains("3:5"), "should include 1-based line:col");
        assert!(text.contains("error"));
        assert!(text.contains("[rustc]"));
        assert!(text.contains("cannot find value `x`"));
    }

    #[test]
    fn format_empty_returns_no_diagnostics_message() {
        let text = format_diagnostics(&[], "src/main.rs");
        assert!(text.contains("No diagnostics"));
    }

    #[test]
    fn format_multiple_files() {
        let files = vec![
            DiagnosticFile {
                uri: "file:///a.rs".to_string(),
                diagnostics: vec![error_diag("err A")],
            },
            DiagnosticFile {
                uri: "file:///b.rs".to_string(),
                diagnostics: vec![error_diag("err B")],
            },
        ];
        let text = format_diagnostics(&files, "a.rs");
        assert!(text.contains("2 diagnostic(s)"));
        assert!(text.contains("err A"));
        assert!(text.contains("err B"));
    }

    #[tokio::test]
    async fn tool_returns_error_when_no_lsp_manager() {
        let tool = LspDiagnosticsTool;
        let ctx = crate::tool::ToolContext::new("/project");
        let input = serde_json::json!({ "file_path": "main.rs" });
        let result = tool.execute(&input, &ctx, CancellationToken::new()).await;
        assert!(result.is_error);
        assert!(result.content.contains("No LSP manager"));
    }
}
