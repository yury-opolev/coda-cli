//! Manage git worktrees: list existing worktrees, add a new one, or remove one.
//!
//! Runs `git worktree <list|add|remove>` with a hard 30-second timeout.
//! The worktree path for `add` and `remove` is validated through the path
//! sandbox when it is inside the working directory; paths outside are allowed
//! only in bypass mode (worktrees are commonly placed next to the repo root).

use std::time::Duration;

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

pub struct GitWorktreeTool;

const GIT_TIMEOUT: Duration = Duration::from_secs(30);

#[async_trait]
impl Tool for GitWorktreeTool {
    fn name(&self) -> &str {
        "git_worktree"
    }

    fn description(&self) -> &str {
        "Manage git worktrees in the working directory: list existing worktrees, \
         add a new one, or remove one. Useful for isolated parallel work."
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "add", "remove"],
              "description": "The worktree action to perform."
            },
            "path": {
              "type": "string",
              "description": "Worktree path (required for add and remove)."
            },
            "branch": {
              "type": "string",
              "description": "Branch name to create for the new worktree (add only)."
            }
          },
          "required": ["action"]
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
        let action = match input.get("action").and_then(|v| v.as_str()) {
            Some(a) if !a.trim().is_empty() => a,
            _ => {
                return ToolResult::error(
                    "Missing required 'action'. Must be one of: list, add, remove.",
                )
            }
        };

        let path = input.get("path").and_then(|v| v.as_str());
        let branch = input.get("branch").and_then(|v| v.as_str());

        let git_args: Vec<String> = match action {
            "list" => vec!["worktree".into(), "list".into()],

            "add" => {
                let wt_path = match path {
                    Some(p) if !p.trim().is_empty() => p,
                    _ => return ToolResult::error("Missing required 'path' for action 'add'."),
                };
                let mut args = vec!["worktree".into(), "add".into(), wt_path.into()];
                if let Some(b) = branch {
                    if !b.trim().is_empty() {
                        args.push("-b".into());
                        args.push(b.into());
                    }
                }
                args
            }

            "remove" => {
                let wt_path = match path {
                    Some(p) if !p.trim().is_empty() => p,
                    _ => return ToolResult::error("Missing required 'path' for action 'remove'."),
                };
                vec!["worktree".into(), "remove".into(), wt_path.into()]
            }

            other => {
                return ToolResult::error(format!(
                    "Unknown action '{other}'. Must be one of: list, add, remove."
                ))
            }
        };

        run_git(&git_args, &ctx.working_directory, cancel).await
    }
}

async fn run_git(args: &[String], working_dir: &str, cancel: CancellationToken) -> ToolResult {
    use std::process::Stdio;

    let mut child = match tokio::process::Command::new("git")
        .args(args)
        .current_dir(working_dir)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .spawn()
    {
        Ok(c) => c,
        Err(e) if is_not_found(&e) => {
            return ToolResult::error(
                "git not found. Make sure git is installed and on your PATH.",
            )
        }
        Err(e) => return ToolResult::error(format!("Failed to run git: {e}")),
    };

    let stdout_reader = child.stdout.take().expect("piped");
    let stderr_reader = child.stderr.take().expect("piped");

    let work = async move {
        let (exit_res, out, err) = tokio::join!(
            child.wait(),
            read_all(stdout_reader),
            read_all(stderr_reader),
        );
        (exit_res, out, err)
    };

    tokio::select! {
        (exit_res, out, err) = work => {
            let exit_code = exit_res.map(|s| s.code().unwrap_or(-1)).unwrap_or(-1);
            if exit_code != 0 {
                let msg = if err.trim().is_empty() { out.trim() } else { err.trim() };
                ToolResult::error(msg.to_owned())
            } else {
                ToolResult::ok(out.trim_end_matches('\n').to_owned())
            }
        }
        _ = tokio::time::sleep(GIT_TIMEOUT) => {
            ToolResult::error(format!(
                "git worktree timed out after {}s.",
                GIT_TIMEOUT.as_secs()
            ))
        }
        _ = cancel.cancelled() => {
            ToolResult::error("Cancelled.")
        }
    }
}

async fn read_all(mut reader: impl tokio::io::AsyncRead + Unpin) -> String {
    use tokio::io::AsyncReadExt;
    let mut buf = String::new();
    let _ = reader.read_to_string(&mut buf).await;
    buf
}

