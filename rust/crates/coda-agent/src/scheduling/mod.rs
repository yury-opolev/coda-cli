//! Scheduled task definitions, recurrence, and persistence.
//!
//! ## Modules
//! - [`cron_expression`]: Five-field cron parser that rejects malformed expressions.
//! - [`scheduled_task`]: Data types for scheduled definitions.
//! - [`limits`]: Shared creation validation plus pure bounded-schedule admission.
//! - [`schedule_recurrence`]: Next-occurrence computation for all schedule kinds.
//! - [`scheduled_task_store`]: Thread-safe persistent store with atomic writes.
//! - [`runtime`]: Live runtime that watches the store and fires due definitions.

pub mod cron_expression;
pub mod limits;
pub mod runtime;
pub mod schedule_recurrence;
pub mod scheduled_task;
pub mod scheduled_task_store;

pub use cron_expression::CronExpression;
pub use limits::{
    admit, build_draft, deadline_wake, parse_unit_duration, reported_state, resolve_bounds,
    sanitize_note, Admission, ScheduleBounds, ScheduleCreateRequest,
};
pub use runtime::{
    NullScheduleLifecycleSink, ScheduleClock, ScheduleLifecycleEvent, ScheduleLifecycleSink,
    ScheduleRuntimeSnapshot, ScheduleRuntimeState, ScheduleRuntimeStatus, ScheduleRuntimeView,
    ScheduleRuntime, ScheduledAgentRunner, ScheduledRun, SystemClock, TaskManagerRunner,
};
pub use schedule_recurrence::ScheduleRecurrence;
pub use scheduled_task::{
    ScheduleDefinitionDraft, ScheduleKind, ScheduleLiveState, ScheduleLiveStatus,
    ScheduleRetirement, ScheduleRetirementReason, ScheduleTerminalMetadata,
    ScheduleTerminalOutcome, ScheduledTask, ScheduledTaskStoreSnapshot,
};
pub use scheduled_task_store::ScheduledTaskStore;
