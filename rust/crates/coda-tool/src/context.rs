//! Per-tool execution context, interaction seams, and the todo model.
//!
//! This module lives in `coda-tool` (a leaf crate with no engine-specific
//! dependencies) so both `coda-agent` and `coda-mcp` can share the `Tool`
//! trait and `ToolContext` type without `coda-mcp` pulling in the whole
//! agent engine (MINOR 4).
//!
//! # Opaque service handles
//!
//! The engine-specific service handles (`LspServerManager`, `TaskManager`,
//! `ScheduledTaskStore`, `SubagentFactory`) are stored as `OpaqueServiceHandle`
//! so `coda-tool` does not depend on those types.  `coda-agent` provides a
//! `ToolContextServiceExt` extension trait that gives callers typed access via
//! downcast.

use std::any::Any;
use std::collections::HashSet;
use std::sync::Arc;

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

// ── Session todo model ────────────────────────────────────────────────────────

/// The completion state of a single todo item.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TodoStatus {
    Pending,
    InProgress,
    Completed,
}

/// One todo item.
///
/// `content` is the imperative form ("Fix the bug"); `active_form` is the
/// present-continuous form shown while the item is in progress ("Fixing the bug").
#[derive(Debug, Clone)]
pub struct TodoItem {
    pub content: String,
    pub active_form: String,
    pub status: TodoStatus,
}

impl TodoItem {
    pub fn new(
        content: impl Into<String>,
        active_form: impl Into<String>,
        status: TodoStatus,
    ) -> Self {
        Self { content: content.into(), active_form: active_form.into(), status }
    }
}

/// Thread-safe session todo store; the model replaces the full list on every write.
#[derive(Debug, Default)]
pub struct TodoStore {
    items: std::sync::Mutex<Vec<TodoItem>>,
}

impl TodoStore {
    pub fn new() -> Self {
        Self::default()
    }

    /// Replace the full list atomically.
    pub fn set(&self, items: Vec<TodoItem>) {
        *self.items.lock().expect("todo store lock poisoned") = items;
    }

    /// Return a snapshot of the current list.
    pub fn items(&self) -> Vec<TodoItem> {
        self.items.lock().expect("todo store lock poisoned").clone()
    }
}

// ── Interaction seams ─────────────────────────────────────────────────────────

/// Why a question produced no answer.
///
/// Every variant is a *fault*, never a choice. A client that vanished, a
/// timeout, a cancellation and a malformed reply are all distinguishable, and
/// none of them may ever be collapsed into a selected option.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NoAnswerReason {
    /// No controller was attached at all (headless).
    NoController,
    /// The connection to the controller was lost.
    Disconnected,
    /// The turn/tool was cancelled while the question was outstanding.
    Cancelled,
    /// An explicitly configured reverse-request timeout elapsed.
    Timeout,
    /// A reply arrived but did not carry a usable answer.
    Malformed,
    /// The controller explicitly declined to answer (`session/cancelRequest`).
    Declined,
}

impl NoAnswerReason {
    /// Stable classification token — safe for logs, wire fields and tests.
    pub fn as_str(self) -> &'static str {
        match self {
            NoAnswerReason::NoController => "noController",
            NoAnswerReason::Disconnected => "disconnected",
            NoAnswerReason::Cancelled => "cancelled",
            NoAnswerReason::Timeout => "timeout",
            NoAnswerReason::Malformed => "malformed",
            NoAnswerReason::Declined => "declined",
        }
    }
}

/// The result of asking the operator a question.
///
/// This type exists because `-> String` made "the connection died" and "the
/// user picked option 1" the same value. Every caller must handle
/// [`AnswerOutcome::NoAnswer`] as a typed abort: never coerced to a string,
/// never substituted with the first option, never auto-retried.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AnswerOutcome {
    /// The operator answered. The string is exactly what they chose or typed.
    Answered(String),
    /// No answer exists. There is nothing to tell the model.
    NoAnswer(NoAnswerReason),
}

