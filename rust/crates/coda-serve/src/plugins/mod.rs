//! Claude-format plugin discovery.
//!
//! One step finds every installed plugin and resolves which component
//! directories it contributes; everything else (skills, agents, MCP, hooks,
//! LSP, `plugins/list`, the `coda plugin` CLI) consumes the resulting
//! [`PluginSet`].
//!
//! # Discovery order
//!
//! user root (`~/.coda/plugins/<dir>`) → settings `plugins.paths` →
//! settings `plugins.marketplaces` → project root
//! (`<workspace>/.coda/plugins/<dir>`). A later plugin with the same name
//! replaces an earlier one (matching skill precedence) and the replacement is
//! recorded on the winner, never silent.
//!
//! # Failure isolation
//!
//! Every plugin is parsed on its own. Any problem is recorded in that
//! plugin's `errors` and discovery carries on; a malformed plugin is still
//! listed, it just contributes nothing.

pub mod cli;
pub mod components;
pub mod install;
pub mod manifest;
pub mod settings;
pub mod trust;
pub mod wiring;

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use serde_json::{json, Value};

pub use manifest::PluginManifest;
pub use settings::{MarketplaceKind, MarketplaceRef, PluginSettings};

/// Where a plugin was found.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PluginSource {
    User,
    Project,
    Path,
    Marketplace(String),
}

impl PluginSource {
    /// Wire form: `user`, `project`, `path` or `marketplace:<id>`.
    pub fn label(&self) -> String {
        match self {
            PluginSource::User => "user".into(),
            PluginSource::Project => "project".into(),
            PluginSource::Path => "path".into(),
            PluginSource::Marketplace(id) => format!("marketplace:{id}"),
        }
    }

    /// Project plugins come from an attacker-controllable checkout.
    pub fn is_project(&self) -> bool {
        matches!(self, PluginSource::Project)
    }
}

/// One discovered plugin.
#[derive(Debug, Clone)]
pub struct PluginDescriptor {
    pub name: String,
    pub version: Option<String>,
    pub description: Option<String>,
    /// Canonical plugin directory.
    pub root: PathBuf,
    pub source: PluginSource,
    /// `None` when the manifest could not be parsed (see `errors`).
    pub manifest: Option<PluginManifest>,
    /// The manifest file's bytes; approvals are keyed by them.
    pub manifest_bytes: Vec<u8>,
    pub enabled: bool,
    pub errors: Vec<String>,
    /// Informational notes, e.g. that this plugin replaced another.
    pub notes: Vec<String>,
    /// Existing skill directories (each holds `<skill>/SKILL.md`).
    pub skill_dirs: Vec<PathBuf>,
    /// Existing command directories (each holds `*.md`).
    pub command_dirs: Vec<PathBuf>,
    /// Existing agent directories (each holds `*.md`).
    pub agent_dirs: Vec<PathBuf>,
    /// Components that execute or connect; each needs approval.
    pub exec: Vec<trust::ExecComponent>,
}

impl PluginDescriptor {
    /// Contributes components: enabled and with a usable manifest.
    pub fn is_active(&self) -> bool {
        self.enabled && self.manifest.is_some()
    }
}

/// Everything discovery found, plus settings-level problems.
#[derive(Debug, Clone, Default)]
pub struct PluginSet {
    /// Sorted by name.
    pub plugins: Vec<PluginDescriptor>,
    /// Problems not attributable to one plugin (settings shape, missing paths).
    pub errors: Vec<String>,
}

impl PluginSet {
    pub fn empty() -> Self {
        Self::default()
    }

    /// Plugins that contribute components.
    pub fn active(&self) -> impl Iterator<Item = &PluginDescriptor> {
        self.plugins.iter().filter(|p| p.is_active())
    }

    pub fn find(&self, name: &str) -> Option<&PluginDescriptor> {
        self.plugins.iter().find(|p| p.name.eq_ignore_ascii_case(name))
    }
}

