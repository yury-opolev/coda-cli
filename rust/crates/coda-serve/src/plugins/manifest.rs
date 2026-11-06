//! The Claude plugin manifest subset Coda understands.
//!
//! A plugin directory is recognised by `.claude-plugin/plugin.json`, falling
//! back to a root `plugin.json` (both layouts exist in the wild; the LSP loader
//! has always read the root form). Only `name` is required. Unknown keys are
//! ignored with a debug log so a manifest written for a newer CLI still loads.

use std::path::{Component, Path, PathBuf};

use serde_json::Value;

/// The keys this module maps. Anything else is logged and ignored.
const KNOWN_KEYS: &[&str] = &[
    "name",
    "version",
    "description",
    "author",
    "skills",
    "agents",
    "commands",
    "mcpServers",
    "hooks",
    "lspServers",
];

/// A parsed plugin manifest.
#[derive(Debug, Clone, Default)]
pub struct PluginManifest {
    pub name: String,
    pub version: Option<String>,
    pub description: Option<String>,
    pub author: Option<String>,
    /// Extra skill directories, additive to `./skills/`.
    pub skills: Vec<String>,
    /// Extra agent directories, additive to `./agents/`.
    pub agents: Vec<String>,
    /// Extra command directories, additive to `./commands/`.
    pub commands: Vec<String>,
    /// Inline object or relative path to a JSON file.
    pub mcp_servers: Option<Value>,
    /// Inline object or relative path to a JSON file.
    pub hooks: Option<Value>,
    /// Handed to the LSP loader as-is.
    pub lsp_servers: Option<Value>,
}

/// The manifest file for `root`, preferring the `.claude-plugin/` layout.
pub fn manifest_path(root: &Path) -> Option<PathBuf> {
    let nested = root.join(".claude-plugin").join("plugin.json");
    if nested.is_file() {
        return Some(nested);
    }
    let flat = root.join("plugin.json");
    flat.is_file().then_some(flat)
}

/// Reads and parses the manifest of `root`.
///
/// `None` when `root` is not a plugin directory at all; `Some(Err)` when it is
/// one but the manifest is unusable. The raw bytes are returned with the
/// manifest because approvals are keyed by them.
pub fn load(root: &Path) -> Option<Result<(PluginManifest, Vec<u8>), String>> {
    let path = manifest_path(root)?;
    Some(load_file(&path))
}

fn load_file(path: &Path) -> Result<(PluginManifest, Vec<u8>), String> {
    let bytes = std::fs::read(path).map_err(|e| format!("cannot read {}: {e}", path.display()))?;
    let text = String::from_utf8_lossy(&bytes);
    let text = text.trim_start_matches('\u{feff}');
    let value: Value =
        serde_json::from_str(text).map_err(|e| format!("invalid plugin.json: {e}"))?;
    let manifest = parse(&value)?;
    Ok((manifest, bytes))
}

/// Maps a manifest document onto [`PluginManifest`].
pub fn parse(value: &Value) -> Result<PluginManifest, String> {
    let obj = value
        .as_object()
        .ok_or_else(|| "invalid plugin.json: the root must be an object".to_owned())?;
    let name = obj
        .get("name")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|n| !n.is_empty())
        .ok_or_else(|| "invalid plugin.json: `name` is required".to_owned())?;
    if !is_valid_name(name) {
        return Err(format!(
            "invalid plugin name '{name}': use letters, digits, '-', '_' or '.'"
        ));
    }
    for key in obj.keys() {
        if !KNOWN_KEYS.contains(&key.as_str()) {
            tracing::debug!(
                plugin = name,
                key = key.as_str(),
                "ignoring unknown plugin.json key"
            );
        }
    }
    let author = match obj.get("author") {
        Some(Value::String(s)) => Some(s.clone()),
        Some(Value::Object(a)) => a.get("name").and_then(Value::as_str).map(str::to_owned),
        _ => None,
    };
    Ok(PluginManifest {
        name: name.to_owned(),
        version: string_field(obj.get("version")),
        description: string_field(obj.get("description")),
        author,
        skills: path_list(obj.get("skills")),
        agents: path_list(obj.get("agents")),
        commands: path_list(obj.get("commands")),
        mcp_servers: obj.get("mcpServers").filter(|v| !v.is_null()).cloned(),
        hooks: obj.get("hooks").filter(|v| !v.is_null()).cloned(),
        lsp_servers: obj.get("lspServers").filter(|v| !v.is_null()).cloned(),
    })
}

/// A plugin name becomes part of skill, agent and MCP tool names, and a
/// directory name for git installs, so it is restricted to a safe alphabet.
pub fn is_valid_name(name: &str) -> bool {
    !name.is_empty()
        && name != "."
        && name != ".."
        && name
            .chars()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_' | '.'))
}

