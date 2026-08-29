//! Tool schema quarantine: evict tools whose JSON schemas the provider rejects.
//!
//! When the provider returns a 400 schema-rejection error, the loop identifies
//! the offending tool, evicts it here, and retries the request without it.
//! The quarantine is shared and thread-safe; tools evicted mid-session stay
//! evicted for the rest of the session.

use std::collections::HashSet;
use std::sync::Mutex;

use coda_llm::ToolDefinition;

pub struct ToolQuarantine {
    /// Lowercased tool names that have been evicted.
    quarantined: Mutex<HashSet<String>>,
}

impl ToolQuarantine {
    pub fn new() -> Self {
        Self { quarantined: Mutex::new(HashSet::new()) }
    }

    /// Evict a tool by name; subsequent `filter` calls will exclude it.
    pub fn evict(&self, name: &str) {
        self.quarantined.lock().unwrap().insert(name.to_lowercase());
    }

    pub fn is_quarantined(&self, name: &str) -> bool {
        self.quarantined.lock().unwrap().contains(&name.to_lowercase())
    }

    /// Remove quarantined tools from a list of wire definitions.
    pub fn filter(&self, defs: Vec<ToolDefinition>) -> Vec<ToolDefinition> {
        let q = self.quarantined.lock().unwrap();
        defs.into_iter().filter(|d| !q.contains(&d.name.to_lowercase())).collect()
    }
}

impl Default for ToolQuarantine {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_llm::ToolDefinition;

    fn def(name: &str) -> ToolDefinition {
        ToolDefinition::new(name, "", "{}")
    }

    #[test]
    fn newly_created_quarantine_is_empty() {
        let q = ToolQuarantine::new();
        assert!(!q.is_quarantined("any_tool"));
    }

    #[test]
    fn evicted_tool_is_quarantined() {
        let q = ToolQuarantine::new();
        q.evict("bad_tool");
        assert!(q.is_quarantined("bad_tool"));
    }

    #[test]
    fn eviction_is_case_insensitive() {
        let q = ToolQuarantine::new();
        q.evict("BadTool");
        assert!(q.is_quarantined("badtool"));
        assert!(q.is_quarantined("BADTOOL"));
    }

    #[test]
    fn filter_removes_quarantined_tools() {
        let q = ToolQuarantine::new();
        q.evict("bad");
        let defs = vec![def("good"), def("bad"), def("also_good")];
        let filtered = q.filter(defs);
        assert_eq!(filtered.len(), 2);
        assert!(filtered.iter().all(|d| d.name != "bad"));
    }

    #[test]
    fn filter_without_evictions_returns_all() {
        let q = ToolQuarantine::new();
        let defs = vec![def("a"), def("b")];
        assert_eq!(q.filter(defs).len(), 2);
    }

    #[test]
    fn non_evicted_tools_survive_filter() {
        let q = ToolQuarantine::new();
        q.evict("x");
        let filtered = q.filter(vec![def("a"), def("x"), def("b")]);
        let names: Vec<_> = filtered.iter().map(|d| d.name.as_str()).collect();
        assert_eq!(names, vec!["a", "b"]);
    }
}