/// Discovers the plugins visible from `workspace` with the user's settings
/// (`<CODA_HOME>/.coda/settings.json`).
pub fn load_for_workspace(workspace: &Path) -> PluginSet {
    let coda_dir = coda_auth::coda_dir();
    let settings = crate::settings::load_settings_json(&coda_dir.join("settings.json"));
    discover_from_settings(workspace, &coda_dir, &settings)
}

/// Discovers plugins using the user settings file and legacy state under
/// `coda_dir`.
pub fn discover_from_settings(workspace: &Path, coda_dir: &Path, settings: &Value) -> PluginSet {
    let (plugin_settings, errors) = PluginSettings::from_settings(settings, coda_dir);
    let plugin_settings = plugin_settings.with_legacy_state(coda_dir);
    let mut set = discover_plugins(workspace, coda_dir, &plugin_settings);
    set.errors.splice(0..0, errors);
    set
}

/// The discovery step. See the module documentation for the order.
pub fn discover_plugins(workspace: &Path, coda_dir: &Path, settings: &PluginSettings) -> PluginSet {
    let mut errors = Vec::new();
    let mut candidates: Vec<(PathBuf, PluginSource)> = Vec::new();

    for root in plugin_children(&coda_dir.join("plugins")) {
        candidates.push((root, PluginSource::User));
    }

    for path in &settings.paths {
        if !path.is_dir() {
            errors.push(format!("plugin path not found: {}", path.display()));
            continue;
        }
        if manifest::manifest_path(path).is_some() {
            candidates.push((path.clone(), PluginSource::Path));
            continue;
        }
        let children = plugin_children(path);
        if children.is_empty() {
            errors.push(format!("no plugin found at {}", path.display()));
        }
        candidates.extend(children.into_iter().map(|c| (c, PluginSource::Path)));
    }

    for market in &settings.marketplaces {
        let source = PluginSource::Marketplace(market.id.clone());
        match marketplace_plugins(market) {
            Ok((roots, problems)) => {
                errors.extend(problems);
                candidates.extend(roots.into_iter().map(|r| (r, source.clone())));
            }
            Err(e) => errors.push(e),
        }
    }

    for root in plugin_children(&workspace.join(".coda").join("plugins")) {
        candidates.push((root, PluginSource::Project));
    }

    let mut by_name: BTreeMap<String, PluginDescriptor> = BTreeMap::new();
    for (root, source) in candidates {
        let mut descriptor = load_descriptor(&root, source, settings);
        let key = descriptor.name.to_ascii_lowercase();
        if let Some(previous) = by_name.remove(&key) {
            if previous.root == descriptor.root {
                // Listed twice (e.g. a path inside a marketplace): keep the
                // first, more specific, source.
                by_name.insert(key, previous);
                continue;
            }
            descriptor.notes.push(format!(
                "overrides the {} plugin at {}",
                previous.source.label(),
                previous.root.display()
            ));
        }
        by_name.insert(key, descriptor);
    }

    PluginSet { plugins: by_name.into_values().collect(), errors }
}

/// Immediate subdirectories of `dir` that are plugin directories. Hidden
/// directories (staging areas, `.git`) are skipped.
fn plugin_children(dir: &Path) -> Vec<PathBuf> {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return Vec::new();
    };
    let mut out: Vec<PathBuf> = entries
        .flatten()
        .filter(|e| e.file_type().map(|t| t.is_dir()).unwrap_or(false))
        .filter(|e| !e.file_name().to_string_lossy().starts_with('.'))
        .map(|e| e.path())
        .filter(|p| manifest::manifest_path(p).is_some())
        .collect();
    out.sort();
    out
}

