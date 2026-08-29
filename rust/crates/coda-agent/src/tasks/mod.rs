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

// ── Shared byte-cap utility ────────────────────────────────────────────────────

/// Returns the byte index at which the newest suffix of `s` begins, where the
/// suffix's UTF-8 encoding fits in `max_bytes` and the cut falls on a
/// code-point boundary.
///
/// When even the last code point alone exceeds `max_bytes`, its byte index is
/// still returned so callers can retain at least one character (C# behaviour).
/// Returns `s.len()` when `s` is empty (no suffix to keep).
///
/// Both [`log_writer::newest_suffix_within_cap`] and
/// [`output_ring`]'s internal trimmer call this to avoid duplicating the logic.
pub(super) fn suffix_start_within_cap(s: &str, max_bytes: usize) -> usize {
    let mut bytes: usize = 0;
    let mut start = s.len();

    for (byte_idx, ch) in s.char_indices().rev() {
        let cp_bytes = ch.len_utf8();
        if bytes + cp_bytes > max_bytes {
            if start == s.len() {
                // Last code point alone exceeds cap — retain it anyway.
                start = byte_idx;
            }
            break;
        }
        bytes += cp_bytes;
        start = byte_idx;
    }

    start
}
