//! Per-tool execution context and the path sandbox.
//!
//! Path sandbox: tools may only operate inside `working_directory` (or
//! additional granted roots). Symlinks/junctions on the deepest existing
//! component are resolved so a reparse point that points outside the cwd
//! cannot be used as an escape hatch.

use std::collections::HashSet;
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::todos::TodoStore;
use crate::lsp::LspServerManager;
use crate::tasks::TaskManager;
use crate::scheduling::ScheduledTaskStore;

// ── Interaction seams ─────────────────────────────────────────────────────────

/// Seam for surfacing a multiple-choice question to the user and receiving an answer.
/// The loop wires a concrete implementation; `None` in `ToolContext` signals headless mode.
#[async_trait]
pub trait UserQuestion: Send + Sync {
    async fn ask(
        &self,
        question: &str,
        options: &[String],
        multi_select: bool,
        cancel: CancellationToken,
    ) -> String;
}

/// Seam for presenting a plan to the user and receiving an approval decision.
/// The loop wires a concrete implementation; `None` in `ToolContext` signals headless mode.
#[async_trait]
pub trait PlanApprover: Send + Sync {
    async fn approve(&self, plan: &str, cancel: CancellationToken) -> bool;
}

/// Lightweight description of a registered tool, used by `tool_search` to query
/// the tool list without holding `Arc<dyn Tool>` in the context (which would be
/// a circular dependency: `Tool::execute` takes `&ToolContext`).
#[derive(Debug, Clone)]
pub struct ToolDescriptor {
    pub name: String,
    pub description: String,
    pub input_schema_json: String,
    pub is_deferred: bool,
    pub search_hint: Option<String>,
}

// ── ToolContext ───────────────────────────────────────────────────────────────

/// Context passed to a tool during execution.
///
/// All service handles are `None` for tools run in isolation (tests, early specs).
/// The loop populates them when the full stack is wired.
pub struct ToolContext {
    /// Root directory the agent was started in; the default sandbox boundary.
    pub working_directory: String,
    /// When `true`, filesystem tools may operate anywhere the process can reach.
    /// Set only in bypass-permissions ("yolo") mode; default keeps the cwd sandbox.
    pub allow_outside_working_directory: bool,
    /// Additional roots (e.g. skill directories the user consented to) that
    /// file tools may access beyond `working_directory`.
    pub granted_directories: Option<HashSet<String>>,

    // ── Optional service handles ──────────────────────────────────────────────
    /// Session todo list; `None` means the store is not available (tool still works,
    /// it just cannot persist the todo state).
    pub todos: Option<Arc<TodoStore>>,
    /// User-question seam; `None` means headless (no interactive user).
    pub user_question: Option<Arc<dyn UserQuestion>>,
    /// Plan-approval seam; `None` means headless.
    pub plan_approver: Option<Arc<dyn PlanApprover>>,
    /// Full registry of tools in descriptor form, used by `tool_search`.
    pub all_tools: Option<Vec<ToolDescriptor>>,
    /// LSP server manager; `None` when no servers are configured.
    pub lsp_manager: Option<Arc<LspServerManager>>,
    /// Task manager; `None` when the task runtime is not available.
    pub task_manager: Option<Arc<TaskManager>>,
    /// Scheduled task store; `None` when scheduling is not available.
    pub schedule_store: Option<Arc<ScheduledTaskStore>>,
    /// Caller's task id (the subagent or shell that is running these tools).
    /// `None` means the main agent. Used to enforce depth-based authorization:
    /// a subagent may only manage its own strict descendants.
    pub caller_task_id: Option<String>,
}

impl std::fmt::Debug for ToolContext {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ToolContext")
            .field("working_directory", &self.working_directory)
            .field("allow_outside_working_directory", &self.allow_outside_working_directory)
            .field("granted_directories", &self.granted_directories)
            .field("todos", &self.todos.is_some())
            .field("user_question", &self.user_question.is_some())
            .field("plan_approver", &self.plan_approver.is_some())
            .field("all_tools", &self.all_tools.as_ref().map(|v| v.len()))
            .field("lsp_manager", &self.lsp_manager.is_some())
    .field("task_manager", &self.task_manager.is_some())
    .field("schedule_store", &self.schedule_store.is_some())
    .field("caller_task_id", &self.caller_task_id)
    .finish()
    }
}

impl ToolContext {
    pub fn new(working_directory: impl Into<String>) -> Self {
        Self {
            working_directory: working_directory.into(),
            allow_outside_working_directory: false,
            granted_directories: None,
            todos: None,
            user_question: None,
            plan_approver: None,
            all_tools: None,
            lsp_manager: None,
            task_manager: None,
            schedule_store: None,
            caller_task_id: None,
        }
    }

