//! Tool registry: name-keyed, insertion-ordered, last-write-wins on collision.
//!
//! # Collision policy
//!
//! Registry construction order decides which tool wins when two tools share a
//! name. Callers should insert built-ins first, then MCP tools, then plugins;
//! the last inserted tool with a given name is what `resolve` returns.
//!
//! The registry keeps an explicit insertion-ordered `Vec` so `all()` is stable
//! regardless of `HashMap`'s internal ordering. On a name collision the value
//! is updated in-place in the `Vec`, preserving the original slot position of
//! the first occurrence (matching C# `Dictionary` behaviour).
//!
//! # Live sources
//!
//! A registry may also hold `ToolSource`s (e.g. MCP servers that reconnect).
//! Their tools are queried on every lookup and never shadow static tools.

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use coda_llm::ToolDefinition;

use crate::tool::{Tool, ToolSource};

pub struct ToolRegistry {
    /// Name → index into `tools`.
    names: HashMap<String, usize>,
    /// Tools in first-insertion order; on collision the value at the existing
    /// index is replaced.
    tools: Vec<Arc<dyn Tool>>,
    /// Live tool sets queried on every lookup, after the static tools.
    sources: Vec<Arc<dyn ToolSource>>,
}

impl ToolRegistry {
    /// Build a registry from an ordered sequence of tools.
    ///
    /// If the same name appears more than once, the later tool wins but keeps
    /// its predecessor's slot in the ordering.
    pub fn new(tools: impl IntoIterator<Item = Arc<dyn Tool>>) -> Self {
        let mut registry = Self { names: HashMap::new(), tools: Vec::new(), sources: Vec::new() };
        for tool in tools {
            registry.insert(tool);
        }
        registry
    }

    /// Attach a live tool source. Its tools never shadow static tools.
    pub fn with_source(mut self, source: Arc<dyn ToolSource>) -> Self {
        self.sources.push(source);
        self
    }

    /// Insert a tool. If a tool with the same name already exists it is
    /// replaced (last write wins), but the slot order is not changed.
    pub fn insert(&mut self, tool: Arc<dyn Tool>) {
        let name = tool.name().to_owned();
        if let Some(&idx) = self.names.get(&name) {
            self.tools[idx] = tool;
        } else {
            let idx = self.tools.len();
            self.names.insert(name, idx);
            self.tools.push(tool);
        }
    }

    /// Look up a tool by its exact name. Static tools win over source tools.
    pub fn resolve(&self, name: &str) -> Option<Arc<dyn Tool>> {
        if let Some(&idx) = self.names.get(name) {
            return Some(Arc::clone(&self.tools[idx]));
        }
        self.sources
            .iter()
            .find_map(|source| source.tools().into_iter().find(|t| t.name() == name))
    }

    /// All tools: static ones in first-insertion order, then source tools whose
    /// names are not already taken.
    pub fn all(&self) -> Vec<Arc<dyn Tool>> {
        let mut seen: HashSet<String> = self.names.keys().cloned().collect();
        let mut all = self.tools.clone();
        for source in &self.sources {
            for tool in source.tools() {
                if seen.insert(tool.name().to_owned()) {
                    all.push(tool);
                }
            }
        }
        all
    }

    /// Wire definitions for all tools, in `all()` order.
    pub fn definitions(&self) -> Vec<ToolDefinition> {
        self.all().iter().map(|t| t.to_definition()).collect()
    }

    /// A new static registry containing a snapshot of the read-only tools,
    /// preserving their relative order from this registry.
    pub fn read_only(&self) -> Self {
        Self::new(self.all().into_iter().filter(|t| t.is_read_only()))
    }

    /// A new registry keeping only the tools `keep` accepts. Static tools are
    /// filtered now; source tools are filtered lazily on every query.
    pub fn filtered(&self, keep: impl Fn(&dyn Tool) -> bool + Send + Sync + 'static) -> Self {
        let keep: Arc<dyn Fn(&dyn Tool) -> bool + Send + Sync> = Arc::new(keep);
        let mut registry = Self::new(self.tools.iter().filter(|t| keep(t.as_ref())).cloned());
        registry.sources = self
            .sources
            .iter()
            .map(|source| {
                Arc::new(FilteredSource { inner: Arc::clone(source), keep: Arc::clone(&keep) })
                    as Arc<dyn ToolSource>
            })
            .collect();
        registry
    }
}

struct FilteredSource {
    inner: Arc<dyn ToolSource>,
    keep: Arc<dyn Fn(&dyn Tool) -> bool + Send + Sync>,
}

