//! LSP diagnostic types and the thread-safe registry that collects
//! `textDocument/publishDiagnostics` notifications.
//!
//! The registry bounds how many diagnostics are delivered per turn so a
//! server that emits hundreds of errors at once cannot flood the model
//! context.

use std::collections::{HashMap, VecDeque};
use std::sync::Mutex;

// ── Wire types ────────────────────────────────────────────────────────────────

/// A single LSP diagnostic, parsed from the `publishDiagnostics` params.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct LspDiagnostic {
    pub message: String,
    pub severity: LspDiagnosticSeverity,
    pub range: LspRange,
    pub source: Option<String>,
    pub code: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum LspDiagnosticSeverity {
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

impl LspDiagnosticSeverity {
    fn from_u8(n: u8) -> Self {
        match n {
            1 => Self::Error,
            2 => Self::Warning,
            3 => Self::Information,
            _ => Self::Hint,
        }
    }

    pub fn label(&self) -> &'static str {
        match self {
            Self::Error => "error",
            Self::Warning => "warning",
            Self::Information => "info",
            Self::Hint => "hint",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct LspRange {
    pub start: LspPosition,
    pub end: LspPosition,
}

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct LspPosition {
    pub line: u32,
    pub character: u32,
}

/// A set of diagnostics for one file, from one `publishDiagnostics` call.
#[derive(Debug, Clone)]
pub struct DiagnosticFile {
    pub uri: String,
    pub diagnostics: Vec<LspDiagnostic>,
}

// ── Parsing ───────────────────────────────────────────────────────────────────

impl LspDiagnostic {
    /// Parse a single diagnostic from a JSON value.
    pub fn parse(v: &serde_json::Value) -> Option<Self> {
        let message = v.get("message").and_then(|m| m.as_str())?.to_string();
        let severity = v
            .get("severity")
            .and_then(|s| s.as_u64())
            .map(|n| LspDiagnosticSeverity::from_u8(n as u8))
            .unwrap_or(LspDiagnosticSeverity::Error);

        let range_val = v.get("range")?;
        let start = parse_position(range_val.get("start")?)?;
        let end = parse_position(range_val.get("end")?)?;
        let range = LspRange { start, end };

        let source = v.get("source").and_then(|s| s.as_str()).map(str::to_string);
        let code = v.get("code").map(|c| {
            if let Some(s) = c.as_str() {
                s.to_string()
            } else {
                c.to_string()
            }
        });

        Some(LspDiagnostic { message, severity, range, source, code })
    }

    /// Parse the `params` value from a `textDocument/publishDiagnostics`
    /// notification, returning the file URI and list of diagnostics.
    pub fn parse_notification(params: &serde_json::Value) -> Option<(String, Vec<Self>)> {
        let uri = params.get("uri")?.as_str()?.to_string();
        let diags = params
            .get("diagnostics")
            .and_then(|d| d.as_array())
            .map(|arr| arr.iter().filter_map(Self::parse).collect())
            .unwrap_or_default();
        Some((uri, diags))
    }

    /// A stable dedup key (used by the registry for cross-turn dedup).
    fn key(&self) -> String {
        format!(
            "{}|{}|{}:{}|{}",
            self.message,
            self.severity as u8,
            self.range.start.line,
            self.range.start.character,
            self.source.as_deref().unwrap_or("")
        )
    }
}

fn parse_position(v: &serde_json::Value) -> Option<LspPosition> {
    let line = v.get("line")?.as_u64()? as u32;
    let character = v.get("character")?.as_u64()? as u32;
    Some(LspPosition { line, character })
}

// ── Registry ─────────────────────────────────────────────────────────────────

/// Limits matching C#:
const MAX_PER_FILE: usize = 10;
const MAX_TOTAL: usize = 30;
/// LRU capacity for cross-turn delivered-key tracking.
const MAX_TRACKED_FILES: usize = 500;

/// Collects `publishDiagnostics` notifications across turns and deduplicates
/// them, exposing only new diagnostics to the agent loop.
///
/// Thread-safe: `register_pending` is called from the LSP dispatch task;
/// `check_for_diagnostics` is called from the agent loop.
pub struct LspDiagnosticRegistry {
    inner: Mutex<RegistryInner>,
}

struct RegistryInner {
    pending: Vec<DiagnosticFile>,
    /// LRU map: file URI → set of diagnostic keys already delivered.
    delivered: LruMap<String, HashMap<String, ()>>,
}

impl Default for LspDiagnosticRegistry {
    fn default() -> Self {
        Self::new()
    }
}

impl LspDiagnosticRegistry {
    pub fn new() -> Self {
        Self {
            inner: Mutex::new(RegistryInner {
                pending: Vec::new(),
                delivered: LruMap::new(MAX_TRACKED_FILES),
            }),
        }
    }

    /// Called by the LSP notification handler (from the dispatch task) when a
    /// `publishDiagnostics` notification arrives.
    pub fn register_pending(&self, file: DiagnosticFile) {
        self.inner.lock().expect("poisoned").pending.push(file);
    }

    /// Returns new (undelivered) diagnostics, applies volume limits, marks
    /// them delivered, and clears the pending queue.
    ///
    /// Returns `None` when there are no new diagnostics.
    pub fn check_for_diagnostics(&self) -> Option<Vec<DiagnosticFile>> {
        let mut inner = self.inner.lock().expect("poisoned");
        if inner.pending.is_empty() {
            return None;
        }

        let pending = std::mem::take(&mut inner.pending);

        // Deduplicate within batch and against previously delivered.
        let mut batch_seen: HashMap<String, HashMap<String, ()>> = HashMap::new();
        let mut deduped: Vec<DiagnosticFile> = Vec::new();

        for file in &pending {
            let canonical = canonical_key(&file.uri);
            let batch_keys = batch_seen.entry(canonical.clone()).or_default();
            let prev_keys = inner.delivered.get(&canonical);

            let new_diags: Vec<LspDiagnostic> = file
                .diagnostics
                .iter()
                .filter(|d| {
                    let k = d.key();
                    if batch_keys.contains_key(&k) {
                        return false;
                    }
                    if prev_keys.map_or(false, |m| m.contains_key(&k)) {
                        return false;
                    }
                    batch_keys.insert(k, ());
                    true
                })
                .cloned()
                .collect();

            if !new_diags.is_empty() {
                deduped.push(DiagnosticFile { uri: file.uri.clone(), diagnostics: new_diags });
            }
        }

        if deduped.is_empty() {
            return None;
        }

        // Volume limit: sort each file's diags by severity, cap per file + total.
        let mut result: Vec<DiagnosticFile> = Vec::new();
        let mut total = 0usize;

        for mut file in deduped {
            if total >= MAX_TOTAL {
                break;
            }
            file.diagnostics.sort_by_key(|d| d.severity as u8);
            let available = MAX_TOTAL - total;
            file.diagnostics.truncate(MAX_PER_FILE.min(available));
            total += file.diagnostics.len();
            result.push(file);
        }

        // Mark as delivered.
        for file in &result {
            let canonical = canonical_key(&file.uri);
            let keys = inner.delivered.get_or_insert_mut(&canonical, HashMap::new());
            for d in &file.diagnostics {
                keys.insert(d.key(), ());
            }
        }

        Some(result)
    }

    /// Reset all state (pending + delivered).
    pub fn reset(&self) {
        let mut inner = self.inner.lock().expect("poisoned");
        inner.pending.clear();
        inner.delivered.clear();
    }

    /// Remove cross-turn delivered tracking for one file so its diagnostics
    /// will re-surface on the next `check_for_diagnostics`.
    pub fn clear_for_file(&self, uri: &str) {
        let canonical = canonical_key(uri);
        self.inner.lock().expect("poisoned").delivered.remove(&canonical);
    }
}

fn canonical_key(uri: &str) -> String {
    // Normalise file:// URIs to their path so publishDiagnostics URIs and
    // agent-side file paths can be compared case-insensitively on Windows.
    let path = if let Some(rest) = uri.strip_prefix("file:///") {
        rest.replace('/', "\\")
    } else if let Some(rest) = uri.strip_prefix("file://") {
        rest.replace('/', "\\")
    } else {
        uri.to_string()
    };

    if cfg!(windows) { path.to_lowercase() } else { path }
}

// ── Minimal LRU map ───────────────────────────────────────────────────────────

/// A bounded map that evicts the least-recently-used entry when capacity is
/// exceeded. Used only for the diagnostic delivered-key tracking.
struct LruMap<K, V> {
    capacity: usize,
    // Store keys in insertion/access order; the front is the LRU entry.
    order: VecDeque<K>,
    map: HashMap<K, V>,
}

impl<K: Eq + std::hash::Hash + Clone, V> LruMap<K, V> {
    fn new(capacity: usize) -> Self {
        Self { capacity, order: VecDeque::new(), map: HashMap::new() }
    }

    fn get(&self, key: &K) -> Option<&V> {
        self.map.get(key)
    }

    fn get_or_insert_mut(&mut self, key: &K, default: V) -> &mut V {
        if !self.map.contains_key(key) {
            self.insert(key.clone(), default);
        } else {
            // Move to back (most recently used).
            self.order.retain(|k| k != key);
            self.order.push_back(key.clone());
        }
        self.map.get_mut(key).expect("just inserted")
    }

    fn insert(&mut self, key: K, value: V) {
        if self.map.contains_key(&key) {
            self.order.retain(|k| k != &key);
        } else if self.map.len() >= self.capacity {
            if let Some(evicted) = self.order.pop_front() {
                self.map.remove(&evicted);
            }
        }
        self.order.push_back(key.clone());
        self.map.insert(key, value);
    }

    fn remove(&mut self, key: &K) {
        if self.map.remove(key).is_some() {
            self.order.retain(|k| k != key);
        }
    }

    fn clear(&mut self) {
        self.map.clear();
        self.order.clear();
    }
}

// ── Tests ────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn make_diag(msg: &str, severity: LspDiagnosticSeverity) -> LspDiagnostic {
        LspDiagnostic {
            message: msg.to_string(),
            severity,
            range: LspRange {
                start: LspPosition { line: 0, character: 0 },
                end: LspPosition { line: 0, character: 1 },
            },
            source: None,
            code: None,
        }
    }

    fn make_file(uri: &str, diags: Vec<LspDiagnostic>) -> DiagnosticFile {
        DiagnosticFile { uri: uri.to_string(), diagnostics: diags }
    }

    #[test]
    fn empty_registry_returns_none() {
        let reg = LspDiagnosticRegistry::new();
        assert!(reg.check_for_diagnostics().is_none());
    }

    #[test]
    fn new_diagnostics_are_returned_once() {
        let reg = LspDiagnosticRegistry::new();
        reg.register_pending(make_file("file:///a.rs", vec![make_diag("err", LspDiagnosticSeverity::Error)]));

        let first = reg.check_for_diagnostics().expect("should have diags");
        assert_eq!(first.len(), 1);

        // Second call: same diagnostic → already delivered.
        reg.register_pending(make_file("file:///a.rs", vec![make_diag("err", LspDiagnosticSeverity::Error)]));
        assert!(reg.check_for_diagnostics().is_none(), "duplicate must be suppressed");
    }

    #[test]
    fn different_messages_are_not_deduped() {
        let reg = LspDiagnosticRegistry::new();
        reg.register_pending(make_file(
            "file:///a.rs",
            vec![
                make_diag("first error", LspDiagnosticSeverity::Error),
                make_diag("second error", LspDiagnosticSeverity::Error),
            ],
        ));

        let result = reg.check_for_diagnostics().unwrap();
        assert_eq!(result[0].diagnostics.len(), 2);
    }

    #[test]
    fn volume_limit_caps_at_max_per_file() {
        let reg = LspDiagnosticRegistry::new();
        let many: Vec<LspDiagnostic> = (0..15)
            .map(|i| make_diag(&format!("err{i}"), LspDiagnosticSeverity::Error))
            .collect();
        reg.register_pending(make_file("file:///a.rs", many));

        let result = reg.check_for_diagnostics().unwrap();
        assert!(result[0].diagnostics.len() <= MAX_PER_FILE, "exceeded per-file limit");
    }

    #[test]
    fn volume_limit_caps_total_across_files() {
        let reg = LspDiagnosticRegistry::new();
        for i in 0..10 {
            let diags: Vec<_> = (0..5)
                .map(|j| make_diag(&format!("err{j}"), LspDiagnosticSeverity::Error))
                .collect();
            reg.register_pending(make_file(&format!("file:///f{i}.rs"), diags));
        }

        let result = reg.check_for_diagnostics().unwrap();
        let total: usize = result.iter().map(|f| f.diagnostics.len()).sum();
        assert!(total <= MAX_TOTAL, "exceeded total limit: {total}");
    }

    #[test]
    fn clear_for_file_allows_re_delivery() {
        let reg = LspDiagnosticRegistry::new();
        let diag = make_diag("persistent", LspDiagnosticSeverity::Error);

        reg.register_pending(make_file("file:///a.rs", vec![diag.clone()]));
        let _ = reg.check_for_diagnostics(); // delivered

        reg.clear_for_file("file:///a.rs");

        reg.register_pending(make_file("file:///a.rs", vec![diag]));
        assert!(
            reg.check_for_diagnostics().is_some(),
            "after clear_for_file the diagnostic should re-surface"
        );
    }

    #[test]
    fn reset_clears_all_state() {
        let reg = LspDiagnosticRegistry::new();
        reg.register_pending(make_file("file:///a.rs", vec![make_diag("e", LspDiagnosticSeverity::Error)]));
        let _ = reg.check_for_diagnostics();
        reg.reset();

        // After reset, previously delivered diagnostics re-surface.
        reg.register_pending(make_file("file:///a.rs", vec![make_diag("e", LspDiagnosticSeverity::Error)]));
        assert!(reg.check_for_diagnostics().is_some());
    }

    #[test]
    fn parse_diagnostic_from_json() {
        let v = json!({
            "message": "cannot find value",
            "severity": 1,
            "range": {
                "start": { "line": 5, "character": 3 },
                "end": { "line": 5, "character": 10 }
            },
            "source": "rustc"
        });
        let d = LspDiagnostic::parse(&v).unwrap();
        assert_eq!(d.message, "cannot find value");
        assert_eq!(d.severity, LspDiagnosticSeverity::Error);
        assert_eq!(d.range.start.line, 5);
        assert_eq!(d.source.as_deref(), Some("rustc"));
    }

    #[test]
    fn parse_notification_returns_uri_and_diags() {
        let params = json!({
            "uri": "file:///src/main.rs",
            "diagnostics": [{
                "message": "err",
                "severity": 1,
                "range": {
                    "start": { "line": 0, "character": 0 },
                    "end": { "line": 0, "character": 1 }
                }
            }]
        });
        let (uri, diags) = LspDiagnostic::parse_notification(&params).unwrap();
        assert_eq!(uri, "file:///src/main.rs");
        assert_eq!(diags.len(), 1);
    }
}