    /// Enables bypass mode (no path sandbox).
    pub fn with_bypass(mut self) -> Self {
        self.allow_outside_working_directory = true;
        self
    }

    pub fn with_todos(mut self, todos: Arc<TodoStore>) -> Self {
        self.todos = Some(todos);
        self
    }

    pub fn with_user_question(mut self, uq: Arc<dyn UserQuestion>) -> Self {
        self.user_question = Some(uq);
        self
    }

    pub fn with_plan_approver(mut self, pa: Arc<dyn PlanApprover>) -> Self {
        self.plan_approver = Some(pa);
        self
    }

    pub fn with_all_tools(mut self, tools: Vec<ToolDescriptor>) -> Self {
        self.all_tools = Some(tools);
        self
    }

    pub fn with_lsp_manager(mut self, mgr: Arc<LspServerManager>) -> Self {
        self.lsp_manager = Some(mgr);
        self
    }

    pub fn with_task_manager(mut self, mgr: Arc<TaskManager>) -> Self {
        self.task_manager = Some(mgr);
        self
    }

    pub fn with_schedule_store(mut self, store: Arc<ScheduledTaskStore>) -> Self {
        self.schedule_store = Some(store);
        self
    }

    pub fn with_caller_task_id(mut self, task_id: impl Into<String>) -> Self {
        self.caller_task_id = Some(task_id.into());
        self
    }
}

/// Resolve a (possibly relative) path against `working_directory` without
/// requiring the path to exist.
pub fn resolve_path(working_directory: &str, path: &str) -> String {
    let p = Path::new(path);
    let full = if p.is_absolute() {
        p.to_path_buf()
    } else {
        Path::new(working_directory).join(path)
    };
    normalize_path(&full).to_string_lossy().into_owned()
}

/// Returns `true` when `full_path` is the same as `root` or is inside it,
/// after resolving symlinks on the deepest existing component.
pub fn is_within_root(root: &str, full_path: &str) -> bool {
    let root_resolved = resolve_final_target(Path::new(root));
    let path_resolved = resolve_final_target(Path::new(full_path));

    // Case-insensitive comparison matches Windows filesystem semantics.
    let root_str = root_resolved.to_string_lossy().to_lowercase();
    let root_str = root_str.trim_end_matches(['/', '\\']).to_string();
    let path_str = path_resolved.to_string_lossy().to_lowercase();

    path_str == root_str
        || path_str.starts_with(&format!("{root_str}\\"))
        || path_str.starts_with(&format!("{root_str}/"))
}

/// Resolve a path and confirm it stays within the allowed roots.
///
/// Returns `Ok(full_path)` when the path is permitted, `Err(reason)` when it
/// would escape the sandbox. Bypass mode (`allow_outside_root = true`) skips
/// the containment check but still resolves the path.
pub fn try_resolve_within_root(
    root: &str,
    path: &str,
    allow_outside_root: bool,
    additional_roots: Option<&HashSet<String>>,
) -> Result<String, String> {
    let full = resolve_path(root, path);
    if allow_outside_root {
        return Ok(full);
    }
    if is_within_root(root, &full) {
        return Ok(full);
    }
    if let Some(extras) = additional_roots {
        for extra in extras {
            if is_within_root(extra, &full) {
                return Ok(full);
            }
        }
    }
    Err(format!(
        "Path '{path}' is outside the working directory and is not allowed. \
         Switch to bypass permissions (/yolo) to allow paths outside the working directory."
    ))
}

/// Resolve through symlinks/junctions on the deepest existing path component.
///
/// Falls back recursively to the parent when the full path does not exist yet,
/// so a new file inside an existing directory is still resolved correctly.
fn resolve_final_target(path: &Path) -> PathBuf {
    if let Ok(canon) = std::fs::canonicalize(path) {
        return canon;
    }
    if let Some(parent) = path.parent() {
        if !parent.as_os_str().is_empty() {
            let parent_resolved = resolve_final_target(parent);
            if let Some(name) = path.file_name() {
                return parent_resolved.join(name);
            }
        }
    }
    normalize_path(path)
}

