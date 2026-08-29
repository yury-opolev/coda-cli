//! Session todo list re-exported from `coda-tool`.
//!
//! The types are defined in `coda-tool` so that `ToolContext.todos` can live
//! there without pulling in `coda-agent`.  This module re-exports them so
//! existing `crate::todos::*` paths keep working (MINOR 4).

pub use coda_tool::context::{TodoItem, TodoStatus, TodoStore};