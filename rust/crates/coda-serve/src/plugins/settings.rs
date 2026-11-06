//! The `plugins` settings key and the legacy `plugin-state.json` disable list.
//!
//! Only the **user** settings file is read: a project settings file must not
//! be able to point Coda at arbitrary directories on the machine.
//!
//! ```json
//! "plugins": {
//!   "enabled": { "deep-review": true },
//!   "paths": [ "D:/src/my-marketplace/plugins/owasp-pr-review" ],
//!   "marketplaces": {
//!     "local-dev": { "source": "directory", "path": "D:/src/my-marketplace" },
//!     "team": { "source": "git", "url": "https://github.com/org/plugins.git" }
//!   }
//! }
//! ```

use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};

use serde_json::Value;

use super::PluginSource;

/// How a marketplace is reached.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MarketplaceKind {
    /// A local folder, read in place.
    Directory,
    /// A git repository cloned under `~/.coda/marketplaces/<id>`.
    Git { url: String },
}

/// One `plugins.marketplaces` entry.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MarketplaceRef {
    pub id: String,
    pub kind: MarketplaceKind,
    /// The directory scanned for plugins.
    pub path: PathBuf,
}

/// The parsed `plugins` settings.
#[derive(Debug, Clone, Default)]
pub struct PluginSettings {
    pub enabled: BTreeMap<String, bool>,
    pub paths: Vec<PathBuf>,
    pub marketplaces: Vec<MarketplaceRef>,
    /// `disabledPlugins` from `~/.coda/plugin-state.json` (written by the TUI).
    pub legacy_disabled: BTreeSet<String>,
    /// `explicitlyEnabled` from the same file.
    pub legacy_enabled: BTreeSet<String>,
}

impl PluginSettings {
    /// Reads `settings["plugins"]`. Shape problems are returned as messages
    /// rather than failing: one bad entry must not hide every plugin.
    pub fn from_settings(settings: &Value, coda_dir: &Path) -> (Self, Vec<String>) {
        let mut out = Self::default();
        let mut errors = Vec::new();
        let Some(plugins) = settings.get("plugins") else {
            return (out, errors);
        };
        let Some(plugins) = plugins.as_object() else {
            errors.push("settings `plugins` must be an object".to_owned());
            return (out, errors);
        };

        match plugins.get("enabled") {
            None | Some(Value::Null) => {}
            Some(Value::Object(map)) => {
                for (name, value) in map {
                    match value.as_bool() {
                        Some(b) => {
                            out.enabled.insert(name.clone(), b);
                        }
                        None => errors.push(format!(
                            "settings `plugins.enabled.{name}` must be true or false"
                        )),
                    }
                }
            }
            Some(_) => errors.push("settings `plugins.enabled` must be an object".to_owned()),
        }

        match plugins.get("paths") {
            None | Some(Value::Null) => {}
            Some(Value::Array(items)) => {
                for item in items {
                    match item.as_str().map(str::trim).filter(|s| !s.is_empty()) {
                        Some(p) => out.paths.push(expand_home(p)),
                        None => errors
                            .push("settings `plugins.paths` entries must be strings".to_owned()),
                    }
                }
            }
            Some(_) => errors.push("settings `plugins.paths` must be an array".to_owned()),
        }

        match plugins.get("marketplaces") {
            None | Some(Value::Null) => {}
            Some(Value::Object(map)) => {
                for (id, def) in map {
                    match parse_marketplace(id, def, coda_dir) {
                        Ok(m) => out.marketplaces.push(m),
                        Err(e) => errors.push(e),
                    }
                }
            }
            Some(_) => errors.push("settings `plugins.marketplaces` must be an object".to_owned()),
        }

        (out, errors)
    }

    /// Reads the legacy disable list. Missing or malformed files are ignored.
    pub fn with_legacy_state(mut self, coda_dir: &Path) -> Self {
        let path = coda_dir.join("plugin-state.json");
        if let Ok(text) = std::fs::read_to_string(path) {
            if let Ok(doc) = serde_json::from_str::<Value>(text.trim_start_matches('\u{feff}')) {
                let names = |key: &str| -> Vec<String> {
                    doc.get(key)
                        .and_then(Value::as_array)
                        .map(|l| {
                            l.iter()
                                .filter_map(Value::as_str)
                                .map(str::to_owned)
                                .collect()
                        })
                        .unwrap_or_default()
                };
                self.legacy_disabled.extend(names("disabledPlugins"));
                self.legacy_enabled.extend(names("explicitlyEnabled"));
            }
        }
        self
    }

    /// Whether a plugin from `source` is enabled.
    ///
    /// An explicit `plugins.enabled` entry always wins. Otherwise a plugin
    /// offered by a marketplace stays off until enabled (a marketplace is a
    /// catalogue, not an install), and every other source is on unless the
    /// legacy state disabled it.
    pub fn is_enabled(&self, name: &str, source: &PluginSource) -> bool {
        if let Some(explicit) = self.enabled.get(name) {
            return *explicit;
        }
        if self.legacy_disabled.contains(name) {
            return false;
        }
        if self.legacy_enabled.contains(name) {
            return true;
        }
        !matches!(source, PluginSource::Marketplace(_))
    }
}

