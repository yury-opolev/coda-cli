//! Maintains the session todo list.
//!
//! The model sends the FULL list on every call; this tool replaces the stored
//! list and returns a rendered checklist.  Considered "read-only" because it
//! performs no filesystem or network mutations.

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::todos::{TodoItem, TodoStatus};
use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

pub struct TodoWriteTool;

#[async_trait]
impl Tool for TodoWriteTool {
    fn name(&self) -> &str {
        "todo_write"
    }

    fn description(&self) -> &str {
        "Create and update the session's structured todo list. Send the ENTIRE list every call. \
         Each item has 'content' (imperative, e.g. \"Fix the bug\"), 'activeForm' \
         (present continuous, e.g. \"Fixing the bug\"), and 'status' \
         (pending|in_progress|completed). Use it to plan multi-step work and mark \
         items completed as you finish them."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "todos": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "content":    {"type": "string"},
                  "activeForm": {"type": "string"},
                  "status":     {"type": "string", "enum": ["pending","in_progress","completed"]}
                },
                "required": ["content","activeForm","status"]
              }
            }
          },
          "required": ["todos"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(
        &self,
        input: &serde_json::Value,
        ctx: &ToolContext,
        _cancel: CancellationToken,
    ) -> ToolOutcome {
        let todos_val = match input.get("todos") {
            Some(v) if v.is_array() => v.as_array().unwrap(),
            _ => return ToolResult::error("todo_write requires a 'todos' array."),
        };

        let mut items = Vec::with_capacity(todos_val.len());
        for element in todos_val {
            let content = element
                .get("content")
                .and_then(|v| v.as_str())
                .unwrap_or("")
                .to_owned();
            if content.trim().is_empty() {
                continue;
            }
            let active_form = element
                .get("activeForm")
                .and_then(|v| v.as_str())
                .unwrap_or(&content)
                .to_owned();
            let status = parse_status(element.get("status").and_then(|v| v.as_str()));
            items.push(TodoItem::new(content, active_form, status));
        }

        if items.is_empty() {
            return ToolResult::error(
                "todo_write requires at least one todo with non-empty content.",
            );
        }

        if let Some(store) = &ctx.todos {
            store.set(items.clone());
        }

        ToolResult::ok(render(&items))
    }
}

fn parse_status(value: Option<&str>) -> TodoStatus {
    match value {
        Some("completed") => TodoStatus::Completed,
        Some("in_progress") => TodoStatus::InProgress,
        _ => TodoStatus::Pending,
    }
}

/// Render a todo list as a plain-text checklist.
///
/// - `[x]` completed
/// - `[~]` in progress (uses `active_form`)
/// - `[ ]` pending
pub fn render(items: &[TodoItem]) -> String {
    let mut out = String::from("Todos:\n");
    for item in items {
        let (marker, label) = match item.status {
            TodoStatus::Completed => ("[x]", item.content.as_str()),
            TodoStatus::InProgress => ("[~]", item.active_form.as_str()),
            TodoStatus::Pending => ("[ ]", item.content.as_str()),
        };
        out.push_str(&format!("{marker} {label}\n"));
    }
    out.trim_end_matches('\n').to_owned()
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use crate::todos::TodoStore;
    use crate::tool::ToolContext;

    fn ctx() -> ToolContext {
        ToolContext::new(std::env::current_dir().unwrap().to_string_lossy().as_ref())
    }

    fn ctx_with_store() -> (ToolContext, Arc<TodoStore>) {
        let store = Arc::new(TodoStore::new());
        let ctx = ToolContext::new("/").with_todos(Arc::clone(&store));
        (ctx, store)
    }

    #[tokio::test]
    async fn missing_todos_returns_error() {
        let result = TodoWriteTool
            .execute(&serde_json::json!({}), &ctx(), CancellationToken::new())
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn empty_todos_array_returns_error() {
        let result = TodoWriteTool
            .execute(
                &serde_json::json!({"todos": []}),
                &ctx(),
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn renders_todo_checklist() {
        let result = TodoWriteTool
            .execute(
                &serde_json::json!({
                    "todos": [
                        {"content": "Fix bug", "activeForm": "Fixing bug", "status": "in_progress"},
                        {"content": "Write tests", "activeForm": "Writing tests", "status": "pending"},
                        {"content": "Deploy", "activeForm": "Deploying", "status": "completed"}
                    ]
                }),
                &ctx(),
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error, "failed: {}", result.content);
        assert!(result.content.contains("[~] Fixing bug"), "{}", result.content);
        assert!(result.content.contains("[ ] Write tests"), "{}", result.content);
        assert!(result.content.contains("[x] Deploy"), "{}", result.content);
    }

    #[tokio::test]
    async fn stores_items_in_todo_store() {
        let (ctx, store) = ctx_with_store();
        let result = TodoWriteTool
            .execute(
                &serde_json::json!({
                    "todos": [
                        {"content": "A", "activeForm": "Doing A", "status": "pending"}
                    ]
                }),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(!result.is_error);
        let items = store.items();
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].content, "A");
    }

    #[test]
    fn render_uses_active_form_for_in_progress() {
        let items = vec![
            TodoItem::new("Fix bug", "Fixing bug", TodoStatus::InProgress),
            TodoItem::new("Write tests", "Writing tests", TodoStatus::Pending),
        ];
        let out = render(&items);
        assert!(out.contains("[~] Fixing bug"), "{out}");
        assert!(out.contains("[ ] Write tests"), "{out}");
    }
}