fn string_field(v: Option<&Value>) -> Option<String> {
    v.and_then(Value::as_str)
        .map(str::to_owned)
        .filter(|s| !s.trim().is_empty())
}

fn path_list(v: Option<&Value>) -> Vec<String> {
    match v {
        Some(Value::String(s)) => vec![s.clone()],
        Some(Value::Array(items)) => items
            .iter()
            .filter_map(Value::as_str)
            .map(str::to_owned)
            .collect(),
        _ => Vec::new(),
    }
}

/// Resolves a manifest-relative path, refusing anything that leaves the root.
///
/// The check is lexical first (absolute paths and `..` escapes are refused
/// even when the target does not exist) and then, for existing targets, on
/// the canonical path, so a symlink pointing outside is refused too.
pub fn resolve_contained(root: &Path, rel: &str) -> Result<PathBuf, String> {
    let rel_path = Path::new(rel);
    if rel_path.is_absolute() || rel_path.has_root() {
        return Err(format!("path must be relative to the plugin root: {rel}"));
    }
    let mut depth: i32 = 0;
    for comp in rel_path.components() {
        match comp {
            Component::ParentDir => depth -= 1,
            Component::Normal(_) => depth += 1,
            Component::CurDir => {}
            _ => return Err(format!("path must be relative to the plugin root: {rel}")),
        }
        if depth < 0 {
            return Err(format!("path escapes plugin root: {rel}"));
        }
    }
    let joined = root.join(rel_path);
    if joined.exists() {
        let canonical_root = super::canonical(root);
        let canonical = super::canonical(&joined);
        if !canonical.starts_with(&canonical_root) {
            return Err(format!("path escapes plugin root: {rel}"));
        }
        return Ok(canonical);
    }
    Ok(joined)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn write(path: &Path, text: &str) {
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(path, text).unwrap();
    }

    #[test]
    fn the_claude_layout_is_preferred_over_a_root_manifest() {
        let dir = tempfile::tempdir().unwrap();
        write(
            &dir.path().join(".claude-plugin/plugin.json"),
            r#"{"name":"nested"}"#,
        );
        write(&dir.path().join("plugin.json"), r#"{"name":"flat"}"#);
        let (m, _) = load(dir.path()).unwrap().unwrap();
        assert_eq!(m.name, "nested");
    }

    #[test]
    fn a_root_manifest_is_accepted() {
        let dir = tempfile::tempdir().unwrap();
        write(
            &dir.path().join("plugin.json"),
            r#"{"name":"flat","version":"1.2.3"}"#,
        );
        let (m, bytes) = load(dir.path()).unwrap().unwrap();
        assert_eq!(m.name, "flat");
        assert_eq!(m.version.as_deref(), Some("1.2.3"));
        assert!(!bytes.is_empty());
    }

    #[test]
    fn a_directory_without_a_manifest_is_not_a_plugin() {
        let dir = tempfile::tempdir().unwrap();
        assert!(load(dir.path()).is_none());
    }

    #[test]
    fn malformed_json_and_a_missing_name_are_errors() {
        let dir = tempfile::tempdir().unwrap();
        write(&dir.path().join("plugin.json"), "{ not json");
        let err = load(dir.path()).unwrap().unwrap_err();
        assert!(err.contains("invalid plugin.json"), "{err}");

        assert!(parse(&json!({"version":"1"}))
            .unwrap_err()
            .contains("`name` is required"));
        assert!(parse(&json!({"name":"../evil"})).is_err());
    }

    #[test]
    fn author_paths_and_unknown_keys_are_tolerated() {
        let m = parse(&json!({
            "name": "p",
            "author": {"name": "someone"},
            "skills": "./extra",
            "agents": ["./a", "./b"],
            "keywords": ["x"],
            "mcpServers": "./servers.json"
        }))
        .unwrap();
        assert_eq!(m.author.as_deref(), Some("someone"));
        assert_eq!(m.skills, vec!["./extra"]);
        assert_eq!(m.agents, vec!["./a", "./b"]);
        assert_eq!(m.mcp_servers, Some(json!("./servers.json")));
        assert_eq!(
            parse(&json!({"name":"p","author":"me"}))
                .unwrap()
                .author
                .as_deref(),
            Some("me")
        );
    }

    #[test]
    fn paths_must_stay_inside_the_root() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(dir.path().join("skills")).unwrap();
        assert!(resolve_contained(dir.path(), "./skills").is_ok());
        assert!(resolve_contained(dir.path(), "a/../skills").is_ok());
        assert!(resolve_contained(dir.path(), "../x")
            .unwrap_err()
            .contains("escapes"));
        assert!(resolve_contained(dir.path(), "a/../../x")
            .unwrap_err()
            .contains("escapes"));
        let absolute = dir.path().join("skills").to_string_lossy().into_owned();
        assert!(resolve_contained(dir.path(), &absolute).is_err());
    }
}
