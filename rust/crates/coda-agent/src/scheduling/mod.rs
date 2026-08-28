//! Scheduled task definitions, recurrence, and persistence.
//!
//! ## Modules
//! - [`cron_expression`]: Five-field cron parser that rejects malformed expressions.
//! - [`scheduled_task`]: Data types for scheduled definitions.
//! - [`schedule_recurrence`]: Next-occurrence computation for all schedule kinds.
//! - [`scheduled_task_store`]: Thread-safe persistent store with atomic writes.

pub mod cron_expression;
pub mod schedule_recurrence;
pub mod scheduled_task;
pub mod scheduled_task_store;

pub use cron_expression::CronExpression;
pub use schedule_recurrence::ScheduleRecurrence;
pub use scheduled_task::{
    ScheduleDefinitionDraft, ScheduleKind, ScheduleTerminalMetadata, ScheduleTerminalOutcome,
    ScheduledTask, ScheduledTaskStoreSnapshot,
};
pub use scheduled_task_store::ScheduledTaskStore;
