//! Per-tool execution context, re-exported from `coda-tool` with engine-specific
//! service accessor extensions.
//!
//! `coda-tool` defines the portable types (`ToolContext`, interaction seam
//! traits, `TodoStore`, path sandbox).  This module re-exports all of those
//! and adds [`ToolContextServiceExt`] — an extension trait that provides typed
//! access to the engine-specific service handles stored as opaque handles in
//! `ToolContext` (`TaskManager`, `LspServerManager`, `ScheduledTaskStore`,
//! `SubagentFactory`).

use std::sync::Arc;

// ── Re-exports from coda-tool ─────────────────────────────────────────────────
pub use coda_tool::context::{
    OpaqueServiceHandle, PlanApprover, TodoItem, TodoStatus, TodoStore, ToolContext,
    ToolDescriptor, UserQuestion,
};
pub use coda_tool::sandbox::{is_within_root, resolve_path, try_resolve_within_root};

use crate::lsp::LspServerManager;
use crate::scheduling::ScheduledTaskStore;
use crate::subagents::SubagentFactory;
use crate::tasks::TaskManager;

// ── SubagentFactory wrapper ───────────────────────────────────────────────────

/// Concrete newtype that wraps `Arc<dyn SubagentFactory>` so it can be stored
/// as a sized value inside [`OpaqueServiceHandle`].
///
/// `OpaqueServiceHandle::new` requires `T: Sized`; trait objects are unsized,
/// so we must wrap the fat pointer in a sized struct.
pub(crate) struct SubagentFactoryWrapper(pub Arc<dyn SubagentFactory>);

// ── ToolContextServiceExt ─────────────────────────────────────────────────────

/// Extension trait for [`ToolContext`] providing typed access to engine-specific
/// service handles.
///
/// These handles are stored as [`OpaqueServiceHandle`] in `coda-tool::ToolContext`
/// to keep `coda-tool` free of engine dependencies.  The accessor methods here
/// downcast the opaque handles back to their concrete types.
///
/// Import via `use crate::tool::ToolContextServiceExt as _;` or bring into
/// scope explicitly to call the methods.
pub trait ToolContextServiceExt: Sized {
    // ── Typed getters ─────────────────────────────────────────────────────────
    fn get_lsp_manager(&self) -> Option<&Arc<LspServerManager>>;
    fn get_task_manager(&self) -> Option<&Arc<TaskManager>>;
    fn get_schedule_store(&self) -> Option<&Arc<ScheduledTaskStore>>;
    /// Returns a clone of the factory Arc, or `None` if not wired.
    fn get_subagent_factory(&self) -> Option<Arc<dyn SubagentFactory>>;

    // ── Typed builders ────────────────────────────────────────────────────────
    fn with_lsp_manager(self, mgr: Arc<LspServerManager>) -> Self;
    fn with_task_manager(self, mgr: Arc<TaskManager>) -> Self;
    fn with_schedule_store(self, store: Arc<ScheduledTaskStore>) -> Self;
    fn with_subagent_factory(self, factory: Arc<dyn SubagentFactory>) -> Self;
}

impl ToolContextServiceExt for ToolContext {
    fn get_lsp_manager(&self) -> Option<&Arc<LspServerManager>> {
        self.lsp_manager.as_ref()?.downcast_ref::<LspServerManager>()
    }

    fn get_task_manager(&self) -> Option<&Arc<TaskManager>> {
        self.task_manager.as_ref()?.downcast_ref::<TaskManager>()
    }

    fn get_schedule_store(&self) -> Option<&Arc<ScheduledTaskStore>> {
        self.schedule_store.as_ref()?.downcast_ref::<ScheduledTaskStore>()
    }

    fn get_subagent_factory(&self) -> Option<Arc<dyn SubagentFactory>> {
        // We stored SubagentFactoryWrapper (a concrete sized type) in the handle.
        self.subagent_factory
            .as_ref()?
            .downcast_ref::<SubagentFactoryWrapper>()
            .map(|w| Arc::clone(&w.0))
    }

    fn with_lsp_manager(mut self, mgr: Arc<LspServerManager>) -> Self {
        self.lsp_manager = Some(OpaqueServiceHandle::new(mgr));
        self
    }

    fn with_task_manager(mut self, mgr: Arc<TaskManager>) -> Self {
        self.task_manager = Some(OpaqueServiceHandle::new(mgr));
        self
    }

    fn with_schedule_store(mut self, store: Arc<ScheduledTaskStore>) -> Self {
        self.schedule_store = Some(OpaqueServiceHandle::new(store));
        self
    }

    fn with_subagent_factory(mut self, factory: Arc<dyn SubagentFactory>) -> Self {
        // Wrap the fat pointer in a sized struct so OpaqueServiceHandle can
        // store it without requiring T: Sized on the inner dyn type.
        self.subagent_factory = Some(OpaqueServiceHandle::new(Arc::new(SubagentFactoryWrapper(factory))));
        self
    }
}