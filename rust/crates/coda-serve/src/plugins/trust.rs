//! Approvals for executable plugin components, in `~/.coda/plugin-trust.json`.
//!
//! Skills, commands and agents are prompt content and need no approval.
//! Components that execute or connect — each MCP server, each LSP server and
//! a plugin's hooks — do. An approval is keyed by the SHA-256 of the plugin
//! manifest bytes plus the component's kind, name and canonical definition
//! (plus the plugin root for project plugins, which binds an approval to one
//! checkout), so any change to either asks again.
//!
//! The file keeps the shape older builds wrote:
//! `{ "pluginApprovals": { "<hash>": ["mcpServer", …] } }`. Unknown top-level
//! keys and entries are preserved on write.

use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};

use serde_json::{Map, Value};
use sha2::{Digest, Sha256};

use super::PluginDescriptor;

pub const KIND_MCP_SERVER: &str = "mcpServer";
pub const KIND_LSP_SERVER: &str = "lspServer";
pub const KIND_HOOK: &str = "hook";

/// One executable component of a plugin.
#[derive(Debug, Clone, PartialEq)]
pub struct ExecComponent {
    /// [`KIND_MCP_SERVER`], [`KIND_LSP_SERVER`] or [`KIND_HOOK`].
    pub kind: &'static str,
    /// Server name, or `hooks` for the hooks block.
    pub name: String,
    /// The definition as written (before variable expansion).
    pub definition: Value,
}

impl ExecComponent {
    /// `kind:name`, as shown in `pendingApproval`.
    pub fn label(&self) -> String {
        format!("{}:{}", self.kind, self.name)
    }
}

/// The approval key for `component` of `plugin`.
pub fn approval_key(plugin: &PluginDescriptor, component: &ExecComponent) -> String {
    let mut hasher = Sha256::new();
    hasher.update(&plugin.manifest_bytes);
    hasher.update([0]);
    hasher.update(component.kind.as_bytes());
    hasher.update([0]);
    hasher.update(component.name.as_bytes());
    hasher.update([0]);
    hasher.update(canonical_json(&component.definition).as_bytes());
    if plugin.source.is_project() {
        hasher.update([0]);
        hasher.update(plugin.root.to_string_lossy().to_lowercase().as_bytes());
    }
    hex(&hasher.finalize())
}

/// JSON with object keys sorted, so key order does not change the hash.
fn canonical_json(v: &Value) -> String {
    fn sort(v: &Value) -> Value {
        match v {
            Value::Object(m) => {
                let sorted: BTreeMap<&String, Value> = m.iter().map(|(k, v)| (k, sort(v))).collect();
                let mut out = Map::new();
                for (k, v) in sorted {
                    out.insert(k.clone(), v);
                }
                Value::Object(out)
            }
            Value::Array(a) => Value::Array(a.iter().map(sort).collect()),
            other => other.clone(),
        }
    }
    sort(v).to_string()
}

pub(crate) fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// Whether a plugin's executable components are approved.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrustState {
    /// The plugin has no executable components.
    NotRequired,
    Approved,
    Partial,
    Pending,
}

impl TrustState {
    pub fn as_str(self) -> &'static str {
        match self {
            TrustState::NotRequired => "notRequired",
            TrustState::Approved => "approved",
            TrustState::Partial => "partial",
            TrustState::Pending => "pending",
        }
    }
}

/// The persisted approvals.
#[derive(Debug, Clone, Default)]
pub struct PluginTrustStore {
    path: PathBuf,
    approvals: BTreeMap<String, BTreeSet<String>>,
}

impl PluginTrustStore {
    /// Loads `<coda_dir>/plugin-trust.json`. A missing or unreadable file is
    /// an empty store: nothing is approved.
    pub fn load(coda_dir: &Path) -> Self {
        let path = coda_dir.join("plugin-trust.json");
        let approvals = read_doc(&path)
            .get("pluginApprovals")
            .and_then(Value::as_object)
            .map(|m| {
                m.iter()
                    .map(|(k, v)| {
                        let kinds = v
                            .as_array()
                            .map(|a| a.iter().filter_map(Value::as_str).map(str::to_owned).collect())
                            .unwrap_or_default();
                        (k.clone(), kinds)
                    })
                    .collect()
            })
            .unwrap_or_default();
        Self { path, approvals }
    }

    pub fn is_approved(&self, plugin: &PluginDescriptor, component: &ExecComponent) -> bool {
        self.approvals
            .get(&approval_key(plugin, component))
            .is_some_and(|kinds| kinds.contains(component.kind))
    }

    pub fn approve(&mut self, plugin: &PluginDescriptor, component: &ExecComponent) {
        self.approvals
            .entry(approval_key(plugin, component))
            .or_default()
            .insert(component.kind.to_owned());
    }