fn parse_marketplace(id: &str, def: &Value, coda_dir: &Path) -> Result<MarketplaceRef, String> {
    if !super::manifest::is_valid_name(id) {
        return Err(format!("marketplace id '{id}' is not valid"));
    }
    let obj = def
        .as_object()
        .ok_or_else(|| format!("settings `plugins.marketplaces.{id}` must be an object"))?;
    let source = obj
        .get("source")
        .and_then(Value::as_str)
        .unwrap_or("directory");
    let path = obj.get("path").and_then(Value::as_str).map(expand_home);
    match source {
        "directory" => {
            let path = path.ok_or_else(|| format!("marketplace '{id}' needs a `path`"))?;
            Ok(MarketplaceRef {
                id: id.to_owned(),
                kind: MarketplaceKind::Directory,
                path,
            })
        }
        "git" => {
            let url = obj
                .get("url")
                .and_then(Value::as_str)
                .filter(|u| !u.trim().is_empty())
                .ok_or_else(|| format!("marketplace '{id}' needs a `url`"))?;
            Ok(MarketplaceRef {
                id: id.to_owned(),
                kind: MarketplaceKind::Git {
                    url: url.to_owned(),
                },
                path: path.unwrap_or_else(|| git_marketplace_dir(coda_dir, id)),
            })
        }
        other => Err(format!(
            "marketplace '{id}' has an unsupported source '{other}'"
        )),
    }
}

/// Where a git marketplace is cloned.
pub fn git_marketplace_dir(coda_dir: &Path, id: &str) -> PathBuf {
    coda_dir.join("marketplaces").join(id)
}

/// Expands a leading `~` to the profile root.
fn expand_home(p: &str) -> PathBuf {
    if let Some(rest) = p.strip_prefix("~/").or_else(|| p.strip_prefix("~\\")) {
        return coda_auth::coda_home().join(rest);
    }
    PathBuf::from(p)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn the_documented_shape_is_parsed() {
        let coda = Path::new("C:/home/.coda");
        let (s, errors) = PluginSettings::from_settings(
            &json!({"plugins": {
                "enabled": {"a": true, "b": false},
                "paths": ["D:/src/p"],
                "marketplaces": {
                    "local": {"source": "directory", "path": "D:/m"},
                    "team": {"source": "git", "url": "https://x/y.git"}
                }
            }}),
            coda,
        );
        assert!(errors.is_empty(), "{errors:?}");
        assert_eq!(s.enabled.get("a"), Some(&true));
        assert_eq!(s.paths, vec![PathBuf::from("D:/src/p")]);
        assert_eq!(s.marketplaces.len(), 2);
        let team = s.marketplaces.iter().find(|m| m.id == "team").unwrap();
        assert_eq!(team.path, coda.join("marketplaces").join("team"));
    }

    #[test]
    fn wrong_shapes_are_reported_not_fatal() {
        let (s, errors) = PluginSettings::from_settings(
            &json!({"plugins": {
                "enabled": {"a": "yes"},
                "paths": [3, "D:/ok"],
                "marketplaces": {"m": {"source": "ftp"}, "n": {"source": "directory"}}
            }}),
            Path::new("."),
        );
        assert_eq!(s.paths, vec![PathBuf::from("D:/ok")]);
        assert_eq!(errors.len(), 4, "{errors:?}");
        let (_, errors) = PluginSettings::from_settings(&json!({"plugins": []}), Path::new("."));
        assert_eq!(errors.len(), 1);
    }

    #[test]
    fn enablement_rules() {
        let mut s = PluginSettings::default();
        let market = PluginSource::Marketplace("m".into());
        assert!(s.is_enabled("p", &PluginSource::User));
        assert!(s.is_enabled("p", &PluginSource::Path));
        assert!(s.is_enabled("p", &PluginSource::Project));
        assert!(
            !s.is_enabled("p", &market),
            "marketplace plugins are opt-in"
        );

        s.legacy_disabled.insert("p".into());
        assert!(!s.is_enabled("p", &PluginSource::User));

        s.enabled.insert("p".into(), true);
        assert!(
            s.is_enabled("p", &PluginSource::User),
            "explicit settings win"
        );
        assert!(s.is_enabled("p", &market));
        s.enabled.insert("p".into(), false);
        assert!(!s.is_enabled("p", &PluginSource::Path));
    }

    #[test]
    fn the_legacy_disable_list_is_read() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(
            dir.path().join("plugin-state.json"),
            r#"{"disabledPlugins":["old"],"explicitlyEnabled":["m"]}"#,
        )
        .unwrap();
        let s = PluginSettings::default().with_legacy_state(dir.path());
        assert!(!s.is_enabled("old", &PluginSource::User));
        assert!(
            s.is_enabled("m", &PluginSource::Marketplace("x".into())),
            "the TUI's /plugin enable counts"
        );
    }
}
