//! Edit a Jupyter notebook (.ipynb): replace, insert, or delete a cell.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::{context::try_resolve_within_root, Tool, ToolContext, ToolOutcome, ToolResult};

pub struct NotebookEditTool;

#[async_trait]
impl Tool for NotebookEditTool {
    fn name(&self) -> &str {
        "notebook_edit"
    }

    fn description(&self) -> &str {
        "Edit a Jupyter notebook (.ipynb): replace, insert, or delete a cell by index."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "notebook_path": {"type": "string",  "description": "Path to the .ipynb file"},
            "cell_number":   {"type": "integer", "description": "Zero-based cell index"},
            "new_source":    {"type": "string",  "description": "New source content (required for replace/insert)"},
            "edit_mode":     {"type": "string",  "enum": ["replace","insert","delete"], "description": "Operation to perform (default: replace)"},
            "cell_type":     {"type": "string",  "enum": ["code","markdown"],           "description": "Cell type for insert (default: code)"}
          },
          "required": ["notebook_path", "cell_number"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        false
    }

    async fn execute(
        &self,
        input: &serde_json::Value,
        ctx: &ToolContext,
        cancel: CancellationToken,
    ) -> ToolOutcome {
        let notebook_path = match input.get("notebook_path").and_then(|v| v.as_str()) {
            Some(p) => p,
            None => return ToolResult::error("Missing required parameter 'notebook_path'."),
        };
        let cell_number_raw = match input.get("cell_number").and_then(|v| v.as_i64()) {
            Some(n) => n,
            None => {
                return ToolResult::error(
                    "Missing or invalid required parameter 'cell_number' (must be an integer).",
                )
            }
        };
        let edit_mode = input.get("edit_mode").and_then(|v| v.as_str()).unwrap_or("replace");
        let cell_type = input.get("cell_type").and_then(|v| v.as_str()).unwrap_or("code");
        let new_source = input.get("new_source").and_then(|v| v.as_str());

        if (edit_mode == "replace" || edit_mode == "insert") && new_source.is_none() {
            return ToolResult::error(format!(
                "Parameter 'new_source' is required for edit_mode '{edit_mode}'."
            ));
        }

        let full = match try_resolve_within_root(
            &ctx.working_directory,
            notebook_path,
            ctx.allow_outside_working_directory,
            ctx.granted_directories.as_ref(),
        ) {
            Ok(p) => p,
            Err(e) => return ToolResult::error(e),
        };

        let json_text = tokio::select! {
            r = tokio::fs::read_to_string(&full) => match r {
                Ok(t) => t,
                Err(_) => return ToolResult::error(format!("File not found: {full}")),
            },
            _ = cancel.cancelled() => return ToolResult::error("Cancelled."),
        };

        let mut notebook: serde_json::Value = match serde_json::from_str(&json_text) {
            Ok(v) => v,
            Err(e) => return ToolResult::error(format!("Failed to parse notebook: {e}")),
        };

        if !notebook.is_object() {
            return ToolResult::error(
                "Failed to parse notebook: root must be a JSON object.",
            );
        }

        let cells = match notebook.get_mut("cells").and_then(|v| v.as_array_mut()) {
            Some(c) => c,
            None => {
                return ToolResult::error(
                    "Failed to parse notebook: missing or invalid 'cells' array.",
                )
            }
        };

        match edit_mode {
            "replace" => {
                if cell_number_raw < 0 || cell_number_raw as usize >= cells.len() {
                    return ToolResult::error(format!(
                        "cell_number {cell_number_raw} is out of range \
                         (notebook has {} cells).",
                        cells.len()
                    ));
                }
                let idx = cell_number_raw as usize;
                let cell = &mut cells[idx];
                if !cell.is_object() {
                    return ToolResult::error(format!("Cell {cell_number_raw} is not a JSON object."));
                }
                cell["source"] =
                    serde_json::Value::String(new_source.unwrap().to_owned());
            }
            "insert" => {
                if cell_number_raw < 0 || cell_number_raw as usize > cells.len() {
                    return ToolResult::error(format!(
                        "cell_number {cell_number_raw} is out of range for insert \
                         (notebook has {} cells).",
                        cells.len()
                    ));
                }
                let new_cell = build_cell(cell_type, new_source.unwrap());
                cells.insert(cell_number_raw as usize, new_cell);
            }
            "delete" => {
                if cell_number_raw < 0 || cell_number_raw as usize >= cells.len() {
                    return ToolResult::error(format!(
                        "cell_number {cell_number_raw} is out of range \
                         (notebook has {} cells).",
                        cells.len()
                    ));
                }
                cells.remove(cell_number_raw as usize);
            }
            other => {
                return ToolResult::error(format!(
                    "Unknown edit_mode '{other}'. Must be 'replace', 'insert', or 'delete'."
                ))
            }
        }

        let updated = match serde_json::to_string_pretty(&notebook) {
            Ok(s) => s,
            Err(e) => return ToolResult::error(format!("Failed to serialize notebook: {e}")),
        };

        tokio::select! {
            r = tokio::fs::write(&full, &updated) => match r {
                Ok(_) => ToolResult::ok(format!(
                    "notebook_edit ({edit_mode}) applied to cell {cell_number_raw} in {full}."
                )),
                Err(e) => ToolResult::error(format!("Cannot write {full}: {e}")),
            },
            _ = cancel.cancelled() => ToolResult::error("Cancelled."),
        }
    }
}