/// Plugin roots offered by a marketplace: the entries of
/// `.claude-plugin/marketplace.json` with relative sources, or every plugin
/// under `<path>/plugins/` when there is no catalogue file.
fn marketplace_plugins(market: &MarketplaceRef) -> Result<(Vec<PathBuf>, Vec<String>), String> {
    if !market.path.is_dir() {
        return Err(match &market.kind {
            MarketplaceKind::Directory => {
                format!("marketplace '{}' not found at {}", market.id, market.path.display())
            }
            MarketplaceKind::Git { .. } => format!(
                "marketplace '{}' is not cloned yet; run `coda plugin marketplace update {}`",
                market.id, market.id
            ),
        });
    }
    let catalogue = market.path.join(".claude-plugin").join("marketplace.json");
    if !catalogue.is_file() {
        let mut roots = plugin_children(&market.path.join("plugins"));
        if roots.is_empty() && manifest::manifest_path(&market.path).is_some() {
            roots.push(market.path.clone());
        }
        return Ok((roots, Vec::new()));
    }
    let text = std::fs::read_to_string(&catalogue)
        .map_err(|e| format!("marketplace '{}': cannot read marketplace.json: {e}", market.id))?;
    let doc: Value = serde_json::from_str(text.trim_start_matches('\u{feff}'))
        .map_err(|e| format!("marketplace '{}': invalid marketplace.json: {e}", market.id))?;
    let mut roots = Vec::new();
    let mut problems = Vec::new();
    for entry in doc.get("plugins").and_then(Value::as_array).into_iter().flatten() {
        let name = entry.get("name").and_then(Value::as_str).unwrap_or("?");
        match entry.get("source") {
            Some(Value::String(rel)) => match manifest::resolve_contained(&market.path, rel) {
                Ok(root) if manifest::manifest_path(&root).is_some() => roots.push(root),
                Ok(root) => problems.push(format!(
                    "marketplace '{}': plugin '{name}' has no plugin.json at {}",
                    market.id,
                    root.display()
                )),
                Err(e) => problems.push(format!("marketplace '{}': plugin '{name}': {e}", market.id)),
            },
            _ => problems.push(format!(
                "marketplace '{}': plugin '{name}' uses a remote source; only relative directory sources are supported",
                market.id
            )),
        }
    }
    Ok((roots, problems))
}

fn load_descriptor(root: &Path, source: PluginSource, settings: &PluginSettings) -> PluginDescriptor {
    let root = canonical(root);
    let dir_name = root.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let mut descriptor = PluginDescriptor {
        name: dir_name,
        version: None,
        description: None,
        root: root.clone(),
        source,
        manifest: None,
        manifest_bytes: Vec::new(),
        enabled: false,
        errors: Vec::new(),
        notes: Vec::new(),
        skill_dirs: Vec::new(),
        command_dirs: Vec::new(),
        agent_dirs: Vec::new(),
        exec: Vec::new(),
    };
    match manifest::load(&root) {
        Some(Ok((m, bytes))) => {
            descriptor.name = m.name.clone();
            descriptor.version = m.version.clone();
            descriptor.description = m.description.clone();
            descriptor.skill_dirs = component_dirs(&root, "skills", &m.skills, &mut descriptor.errors);
            descriptor.command_dirs =
                component_dirs(&root, "commands", &m.commands, &mut descriptor.errors);
            descriptor.agent_dirs = component_dirs(&root, "agents", &m.agents, &mut descriptor.errors);
            descriptor.exec = components::exec_components(&root, &m, &mut descriptor.errors);
            descriptor.manifest = Some(m);
            descriptor.manifest_bytes = bytes;
        }
        Some(Err(e)) => descriptor.errors.push(e),
        None => descriptor.errors.push("no plugin.json found".into()),
    }
    descriptor.enabled = settings.is_enabled(&descriptor.name, &descriptor.source);
    descriptor
}

/// The default directory plus the declared ones, existing and contained only.
fn component_dirs(root: &Path, default: &str, declared: &[String], errors: &mut Vec<String>) -> Vec<PathBuf> {
    let mut dirs: Vec<PathBuf> = Vec::new();
    let default_dir = root.join(default);
    if default_dir.is_dir() {
        dirs.push(canonical(&default_dir));
    }
    for rel in declared {
        match manifest::resolve_contained(root, rel) {
            Ok(dir) if dir.is_dir() => {
                if !dirs.contains(&dir) {
                    dirs.push(dir);
                }
            }
            Ok(_) => errors.push(format!("{default} path not found: {rel}")),
            Err(e) => errors.push(e),
        }
    }
    dirs
}