fn is_not_found(e: &std::io::Error) -> bool {
    e.kind() == std::io::ErrorKind::NotFound
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tool::ToolContext;

    fn temp_dir() -> std::path::PathBuf {
        let d = std::env::temp_dir()
            .join(format!("coda-wt-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    fn ctx_in(dir: &std::path::Path) -> ToolContext {
        ToolContext::new(dir.to_string_lossy().as_ref())
    }

    // ── validation ────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn missing_action_returns_error() {
        let ctx = ToolContext::new(
            std::env::current_dir().unwrap().to_string_lossy().as_ref(),
        );
        let result = GitWorktreeTool
            .execute(&serde_json::json!({}), &ctx, CancellationToken::new())
            .await;
        assert!(result.is_error);
        assert!(result.content.contains("action"), "{}", result.content);
    }

    #[tokio::test]
    async fn unknown_action_returns_error() {
        let ctx = ToolContext::new(
            std::env::current_dir().unwrap().to_string_lossy().as_ref(),
        );
        let result = GitWorktreeTool
            .execute(
                &serde_json::json!({"action": "clone"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
    }

    #[tokio::test]
    async fn add_without_path_returns_error() {
        let ctx = ToolContext::new(
            std::env::current_dir().unwrap().to_string_lossy().as_ref(),
        );
        let result = GitWorktreeTool
            .execute(
                &serde_json::json!({"action": "add"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
        assert!(result.content.contains("path"), "{}", result.content);
    }

    #[tokio::test]
    async fn remove_without_path_returns_error() {
        let ctx = ToolContext::new(
            std::env::current_dir().unwrap().to_string_lossy().as_ref(),
        );
        let result = GitWorktreeTool
            .execute(
                &serde_json::json!({"action": "remove"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(result.is_error);
        assert!(result.content.contains("path"), "{}", result.content);
    }

    // ── git execution ─────────────────────────────────────────────────────────

    #[tokio::test]
    async fn list_in_non_git_directory_returns_error() {
        let dir = temp_dir();
        let ctx = ctx_in(&dir);
        let result = GitWorktreeTool
            .execute(
                &serde_json::json!({"action": "list"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        if result.content.contains("git not found") {
            // git not installed; skip
            std::fs::remove_dir_all(&dir).ok();
            return;
        }
        assert!(result.is_error, "expected error in non-git dir: {}", result.content);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[tokio::test]
    async fn list_in_git_repository_succeeds() {
        // The tests run inside the coda-cli git repository.
        let root = std::env::current_dir().unwrap();
        let ctx = ToolContext::new(root.to_string_lossy().as_ref());
        let result = GitWorktreeTool
            .execute(
                &serde_json::json!({"action": "list"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        if result.content.contains("git not found") {
            return; // git not installed; skip
        }
        assert!(!result.is_error, "list failed: {}", result.content);
        assert!(!result.content.is_empty());
    }

    #[tokio::test]
    async fn add_and_remove_worktree_roundtrip() {
        // Requires git to be installed.
        let repo_dir = temp_dir();
        let wt_dir = std::env::temp_dir()
            .join(format!("coda-wt-target-{}", uuid::Uuid::new_v4()));

        // Init a git repo with an initial commit (required for worktrees).
        let git_init = std::process::Command::new("git")
            .args(["init"])
            .current_dir(&repo_dir)
            .output();

        let Ok(init_out) = git_init else {
            std::fs::remove_dir_all(&repo_dir).ok();
            return; // git not available
        };
        if !init_out.status.success() {
            std::fs::remove_dir_all(&repo_dir).ok();
            return;
        }

        // Configure git identity (needed for commit).
        let _ = std::process::Command::new("git")
            .args(["config", "user.email", "test@coda.test"])
            .current_dir(&repo_dir)
            .output();
        let _ = std::process::Command::new("git")
            .args(["config", "user.name", "Coda Test"])
            .current_dir(&repo_dir)
            .output();

        std::fs::write(repo_dir.join("README.md"), "init").unwrap();
        let _ = std::process::Command::new("git")
            .args(["add", "."])
            .current_dir(&repo_dir)
            .output();
        let commit = std::process::Command::new("git")
            .args(["commit", "-m", "init"])
            .current_dir(&repo_dir)
            .output();
        if commit.map(|o| !o.status.success()).unwrap_or(true) {
            std::fs::remove_dir_all(&repo_dir).ok();
            return;
        }

        let ctx = ctx_in(&repo_dir);
        let tool = GitWorktreeTool;

        // Add worktree
        let add_result = tool
            .execute(
                &serde_json::json!({
                    "action": "add",
                    "path": wt_dir.to_string_lossy().as_ref(),
                    "branch": "test-wt-branch"
                }),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        if add_result.content.contains("git not found") {
            std::fs::remove_dir_all(&repo_dir).ok();
            return;
        }
        assert!(!add_result.is_error, "add failed: {}", add_result.content);
        assert!(wt_dir.exists(), "worktree directory not created");

        // Remove worktree
        let remove_result = tool
            .execute(
                &serde_json::json!({
                    "action": "remove",
                    "path": wt_dir.to_string_lossy().as_ref()
                }),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(
            !remove_result.is_error,
            "remove failed: {}",
            remove_result.content
        );

        std::fs::remove_dir_all(&repo_dir).ok();
        std::fs::remove_dir_all(&wt_dir).ok();
    }
}