fn build_cell(cell_type: &str, source: &str) -> serde_json::Value {
    if cell_type == "markdown" {
        serde_json::json!({
            "cell_type": "markdown",
            "source": source,
            "metadata": {}
        })
    } else {
        serde_json::json!({
            "cell_type": "code",
            "source": source,
            "metadata": {},
            "outputs": [],
            "execution_count": null
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolContext;

    fn temp_dir() -> std::path::PathBuf {
        let d = std::env::temp_dir()
            .join(format!("coda-nb-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    fn sample_notebook(cell_count: usize) -> serde_json::Value {
        let cells: Vec<serde_json::Value> = (0..cell_count)
            .map(|i| {
                serde_json::json!({
                    "cell_type": "code",
                    "source": format!("cell {i}"),
                    "metadata": {},
                    "outputs": [],
                    "execution_count": null
                })
            })
            .collect();
        serde_json::json!({
            "nbformat": 4,
            "nbformat_minor": 5,
            "metadata": {},
            "cells": cells
        })
    }

    // ── sandbox ──────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn rejects_dotdot_traversal() {
        let root = std::env::current_dir().unwrap();
        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = NotebookEditTool
            .execute(
                &serde_json::json!({"notebook_path": "../../x.ipynb", "cell_number": 0}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn rejects_absolute_path_outside_root() {
        let root = std::env::current_dir().unwrap();
        let outside =
            if cfg!(windows) { r"C:\Windows\Temp\x.ipynb" } else { "/tmp/x.ipynb" };
        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = NotebookEditTool
            .execute(
                &serde_json::json!({"notebook_path": outside, "cell_number": 0}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    // ── happy path ────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn replace_changes_cell_source() {
        let dir = temp_dir();
        let nb = sample_notebook(3);
        std::fs::write(dir.join("nb.ipynb"), serde_json::to_string_pretty(&nb).unwrap()).unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = NotebookEditTool
            .execute(
                &serde_json::json!({
                    "notebook_path": "nb.ipynb",
                    "cell_number": 1,
                    "new_source": "replaced"
                }),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        let updated: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(dir.join("nb.ipynb")).unwrap())
                .unwrap();
        assert_eq!(updated["cells"][1]["source"], "replaced");
        // Other cells are untouched.
        assert_eq!(updated["cells"][0]["source"], "cell 0");
        assert_eq!(updated["cells"][2]["source"], "cell 2");
    }

    #[tokio::test]
    async fn insert_adds_cell_at_index() {
        let dir = temp_dir();
        let nb = sample_notebook(2);
        std::fs::write(dir.join("nb.ipynb"), serde_json::to_string_pretty(&nb).unwrap()).unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = NotebookEditTool
            .execute(
                &serde_json::json!({
                    "notebook_path": "nb.ipynb",
                    "cell_number": 1,
                    "edit_mode": "insert",
                    "new_source": "inserted"
                }),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        let updated: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(dir.join("nb.ipynb")).unwrap())
                .unwrap();
        assert_eq!(updated["cells"].as_array().unwrap().len(), 3);
        assert_eq!(updated["cells"][1]["source"], "inserted");
    }

    #[tokio::test]
    async fn delete_removes_cell() {
        let dir = temp_dir();
        let nb = sample_notebook(3);
        std::fs::write(dir.join("nb.ipynb"), serde_json::to_string_pretty(&nb).unwrap()).unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = NotebookEditTool
            .execute(
                &serde_json::json!({
                    "notebook_path": "nb.ipynb",
                    "cell_number": 1,
                    "edit_mode": "delete"
                }),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        let updated: serde_json::Value =
            serde_json::from_str(&std::fs::read_to_string(dir.join("nb.ipynb")).unwrap())
                .unwrap();
        assert_eq!(updated["cells"].as_array().unwrap().len(), 2);
        assert_eq!(updated["cells"][0]["source"], "cell 0");
        assert_eq!(updated["cells"][1]["source"], "cell 2");
    }

    #[tokio::test]
    async fn out_of_range_cell_is_an_error() {
        let dir = temp_dir();
        let nb = sample_notebook(2);
        std::fs::write(dir.join("nb.ipynb"), serde_json::to_string_pretty(&nb).unwrap()).unwrap();
        let ctx = ToolContext::new(dir.to_string_lossy().as_ref());
        let result = NotebookEditTool
            .execute(
                &serde_json::json!({
                    "notebook_path": "nb.ipynb",
                    "cell_number": 99,
                    "new_source": "x"
                }),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
        assert!(result.content.contains("out of range"));
    }
}