/// Normalize a path (collapse `..` and `.`) without requiring existence.
///
/// `..` is clamped at the root rather than allowed to pop past it. Popping the
/// prefix would silently turn an absolute path into a relative one, and any
/// caller that later resolved it against the working directory would land
/// somewhere entirely different from what was checked.
fn normalize_path(path: &Path) -> PathBuf {
    let mut components: Vec<Component<'_>> = Vec::new();
    for component in path.components() {
        match component {
            Component::ParentDir => {
                // Never pop the prefix or root: `C:\a\..\..` is `C:\`, not `..`.
                let poppable = !matches!(
                    components.last(),
                    None | Some(Component::Prefix(_)) | Some(Component::RootDir)
                );
                if poppable {
                    components.pop();
                }
            }
            Component::CurDir => {}
            c => components.push(c),
        }
    }
    components.into_iter().collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn relative_path_is_resolved_against_working_directory() {
        let resolved = resolve_path("/project", "src/main.rs");
        assert!(resolved.replace('\\', "/").ends_with("/project/src/main.rs"));
    }

    #[test]
    fn absolute_path_is_preserved() {
        let resolved = resolve_path("/project", "/other/file.rs");
        assert!(resolved.replace('\\', "/").ends_with("/other/file.rs"));
    }

    #[test]
    fn parent_traversal_is_normalized() {
        let resolved = resolve_path("/project", "src/../README.md");
        assert!(resolved.replace('\\', "/").ends_with("/project/README.md"));
    }

    #[test]
    fn bypass_mode_allows_any_path() {
        let result = try_resolve_within_root("/project", "/etc/passwd", true, None);
        assert!(result.is_ok());
    }

    #[test]
    fn sandbox_blocks_traversal_out_of_the_root() {
        let root = std::env::current_dir().unwrap();
        let root_str = root.to_string_lossy().into_owned();

        for escape in [
            "../../../../../../etc/passwd",
            "../outside.txt",
            "subdir/../../outside.txt",
        ] {
            assert!(
                try_resolve_within_root(&root_str, escape, false, None).is_err(),
                "{escape} escaped the sandbox"
            );
        }
    }

    #[test]
    fn sandbox_blocks_an_absolute_path_outside_the_root() {
        let root = std::env::current_dir().unwrap();
        let root_str = root.to_string_lossy().into_owned();
        let outside = if cfg!(windows) {
            r"C:\Windows\System32\drivers\etc\hosts"
        } else {
            "/etc/passwd"
        };

        assert!(try_resolve_within_root(&root_str, outside, false, None).is_err());
    }

    #[test]
    fn containment_is_checked_at_a_path_boundary_not_by_prefix() {
        // `/projevil` must not pass a `/proj` check just because the string
        // starts with it.
        let root = std::env::temp_dir().join("coda-sandbox-proj");
        let sibling = std::env::temp_dir().join("coda-sandbox-projevil");

        assert!(!is_within_root(
            &root.to_string_lossy(),
            &sibling.to_string_lossy()
        ));
        assert!(is_within_root(
            &root.to_string_lossy(),
            &root.join("inner/file.txt").to_string_lossy()
        ));
    }

    #[test]
    fn a_granted_directory_widens_the_sandbox_only_to_itself() {
        let root = std::env::temp_dir().join("coda-sandbox-root");
        let granted = std::env::temp_dir().join("coda-sandbox-granted");
        let elsewhere = std::env::temp_dir().join("coda-sandbox-elsewhere");
        let granted_list: std::collections::HashSet<String> =
            [granted.to_string_lossy().into_owned()].into_iter().collect();

        let root_str = root.to_string_lossy().into_owned();
        assert!(try_resolve_within_root(
            &root_str,
            &granted.join("file.txt").to_string_lossy(),
            false,
            Some(&granted_list),
        )
        .is_ok());

        assert!(
            try_resolve_within_root(
                &root_str,
                &elsewhere.join("file.txt").to_string_lossy(),
                false,
                Some(&granted_list),
            )
            .is_err(),
            "a granted directory must not open unrelated paths"
        );
    }

    #[test]
    fn excess_parent_segments_clamp_at_the_root_instead_of_going_relative() {
        // Popping past the root would turn an absolute path into a relative
        // one, which a later join would resolve somewhere unchecked.
        let normalized = normalize_path(Path::new(if cfg!(windows) {
            r"C:\a\..\..\..\b"
        } else {
            "/a/../../../b"
        }));

        assert!(
            normalized.is_absolute(),
            "{} lost its root",
            normalized.display()
        );
    }

    #[test]
    fn path_inside_root_is_allowed() {
        let root = std::env::current_dir().unwrap().to_string_lossy().into_owned();
        let inside = format!("{root}/subdir/file.txt");
        let result = try_resolve_within_root(&root, &inside, false, None);
        assert!(result.is_ok());
    }
}