/// `canonicalize` without the Windows verbatim (`\\?\C:\`) prefix, which
/// would leak into prompts and break `cmd.exe`. Falls back to the input.
pub fn canonical(path: &Path) -> PathBuf {
    let Ok(c) = path.canonicalize() else {
        return path.to_path_buf();
    };
    let s = c.to_string_lossy();
    if let Some(rest) = s.strip_prefix(r"\\?\") {
        if rest.as_bytes().get(1) == Some(&b':') {
            return PathBuf::from(rest);
        }
    }
    c
}

/// Expands `${CLAUDE_PLUGIN_ROOT}` and `${CODA_PLUGIN_ROOT}`.
pub fn expand_root(text: &str, root: &Path) -> String {
    let root = root.to_string_lossy();
    text.replace("${CLAUDE_PLUGIN_ROOT}", &root).replace("${CODA_PLUGIN_ROOT}", &root)
}

/// The `plugins/list` item for one plugin.
pub fn inventory_item(
    plugin: &PluginDescriptor,
    components: Value,
    trust: &str,
    pending: &[String],
) -> Value {
    json!({
        "name": plugin.name,
        "version": plugin.version,
        "description": plugin.description,
        "source": plugin.source.label(),
        "root": plugin.root.to_string_lossy(),
        "enabled": plugin.enabled,
        "trusted": pending.is_empty(),
        "trust": trust,
        "isExternal": false,
        "components": components,
        "pendingApproval": pending,
        "notes": plugin.notes,
        "errors": plugin.errors,
    })
}

#[cfg(test)]
pub(crate) mod test_support {
    use std::path::Path;

    pub fn write(path: &Path, text: &str) {
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(path, text).unwrap();
    }

    /// A Claude-layout plugin with one skill and one agent.
    pub fn plugin(dir: &Path, name: &str) {
        write(&dir.join(".claude-plugin/plugin.json"), &format!(r#"{{"name":"{name}","version":"1.0.0"}}"#));
        write(
            &dir.join("skills/review/SKILL.md"),
            &format!("---\nname: review\ndescription: review from {name}\n---\nBody of {name}\n"),
        );
        write(&dir.join("agents/skeptic.md"), "---\nname: skeptic\ndescription: doubts\n---\nYou doubt.\n");
    }
}

#[cfg(test)]
mod tests {
    use super::test_support::{plugin, write};
    use super::*;

    fn settings() -> PluginSettings {
        PluginSettings::default()
    }

    #[test]
    fn missing_roots_yield_nothing() {
        let ws = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let set = discover_plugins(ws.path(), home.path(), &settings());
        assert!(set.plugins.is_empty());
        assert!(set.errors.is_empty());
    }

    #[test]
    fn every_source_is_discovered_and_labelled() {
        let ws = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let elsewhere = tempfile::tempdir().unwrap();
        plugin(&home.path().join("plugins/u"), "user-one");
        plugin(&ws.path().join(".coda/plugins/p"), "project-one");
        plugin(&elsewhere.path().join("direct"), "path-one");
        plugin(&elsewhere.path().join("bundle/inner"), "path-two");
        plugin(&elsewhere.path().join("market/plugins/m"), "market-one");

        let mut s = settings();
        s.paths = vec![elsewhere.path().join("direct"), elsewhere.path().join("bundle")];
        s.marketplaces = vec![MarketplaceRef {
            id: "local".into(),
            kind: MarketplaceKind::Directory,
            path: elsewhere.path().join("market"),
        }];
        let set = discover_plugins(ws.path(), home.path(), &s);
        let labels: Vec<(String, String, bool)> =
            set.plugins.iter().map(|p| (p.name.clone(), p.source.label(), p.enabled)).collect();
        assert_eq!(
            labels,
            vec![
                ("market-one".into(), "marketplace:local".into(), false),
                ("path-one".into(), "path".into(), true),
                ("path-two".into(), "path".into(), true),
                ("project-one".into(), "project".into(), true),
                ("user-one".into(), "user".into(), true),
            ]
        );
        let user = set.find("user-one").unwrap();
        assert_eq!(user.skill_dirs.len(), 1);
        assert_eq!(user.agent_dirs.len(), 1);
        assert!(set.errors.is_empty(), "{:?}", set.errors);
    }

    #[test]
    fn a_marketplace_catalogue_lists_relative_sources_only() {
        let ws = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let m = tempfile::tempdir().unwrap();
        plugin(&m.path().join("plugins/sec"), "security-review");
        write(
            &m.path().join(".claude-plugin/marketplace.json"),
            r#"{"name":"m","plugins":[
                {"name":"security-review","source":"./plugins/sec"},
                {"name":"remote","source":{"source":"github","repo":"o/r"}},
                {"name":"escape","source":"../outside"}
            ]}"#,
        );
        let mut s = settings();
        s.enabled.insert("security-review".into(), true);
        s.marketplaces = vec![MarketplaceRef { id: "m".into(), kind: MarketplaceKind::Directory, path: m.path().into() }];
        let set = discover_plugins(ws.path(), home.path(), &s);
        assert_eq!(set.plugins.len(), 1);
        assert!(set.plugins[0].enabled);
        assert_eq!(set.errors.len(), 2, "{:?}", set.errors);
    }

