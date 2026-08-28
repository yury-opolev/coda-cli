//! Session todo list: model types and a thread-safe store.
//!
//! The model is deliberately simple — the agent replaces the entire list on
//! every `todo_write` call, so no incremental mutation API is needed.

use std::sync::Mutex;

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
    items: Mutex<Vec<TodoItem>>,
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn set_replaces_full_list() {
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
    fn empty_set_clears_the_list() {
        let store = TodoStore::new();
        store.set(vec![TodoItem::new("x", "x", TodoStatus::Pending)]);
        store.set(vec![]);
        assert!(store.items().is_empty());
    }
}
