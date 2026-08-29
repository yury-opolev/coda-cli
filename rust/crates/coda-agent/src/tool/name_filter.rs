//! Tool name filter: allow-list intersect, deny-list union, deny wins.
//!
//! Applied at composition time (not in the loop) to the main-agent's tool
//! registry. Subagent and scheduled-root registries are built independently
//! and are never filtered here — non-propagation is the design intent.

use std::collections::HashSet;

/// A pure allow/deny name filter.
///
/// # Merge semantics
///
/// `Merge` is monotonically tightening:
/// - `allow` is **intersected** — a project file can restrict further but can
///   never widen what the user file permitted.
/// - `deny` is **unioned** — either file's denials are always honoured.
/// - Deny wins when a name appears in both lists.
#[derive(Debug, Clone, Default)]
pub struct ToolNameFilter {
    /// When `Some`, only tools in this set pass. An empty vec excludes everything.
    /// `None` means no allowlist restriction.
    allow: Option<Vec<String>>,
    /// Names in this list are always excluded. Empty has no effect.
    deny: Vec<String>,
}

impl ToolNameFilter {
    pub fn new(allow: Option<Vec<String>>, deny: Vec<String>) -> Self {
        Self { allow, deny }
    }

    /// Monotonically tightening merge: intersect allow, union deny.
    pub fn merge(user: Option<Self>, project: Option<Self>) -> Self {
        match (user, project) {
            (None, None) => Self::default(),
            (Some(u), None) => u,
            (None, Some(p)) => p,
            (Some(u), Some(p)) => {
                let merged_allow = match (u.allow, p.allow) {
                    (None, None) => None,
                    (Some(a), None) | (None, Some(a)) => Some(a),
                    (Some(ua), Some(pa)) => {
                        let project_set: HashSet<_> =
                            pa.iter().map(|s| s.to_lowercase()).collect();
                        Some(
                            ua.into_iter()
                                .filter(|n| project_set.contains(&n.to_lowercase()))
                                .collect(),
                        )
                    }
                };
                let mut deny_set: HashSet<String> =
                    u.deny.iter().map(|s| s.to_lowercase()).collect();
                deny_set.extend(p.deny.iter().map(|s| s.to_lowercase()));
                Self {
                    allow: merged_allow,
                    deny: deny_set.into_iter().collect(),
                }
            }
        }
    }

    /// Returns `true` when a tool with `name` would survive this filter.
    pub fn passes(&self, name: &str) -> bool {
        let lower = name.to_lowercase();
        if let Some(allow) = &self.allow {
            let allow_set: HashSet<_> = allow.iter().map(|s| s.to_lowercase()).collect();
            if !allow_set.contains(&lower) {
                return false;
            }
        }
        let deny_set: HashSet<_> = self.deny.iter().map(|s| s.to_lowercase()).collect();
        !deny_set.contains(&lower)
    }

    /// Filter a sequence of tool names, returning only those that pass.
    pub fn apply<'a>(&self, names: impl IntoIterator<Item = &'a str>) -> Vec<&'a str> {
        names.into_iter().filter(|n| self.passes(n)).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // Spec §8 item 30: ToolNameFilter.Merge intersects allow, unions deny, deny wins.

    #[test]
    fn null_filter_passes_everything() {
        let f = ToolNameFilter::default();
        assert!(f.passes("anything"));
        assert!(f.passes("read_file"));
    }

    #[test]
    fn allowlist_excludes_tools_not_in_it() {
        let f = ToolNameFilter::new(Some(vec!["read".into(), "write".into()]), vec![]);
        assert!(f.passes("read"));
        assert!(f.passes("write"));
        assert!(!f.passes("execute"));
    }

    #[test]
    fn empty_allowlist_excludes_everything() {
        let f = ToolNameFilter::new(Some(vec![]), vec![]);
        assert!(!f.passes("anything"));
    }

    #[test]
    fn deny_list_excludes_named_tools() {
        let f = ToolNameFilter::new(None, vec!["dangerous".into()]);
        assert!(!f.passes("dangerous"));
        assert!(f.passes("safe"));
    }

    // Deny wins when a name appears in both lists.
    #[test]
    fn deny_beats_allow() {
        let f = ToolNameFilter::new(
            Some(vec!["tool".into()]),
            vec!["tool".into()],
        );
        assert!(!f.passes("tool"));
    }

    #[test]
    fn allow_and_deny_are_case_insensitive() {
        let f = ToolNameFilter::new(Some(vec!["Read_File".into()]), vec!["WRITE".into()]);
        assert!(f.passes("read_file"));
        assert!(f.passes("READ_FILE"));
        assert!(!f.passes("write"));
        assert!(!f.passes("WRITE"));
    }

    // Merge intersects allow lists.
    #[test]
    fn merge_intersects_allow() {
        let user = ToolNameFilter::new(Some(vec!["a".into(), "b".into()]), vec![]);
        let project = ToolNameFilter::new(Some(vec!["b".into(), "c".into()]), vec![]);
        let merged = ToolNameFilter::merge(Some(user), Some(project));
        assert!(merged.passes("b"));
        assert!(!merged.passes("a"));
        assert!(!merged.passes("c"));
    }

    // Merge unions deny lists.
    #[test]
    fn merge_unions_deny() {
        let user = ToolNameFilter::new(None, vec!["a".into()]);
        let project = ToolNameFilter::new(None, vec!["b".into()]);
        let merged = ToolNameFilter::merge(Some(user), Some(project));
        assert!(!merged.passes("a"));
        assert!(!merged.passes("b"));
        assert!(merged.passes("c"));
    }

    #[test]
    fn merge_with_one_null_allow_uses_the_other() {
        let user = ToolNameFilter::new(Some(vec!["a".into()]), vec![]);
        let merged = ToolNameFilter::merge(Some(user), None);
        assert!(merged.passes("a"));
        assert!(!merged.passes("b"));
    }

    #[test]
    fn merge_null_null_is_permissive() {
        let merged = ToolNameFilter::merge(None, None);
        assert!(merged.passes("anything"));
    }

    #[test]
    fn apply_preserves_passing_entries() {
        let f = ToolNameFilter::new(Some(vec!["a".into(), "b".into()]), vec!["b".into()]);
        let result = f.apply(["a", "b", "c"]);
        assert_eq!(result, vec!["a"]);
    }
}
