//! Task runtime — in-process registry and lifecycle management.
//!
//! ## Modules
//! - [`output_ring`]: Bounded in-memory output ring with absolute char cursors.
//! - [`streaming_secret_redactor`]: Streaming secret redaction for log output.
//! - [`log_writer`]: Persistent, secret-redacted per-task log files.
//! - [`log_retention`]: Startup cleanup of old log files.
//! - [`managed_task`]: One live unit of work with lifecycle, ring, and cursors.
//! - [`task_subscription`]: Change subscription with initial snapshot.
//! - [`task_manager`]: Central registry owning all tasks in a session.

pub mod log_retention;
pub mod log_writer;
pub mod managed_task;
pub mod output_ring;
pub mod streaming_secret_redactor;
pub mod task_manager;
pub mod task_subscription;

pub use log_writer::TaskOutputChannel;
pub use managed_task::{
    ManagedTask, TaskActionResult, TaskExecutionMode, TaskKind, TaskRunStatus, TaskSnapshot,
    MAIN_CONSUMER_ID,
};
pub use task_manager::{
    TaskCompletionEntry, TaskManager, DEFAULT_MAX_RETAINED_TERMINAL_TASKS,
    DEFAULT_SHUTDOWN_BUDGET,
};
pub use task_subscription::{TaskChange, TaskChangeKind, TaskSubscription};