    #[test]
    fn a_later_source_replaces_an_earlier_one_visibly() {
        let ws = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        plugin(&home.path().join("plugins/a"), "same");
        plugin(&ws.path().join(".coda/plugins/a"), "same");
        let set = discover_plugins(ws.path(), home.path(), &settings());
        assert_eq!(set.plugins.len(), 1);
        assert_eq!(set.plugins[0].source, PluginSource::Project);
        assert!(set.plugins[0].notes[0].contains("overrides the user plugin"));
    }

    #[test]
    fn a_malformed_plugin_does_not_hide_the_others() {
        let ws = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        plugin(&home.path().join("plugins/good"), "good");
        write(&home.path().join("plugins/bad/.claude-plugin/plugin.json"), "{ nope");
        let set = discover_plugins(ws.path(), home.path(), &settings());
        assert_eq!(set.plugins.len(), 2);
        let bad = set.find("bad").unwrap();
        assert!(!bad.is_active());
        assert!(bad.errors[0].contains("invalid plugin.json"));
        assert!(set.find("good").unwrap().is_active());
    }

    #[test]
    fn traversal_in_a_component_path_is_rejected_without_dropping_the_plugin() {
        let ws = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let root = home.path().join("plugins/t");
        plugin(&root, "t");
        write(&root.join(".claude-plugin/plugin.json"), r#"{"name":"t","skills":["../../x"]}"#);
        let set = discover_plugins(ws.path(), home.path(), &settings());
        let t = set.find("t").unwrap();
        assert!(t.errors.iter().any(|e| e.contains("escapes")), "{:?}", t.errors);
        assert_eq!(t.skill_dirs.len(), 1, "the default skills dir still loads");
    }

    #[test]
    fn missing_settings_paths_are_reported() {
        let ws = tempfile::tempdir().unwrap();
        let home = tempfile::tempdir().unwrap();
        let mut s = settings();
        s.paths = vec![home.path().join("nope")];
        let set = discover_plugins(ws.path(), home.path(), &s);
        assert!(set.errors[0].contains("plugin path not found"));
    }

    #[test]
    fn root_variables_are_expanded() {
        let root = Path::new("C:/p");
        assert_eq!(expand_root("${CLAUDE_PLUGIN_ROOT}/a ${CODA_PLUGIN_ROOT}", root), "C:/p/a C:/p");
    }
}