impl ToolSource for FilteredSource {
    fn tools(&self) -> Vec<Arc<dyn Tool>> {
        self.inner.tools().into_iter().filter(|t| (self.keep)(t.as_ref())).collect()
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use async_trait::async_trait;
    use tokio_util::sync::CancellationToken;

    use crate::tool::{ToolContext, ToolOutcome, ToolResult};

    struct MockTool {
        name: &'static str,
        read_only: bool,
    }

    #[async_trait]
    impl Tool for MockTool {
        fn name(&self) -> &str {
            self.name
        }
        fn description(&self) -> &str {
            self.name
        }
        fn input_schema_json(&self) -> &str {
            "{}"
        }
        fn is_read_only(&self) -> bool {
            self.read_only
        }
        async fn execute(
            &self,
            _input: &serde_json::Value,
            _ctx: &ToolContext,
            _cancel: CancellationToken,
        ) -> ToolOutcome {
            ToolResult::ok("")
        }
    }

    fn tool(name: &'static str, read_only: bool) -> Arc<dyn Tool> {
        Arc::new(MockTool { name, read_only })
    }

    #[test]
    fn resolve_returns_none_for_unknown_name() {
        let reg = ToolRegistry::new([tool("read_file", true)]);
        assert!(reg.resolve("unknown").is_none());
    }

    #[test]
    fn resolve_finds_a_registered_tool() {
        let reg = ToolRegistry::new([tool("read_file", true)]);
        assert!(reg.resolve("read_file").is_some());
    }

    // Spec §8 item 30: last inserted wins on name collision.
    #[test]
    fn last_inserted_wins_on_name_collision() {
        let first = Arc::new(MockTool { name: "cmd", read_only: false });
        let second = Arc::new(MockTool { name: "cmd", read_only: true });
        let reg = ToolRegistry::new([first as Arc<dyn Tool>, second as Arc<dyn Tool>]);
        // Second tool (read_only = true) must have won.
        assert!(reg.resolve("cmd").unwrap().is_read_only());
    }

    // The replaced tool occupies its original slot, so `all()` length does not grow.
    #[test]
    fn collision_does_not_grow_the_registry() {
        let reg = ToolRegistry::new([
            tool("a", false),
            tool("b", false),
            tool("a", true), // duplicate
        ]);
        assert_eq!(reg.all().len(), 2);
    }

    // Slot order is preserved after a collision.
    #[test]
    fn collision_preserves_original_slot_order() {
        let reg = ToolRegistry::new([
            tool("first", false),
            tool("second", false),
            tool("first", true), // replaces "first" in slot 0
        ]);
        assert_eq!(reg.all()[0].name(), "first");
        assert_eq!(reg.all()[1].name(), "second");
        assert!(reg.all()[0].is_read_only()); // new value
    }

    // Spec §8 item 30: ReadOnly() subset.
    #[test]
    fn read_only_returns_only_read_only_tools() {
        let reg = ToolRegistry::new([tool("edit", false), tool("read", true), tool("list", true)]);
        let ro = reg.read_only();
        assert_eq!(ro.all().len(), 2);
        assert!(ro.all().iter().all(|t| t.is_read_only()));
    }

    #[test]
    fn definitions_maps_all_tools_to_wire_form() {
        let reg = ToolRegistry::new([tool("a", false), tool("b", true)]);
        let defs = reg.definitions();
        assert_eq!(defs.len(), 2);
        assert_eq!(defs[0].name, "a");
        assert_eq!(defs[1].name, "b");
    }

    struct VecSource(std::sync::Mutex<Vec<Arc<dyn Tool>>>);

    impl ToolSource for VecSource {
        fn tools(&self) -> Vec<Arc<dyn Tool>> {
            self.0.lock().unwrap().clone()
        }
    }

    fn source(tools: Vec<Arc<dyn Tool>>) -> Arc<VecSource> {
        Arc::new(VecSource(std::sync::Mutex::new(tools)))
    }

    #[test]
    fn a_source_tool_resolves_and_appears_in_all_and_definitions() {
        let reg = ToolRegistry::new([tool("a", false)])
            .with_source(source(vec![tool("live", true)]));
        assert!(reg.resolve("live").is_some());
        let names: Vec<_> = reg.all().iter().map(|t| t.name().to_owned()).collect();
        assert_eq!(names, ["a", "live"]);
        let defs: Vec<_> = reg.definitions().into_iter().map(|d| d.name).collect();
        assert_eq!(defs, ["a", "live"]);
    }

    #[test]
    fn a_static_tool_shadows_a_source_tool_of_the_same_name() {
        let reg = ToolRegistry::new([tool("dup", true)])
            .with_source(source(vec![tool("dup", false)]));
        assert!(reg.resolve("dup").unwrap().is_read_only());
        assert_eq!(reg.all().len(), 1);
    }

    #[test]
    fn source_changes_are_visible_without_rebuilding() {
        let src = source(vec![tool("one", false)]);
        let reg = ToolRegistry::new([]).with_source(src.clone());
        assert!(reg.resolve("two").is_none());
        *src.0.lock().unwrap() = vec![tool("two", false)];
        assert!(reg.resolve("one").is_none());
        assert!(reg.resolve("two").is_some());
        assert_eq!(reg.definitions().len(), 1);
    }

    #[test]
    fn filtered_applies_to_source_tools_lazily() {
        let src = source(vec![tool("ro1", true), tool("rw1", false)]);
        let reg = ToolRegistry::new([tool("s_ro", true), tool("s_rw", false)])
            .with_source(src.clone());
        let ro = reg.filtered(|t| t.is_read_only());
        let names = |r: &ToolRegistry| -> Vec<String> {
            r.all().iter().map(|t| t.name().to_owned()).collect()
        };
        assert_eq!(names(&ro), ["s_ro", "ro1"]);
        *src.0.lock().unwrap() = vec![tool("rw2", false), tool("ro2", true)];
        assert_eq!(names(&ro), ["s_ro", "ro2"]);
    }
}