impl AnswerOutcome {
    /// The answer text, or `None` when there was no answer.
    ///
    /// Deliberately named so a caller cannot mistake it for a fallback: there
    /// is no `unwrap_or_default()`-shaped API on this type.
    pub fn answered(&self) -> Option<&str> {
        match self {
            AnswerOutcome::Answered(a) => Some(a.as_str()),
            AnswerOutcome::NoAnswer(_) => None,
        }
    }

    pub fn no_answer_reason(&self) -> Option<NoAnswerReason> {
        match self {
            AnswerOutcome::NoAnswer(r) => Some(*r),
            AnswerOutcome::Answered(_) => None,
        }
    }
}

/// Seam for surfacing a multiple-choice question to the user.
///
/// The loop wires a concrete implementation; `None` in `ToolContext` signals
/// headless mode (no interactive user).
#[async_trait]
pub trait UserQuestion: Send + Sync {
    async fn ask(
        &self,
        question: &str,
        options: &[String],
        multi_select: bool,
        cancel: CancellationToken,
    ) -> AnswerOutcome;
}

/// Seam for presenting a plan to the user and receiving an approval decision.
///
/// `None` in `ToolContext` signals headless mode.
#[async_trait]
pub trait PlanApprover: Send + Sync {
    async fn approve(&self, plan: &str, cancel: CancellationToken) -> bool;
}

/// Lightweight description of a registered tool, used by `tool_search` to query
/// the tool list without holding `Arc<dyn Tool>` in the context (circular
/// dependency: `Tool::execute` takes `&ToolContext`).
#[derive(Debug, Clone)]
pub struct ToolDescriptor {
    pub name: String,
    pub description: String,
    pub input_schema_json: String,
    pub is_deferred: bool,
    pub search_hint: Option<String>,
}

// ── ScheduleOrigin ────────────────────────────────────────────────────────────

/// Provenance stamp identifying the scheduled definition a run originated from.
///
/// # Trust
/// This value is **only** constructed by trusted Rust callers — the schedule
/// runtime's runner and the subagent host that propagates it to nested
/// children. It is never parsed from tool arguments, model output, or a wire
/// request, so a model cannot claim to be running on behalf of another job.
/// Tools may read it (a later stage uses it for self-cancellation) but a tool
/// can only ever observe the origin of the run it is already executing in.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ScheduleOrigin {
    /// Id of the `ScheduledTask` definition that triggered this run.
    pub definition_id: String,
    /// Human-readable definition name, when the definition has one.
    pub definition_name: Option<String>,
}

impl ScheduleOrigin {
    pub fn new(definition_id: impl Into<String>, definition_name: Option<String>) -> Self {
        Self { definition_id: definition_id.into(), definition_name }
    }
}

// ── OpaqueServiceHandle ───────────────────────────────────────────────────────

/// A type-erased, `Send + Sync`, heap-allocated handle to an engine-specific
/// service.
///
/// `coda-tool` does not know the concrete types (`TaskManager`, `LspServerManager`,
/// `ScheduledTaskStore`, `SubagentFactory`); they live in `coda-agent`.
/// `coda-agent` stores them here and retrieves them via `downcast_ref::<Arc<T>>()`.
///
/// # Safety (soundness)
/// The downcast is guarded by `TypeId`: it can only succeed when the stored
/// type matches the requested type, so no unsafe code is involved.
pub struct OpaqueServiceHandle(Box<dyn Any + Send + Sync>);

impl OpaqueServiceHandle {
    /// Wrap a strongly-typed `Arc<T>` in an opaque handle.
    pub fn new<T: 'static + Send + Sync>(value: Arc<T>) -> Self {
        // Box<Arc<T>> coerces to Box<dyn Any + Send + Sync> because:
        //   Arc<T>: 'static (when T: 'static), Arc<T>: Send + Sync (when T: Send + Sync)
        //   and 'static implies Any
        Self(Box::new(value))
    }

    /// Attempt to recover a reference to the original `Arc<T>`.
    ///
    /// Returns `Some` only when the stored type matches `T`.
    pub fn downcast_ref<T: 'static + Send + Sync>(&self) -> Option<&Arc<T>> {
        // (*self.0) is `dyn Any + Send + Sync`; downcast_ref checks TypeId.
        (*self.0).downcast_ref::<Arc<T>>()
    }
}