    /// The components of `plugin` that are not approved yet.
    pub fn pending<'a>(&self, plugin: &'a PluginDescriptor) -> Vec<&'a ExecComponent> {
        plugin.exec.iter().filter(|c| !self.is_approved(plugin, c)).collect()
    }

    pub fn state(&self, plugin: &PluginDescriptor) -> TrustState {
        if plugin.exec.is_empty() {
            return TrustState::NotRequired;
        }
        match self.pending(plugin).len() {
            0 => TrustState::Approved,
            n if n == plugin.exec.len() => TrustState::Pending,
            _ => TrustState::Partial,
        }
    }

    /// Writes the store, preserving every key it does not own.
    pub fn save(&self) -> std::io::Result<()> {
        let mut doc = read_doc(&self.path);
        if !doc.is_object() {
            doc = Value::Object(Map::new());
        }
        let approvals: Map<String, Value> = self
            .approvals
            .iter()
            .map(|(k, kinds)| (k.clone(), Value::Array(kinds.iter().cloned().map(Value::String).collect())))
            .collect();
        doc.as_object_mut()
            .expect("object")
            .insert("pluginApprovals".into(), Value::Object(approvals));
        if let Some(dir) = self.path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let tmp = self.path.with_extension("json.tmp");
        std::fs::write(&tmp, serde_json::to_string_pretty(&doc).unwrap_or_default())?;
        std::fs::rename(&tmp, &self.path)
    }
}

fn read_doc(path: &Path) -> Value {
    std::fs::read_to_string(path)
        .ok()
        .and_then(|t| serde_json::from_str(t.trim_start_matches('\u{feff}')).ok())
        .unwrap_or(Value::Null)
}

#[cfg(test)]
pub(crate) fn test_plugin(manifest: &str, source: super::PluginSource, exec: Vec<ExecComponent>) -> PluginDescriptor {
    PluginDescriptor {
        name: "p".into(),
        version: None,
        description: None,
        root: PathBuf::from("C:/Plugins/P"),
        source,
        manifest: None,
        manifest_bytes: manifest.as_bytes().to_vec(),
        enabled: true,
        errors: vec![],
        notes: vec![],
        skill_dirs: vec![],
        command_dirs: vec![],
        agent_dirs: vec![],
        exec,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::plugins::PluginSource;
    use serde_json::json;

    fn plugin(manifest: &str, source: PluginSource, exec: Vec<ExecComponent>) -> PluginDescriptor {
        test_plugin(manifest, source, exec)
    }

    fn server(cmd: &str) -> ExecComponent {
        ExecComponent { kind: KIND_MCP_SERVER, name: "s".into(), definition: json!({"command": cmd, "args": []}) }
    }

    #[test]
    fn any_change_to_the_manifest_or_component_changes_the_key() {
        let base = plugin(r#"{"name":"p"}"#, PluginSource::User, vec![]);
        let key = approval_key(&base, &server("a"));
        assert_eq!(key, approval_key(&base, &server("a")), "stable");
        assert_ne!(key, approval_key(&base, &server("b")), "component change");
        let edited = plugin(r#"{"name":"p","version":"2"}"#, PluginSource::User, vec![]);
        assert_ne!(key, approval_key(&edited, &server("a")), "manifest change");
        let reordered = ExecComponent { definition: json!({"args": [], "command": "a"}), ..server("a") };
        assert_eq!(key, approval_key(&base, &reordered), "key order is irrelevant");
    }

    #[test]
    fn project_approvals_are_bound_to_the_checkout() {
        let a = plugin("{}", PluginSource::Project, vec![]);
        let mut b = a.clone();
        b.root = PathBuf::from("C:/Other/P");
        assert_ne!(approval_key(&a, &server("x")), approval_key(&b, &server("x")));
        let mut u = a.clone();
        u.source = PluginSource::User;
        let mut v = u.clone();
        v.root = PathBuf::from("C:/Other/P");
        assert_eq!(approval_key(&u, &server("x")), approval_key(&v, &server("x")));
    }

    #[test]
    fn approvals_round_trip_and_preserve_existing_entries() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(
            dir.path().join("plugin-trust.json"),
            r#"{"pluginApprovals":{"old":["skill"]},"other":1}"#,
        )
        .unwrap();
        let p = plugin("{}", PluginSource::User, vec![server("a"), server("b")]);
        let mut store = PluginTrustStore::load(dir.path());
        assert_eq!(store.state(&p), TrustState::Pending);
        store.approve(&p, &p.exec[0].clone());
        assert_eq!(store.state(&p), TrustState::Partial);
        store.save().unwrap();

        let reloaded = PluginTrustStore::load(dir.path());
        assert!(reloaded.is_approved(&p, &p.exec[0]));
        assert!(!reloaded.is_approved(&p, &p.exec[1]));
        let doc: Value =
            serde_json::from_str(&std::fs::read_to_string(dir.path().join("plugin-trust.json")).unwrap()).unwrap();
        assert_eq!(doc["other"], 1);
        assert_eq!(doc["pluginApprovals"]["old"][0], "skill");
        assert_eq!(reloaded.state(&plugin("{}", PluginSource::User, vec![])), TrustState::NotRequired);
    }
}