impl std::fmt::Debug for OpaqueServiceHandle {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("OpaqueServiceHandle(<opaque>)")
    }
}

// ── ServiceMap (convenience type alias) ──────────────────────────────────────

/// Opaque bag of named service handles.  Re-exported for callers that need to
/// speak about the map type without importing internals.
pub type ServiceMap = OpaqueServiceHandle;

// ── ToolContext ───────────────────────────────────────────────────────────────

/// Context passed to a tool during execution.
///
/// All service handles default to `None` for tools run in isolation
/// (tests, early specs). The agent loop populates them when the full stack is
/// wired.
///
/// # Engine-specific services
///
/// `lsp_manager`, `task_manager`, `schedule_store`, and `subagent_factory` are
/// stored as `OpaqueServiceHandle` to avoid a dependency on `coda-agent` from
/// this crate.  `coda-agent` provides typed accessor helpers via
/// `ToolContextServiceExt`.
pub struct ToolContext {
    /// Root directory the agent was started in; the default sandbox boundary.
    pub working_directory: String,
    /// When `true`, filesystem tools may operate anywhere the process can reach.
    /// Set only in bypass-permissions ("yolo") mode; default keeps the cwd sandbox.
    pub allow_outside_working_directory: bool,
    /// Additional roots (e.g. skill directories the user consented to) that
    /// file tools may access beyond `working_directory`.
    pub granted_directories: Option<HashSet<String>>,

    // ── Service handles: directly typed (coda-tool knows these types) ─────────
    pub todos: Option<Arc<TodoStore>>,
    pub user_question: Option<Arc<dyn UserQuestion>>,
    pub plan_approver: Option<Arc<dyn PlanApprover>>,
    pub all_tools: Option<Vec<ToolDescriptor>>,
    pub caller_task_id: Option<String>,
    /// Set only when this run was launched by the schedule runtime (directly or
    /// as a nested child of such a run).  Trusted: see [`ScheduleOrigin`].
    pub schedule_origin: Option<ScheduleOrigin>,

    // ── Service handles: engine-specific, opaque to coda-tool ─────────────────
    // coda-agent provides typed accessors via ToolContextServiceExt.
    pub lsp_manager: Option<OpaqueServiceHandle>,
    pub task_manager: Option<OpaqueServiceHandle>,
    pub schedule_store: Option<OpaqueServiceHandle>,
    pub subagent_factory: Option<OpaqueServiceHandle>,
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
            .field("caller_task_id", &self.caller_task_id)
            .field("schedule_origin", &self.schedule_origin)
            .field("lsp_manager", &self.lsp_manager.is_some())
            .field("task_manager", &self.task_manager.is_some())
            .field("schedule_store", &self.schedule_store.is_some())
            .field("subagent_factory", &self.subagent_factory.is_some())
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
            caller_task_id: None,
            schedule_origin: None,
            lsp_manager: None,
            task_manager: None,
            schedule_store: None,
            subagent_factory: None,
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

    pub fn with_caller_task_id(mut self, task_id: impl Into<String>) -> Self {
        self.caller_task_id = Some(task_id.into());
        self
    }

    /// Stamp the run's scheduled provenance.  Trusted callers only — never
    /// build this from tool arguments or model output.
    pub fn with_schedule_origin(mut self, origin: ScheduleOrigin) -> Self {
        self.schedule_origin = Some(origin);
        self
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn todo_store_set_replaces_full_list() {
        let store = TodoStore::new();
        store.set(vec![TodoItem::new("a", "doing a", TodoStatus::Pending)]);
        assert_eq!(store.items().len(), 1);

        store.set(vec![
            TodoItem::new("b", "doing b", TodoStatus::InProgress),
            TodoItem::new("c", "doing c", TodoStatus::Completed),
        ]);
        let items = store.items();
        assert_eq!(items.len(), 2);
        assert_eq!(items[0].content, "b");
        assert_eq!(items[1].status, TodoStatus::Completed);
    }

    #[test]
    fn todo_store_empty_set_clears_the_list() {
        let store = TodoStore::new();
        store.set(vec![TodoItem::new("x", "x", TodoStatus::Pending)]);
        store.set(vec![]);
        assert!(store.items().is_empty());
    }
}
