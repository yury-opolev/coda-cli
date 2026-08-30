//! `PluginAgentLoader` — loads subagent definitions from plugin `agents/` directories.
//!
//! Mirrors C# `Coda.Tui.Plugins.PluginAgentLoader` and the supporting
//! `PluginResourceLoader` utilities.
//!
//! # Security
//! Plugin content is untrusted.  The loader enforces two guards:
//! - **Forbidden keys**: `hooks`, `mcpServers`, `permissions`, `permissionMode` are
//!   logged as errors but the agent still loads (the keys land in unknown fields and
//!   are never acted on; the real restriction is that only recognised fields are
//!   mapped).
//! - **Project-scoped plugins**: the `model` key is silently ignored with a warning
//!   because the project directory is attacker-controlled after a hostile clone, and
//!   model choice is a cost lever.

use std::collections::HashMap;
use std::path::Path;

use super::SubagentDefinition;

/// Loads plugin-contributed subagent definitions from `agents/` directories.
pub struct PluginAgentLoader;

impl PluginAgentLoader {
    /// Loads agent definitions from `agents_dir`, one per `.md` file.
    ///
    /// Malformed files are skipped (logged via `warn`); valid ones are returned.
    ///
    /// # Security
    /// When `is_project_plugin` is `true`, `model:` keys are silently ignored with a
    /// warning — the project directory is attacker-controlled, and model selection is
    /// a cost lever.
    pub fn load_from_directory(
        agents_dir: &str,
        plugin_name: &str,
        is_project_plugin: bool,
    ) -> Vec<SubagentDefinition> {
        let path = Path::new(agents_dir);
        if !path.is_dir() {
            return Vec::new();
        }

        let mut results = Vec::new();

        let entries = match std::fs::read_dir(path) {
            Ok(e) => e,
            Err(_) => return Vec::new(),
        };

        for entry in entries.flatten() {
            let file_path = entry.path();
            if file_path.extension().and_then(|e| e.to_str()) != Some("md") {
                continue;
            }
            let content = match std::fs::read_to_string(&file_path) {
                Ok(c) => c,
                Err(e) => {
                    tracing::error!(
                        plugin = plugin_name,
                        file = %file_path.display(),
                        "failed to read agent file: {e}"
                    );
                    continue;
                }
            };
            match parse_agent_file(&content, plugin_name, &file_path.to_string_lossy(), is_project_plugin) {
                Some(def) => results.push(def),
                None => {} // already logged inside
            }
        }

        results
    }
}

/// Parse a single `.md` agent file. Returns `None` and logs on any fatal parse issue.
fn parse_agent_file(
    content: &str,
    plugin_name: &str,
    file_name: &str,
    is_project_plugin: bool,
) -> Option<SubagentDefinition> {
    let fm = parse_frontmatter(content);

    if !fm.has_frontmatter {
        tracing::warn!(
            plugin = plugin_name,
            file = file_name,
            "agent file has no frontmatter block — skipped"
        );
        return None;
    }

    // SECURITY: log but don't reject on forbidden keys (they're ignored by the mapper anyway).
    static FORBIDDEN_STRIPPED: &[&str] = &["hooks", "mcpservers", "permissions", "permissionmode"];
    for key in fm.unknown_keys.keys() {
        let stripped = key
            .chars()
            .filter(|c| *c != '-' && *c != ' ')
            .collect::<String>()
            .to_lowercase();
        if FORBIDDEN_STRIPPED.contains(&stripped.as_str()) {
            tracing::error!(
                plugin = plugin_name,
                file = file_name,
                key = key.as_str(),
                "key '{}' is forbidden in plugin-contributed agent definitions and will be ignored; \
                 declare hooks, mcpServers, and permission settings at the plugin level instead",
                key
            );
        }
    }

    // `type` is not a standard skill key — it lands in unknown_keys.
    let agent_type = fm.unknown_keys.get("type").map(String::as_str).unwrap_or("").trim().to_owned();
    if agent_type.is_empty() {
        tracing::warn!(
            plugin = plugin_name,
            file = file_name,
            "agent file has no 'type' field — skipped"
        );
        return None;
    }

    let description = fm.description.unwrap_or_default();

    // read-only-tools is not a standard skill key → lands in unknown_keys.
    let read_only_tools_only = fm
        .unknown_keys
        .get("read-only-tools")
        .map(|v| v.eq_ignore_ascii_case("true"))
        .unwrap_or(false);

    // model: accepted for user-scoped plugins, ignored with a warning for project-scoped.
    let model = if let Some(ref m) = fm.model {
        if !m.is_empty() {
            if is_project_plugin {
                tracing::warn!(
                    plugin = plugin_name,
                    file = file_name,
                    "model key is ignored in project-scoped plugin agent definitions; \
                     declare model overrides in your user settings file instead"
                );
                None
            } else {
                Some(m.trim().to_owned())
            }
        } else {
            None
        }
    } else {
        None
    };

    Some(SubagentDefinition {
        agent_type,
        description,
        system_prompt_body: fm.body,
        read_only_tools_only,
        default_model: model,
    })
}

// ── Minimal YAML-subset frontmatter parser ────────────────────────────────────
//
// Only the fields needed for agent definitions are parsed. Mirrors the relevant
// portions of C# `SkillFrontmatterParser` (key normalisation, body extraction,
// unknown-field retention, forbidden-key detection).

struct ParsedFrontmatter {
    has_frontmatter: bool,
    description: Option<String>,
    model: Option<String>,
    body: String,
    /// All keys that are NOT `description` or `model` (i.e., not "standard skill" keys).
    unknown_keys: HashMap<String, String>,
}

/// Parse YAML-subset frontmatter delimited by `---` lines.
/// Never panics; malformed input degrades to sensible defaults.
fn parse_frontmatter(content: &str) -> ParsedFrontmatter {
    let content = content.replace("\r\n", "\n").replace('\r', "\n");
    let lines: Vec<&str> = content.split('\n').collect();

    // Find opening ---
    let mut start = None;
    for (i, line) in lines.iter().enumerate() {
        let t = line.trim();
        if !t.is_empty() {
            if t == "---" {
                start = Some(i);
            }
            break;
        }
    }
    let start = match start {
        Some(s) => s,
        None => {
            return ParsedFrontmatter {
                has_frontmatter: false,
                description: None,
                model: None,
                body: content.trim().to_owned(),
                unknown_keys: HashMap::new(),
            }
        }
    };

    // Find closing ---
    let end = lines[start + 1..].iter().position(|l| l.trim() == "---").map(|i| start + 1 + i);
    let end = match end {
        Some(e) => e,
        None => {
            // Unterminated → treat entire file as body
            return ParsedFrontmatter {
                has_frontmatter: false,
                description: None,
                model: None,
                body: content.trim().to_owned(),
                unknown_keys: HashMap::new(),
            };
        }
    };

    let fm_lines = &lines[start + 1..end];
    let body = lines[end + 1..].join("\n").trim().to_owned();

    // Parse key: value pairs.
    let mut description: Option<String> = None;
    let mut model: Option<String> = None;
    let mut unknown: HashMap<String, String> = HashMap::new();

    for raw_line in fm_lines {
        let line = strip_inline_comment(raw_line);
        if line.trim().is_empty() {
            continue;
        }
        let colon = match line.find(':') {
            Some(i) if i > 0 => i,
            _ => continue,
        };
        let key = normalize_key(&line[..colon]);
        if key.is_empty() {
            continue;
        }
        let value = strip_quotes(line[colon + 1..].trim());

        match key.as_str() {
            "description" => description = Some(value.to_owned()),
            "model" => {
                let v = if value.eq_ignore_ascii_case("inherit") {
                    String::new()
                } else {
                    value.to_owned()
                };
                model = Some(v);
            }
            _ => {
                unknown.insert(key, value.to_owned());
            }
        }
    }

    ParsedFrontmatter {
        has_frontmatter: true,
        description,
        model,
        body,
        unknown_keys: unknown,
    }
}

/// Normalise a frontmatter key: lowercase, underscores → hyphens, trim.
fn normalize_key(raw: &str) -> String {
    raw.trim().to_lowercase().replace('_', "-")
}

/// Strip a leading/trailing matched pair of single or double quotes.
fn strip_quotes(value: &str) -> &str {
    let v = value.trim();
    if v.len() >= 2 {
        let (first, last) = (v.as_bytes()[0], v.as_bytes()[v.len() - 1]);
        if (first == b'"' && last == b'"') || (first == b'\'' && last == b'\'') {
            return &v[1..v.len() - 1];
        }
    }
    v
}

/// Strip a trailing `# comment` from a YAML value line.
fn strip_inline_comment(text: &str) -> &str {
    let bytes = text.as_bytes();
    let mut in_quote: Option<u8> = None;
    for (i, &b) in bytes.iter().enumerate() {
        if let Some(q) = in_quote {
            if b == q {
                in_quote = None;
            }
        } else if b == b'"' || b == b'\'' {
            in_quote = Some(b);
        } else if b == b'#' && (i == 0 || bytes[i - 1].is_ascii_whitespace()) {
            return text[..i].trim_end();
        }
    }
    text
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// Create a unique temp directory and return a guard that removes it on drop.
    struct TempDir(std::path::PathBuf);

    impl TempDir {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "coda-agent-plugin-loader-tests-{}",
                uuid::Uuid::new_v4()
            ));
            std::fs::create_dir_all(&path).unwrap();
            Self(path)
        }
        fn path(&self) -> &std::path::Path { &self.0 }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    // helper: write .md files, run the loader, collect results
    fn load_from(files: &[(&str, &str)]) -> (TempDir, Vec<SubagentDefinition>) {
        let dir = TempDir::new();
        for (name, content) in files {
            std::fs::write(dir.path().join(name), content).unwrap();
        }
        let defs = PluginAgentLoader::load_from_directory(
            dir.path().to_str().unwrap(),
            "test-plugin",
            false,
        );
        (dir, defs)
    }

    #[test]
    fn loads_agent_from_md_file() {
        let (_dir, agents) = load_from(&[(
            "reviewer.md",
            "---\ntype: my-reviewer\ndescription: Specialist\n---\nYou review code thoroughly.",
        )]);
        assert_eq!(agents.len(), 1);
        assert_eq!(agents[0].agent_type, "my-reviewer");
        assert_eq!(agents[0].description, "Specialist");
        assert_eq!(agents[0].system_prompt_body, "You review code thoroughly.");
    }

    #[test]
    fn reads_read_only_tools_flag() {
        let (_dir, agents) = load_from(&[(
            "readonly.md",
            "---\ntype: readonly-agent\nread-only-tools: true\n---\nRead only.",
        )]);
        assert_eq!(agents.len(), 1);
        assert!(agents[0].read_only_tools_only);
    }

    #[test]
    fn skips_file_without_frontmatter() {
        let (_dir, agents) = load_from(&[("no-fm.md", "Just plain text, no frontmatter.")]);
        assert!(agents.is_empty());
    }

    #[test]
    fn skips_file_without_type_field() {
        let (_dir, agents) = load_from(&[(
            "no-type.md",
            "---\ndescription: No type declared\n---\nBody.",
        )]);
        assert!(agents.is_empty());
    }

    #[test]
    fn returns_empty_when_directory_missing() {
        let agents = PluginAgentLoader::load_from_directory(
            "/no/such/directory/9999",
            "test-plugin",
            false,
        );
        assert!(agents.is_empty());
    }

    #[test]
    fn reads_model_key_from_user_scoped_plugin() {
        let (_dir, agents) = load_from(&[(
            "model-agent.md",
            "---\ntype: model-agent\nmodel: fast-cheap-model\n---\nBody.",
        )]);
        assert_eq!(agents.len(), 1);
        assert_eq!(agents[0].default_model.as_deref(), Some("fast-cheap-model"));
    }

    #[test]
    fn ignores_model_key_from_project_scoped_plugin() {
        let dir = TempDir::new();
        std::fs::write(
            dir.path().join("proj-model.md"),
            "---\ntype: proj-model-agent\nmodel: expensive-model\n---\nBody.",
        ).unwrap();
        let agents = PluginAgentLoader::load_from_directory(
            dir.path().to_str().unwrap(),
            "test-plugin",
            true, // is_project_plugin
        );
        assert_eq!(agents.len(), 1);
        assert!(agents[0].default_model.is_none(), "model must be None for project-scoped plugin");
    }

    #[test]
    fn no_model_key_leaves_model_null() {
        let (_dir, agents) = load_from(&[(
            "no-model.md",
            "---\ntype: no-model-agent\n---\nBody.",
        )]);
        assert_eq!(agents.len(), 1);
        assert!(agents[0].default_model.is_none());
    }

    // Forbidden-key tests: agent still loads, key is ignored.
    #[test]
    fn forbidden_hooks_key_still_loads_agent() {
        let (_dir, agents) = load_from(&[(
            "bad-hooks.md",
            "---\ntype: bad-agent\nhooks: ./hook.sh\n---\nBody text.",
        )]);
        assert_eq!(agents.len(), 1, "agent must still load despite forbidden key");
        assert_eq!(agents[0].agent_type, "bad-agent");
    }

    #[test]
    fn forbidden_mcp_servers_key_still_loads_agent() {
        let (_dir, agents) = load_from(&[(
            "bad-mcp.md",
            "---\ntype: mcp-agent\nmcpServers: ./servers.json\n---\nBody.",
        )]);
        assert_eq!(agents.len(), 1);
    }

    #[test]
    fn forbidden_permissions_key_still_loads_agent() {
        let (_dir, agents) = load_from(&[(
            "bad-perms.md",
            "---\ntype: perms-agent\npermissions: allow-all\n---\nBody.",
        )]);
        assert_eq!(agents.len(), 1);
    }

    #[test]
    fn forbidden_permission_mode_key_still_loads_agent() {
        let (_dir, agents) = load_from(&[(
            "bad-mode.md",
            "---\ntype: mode-agent\npermissionMode: allowAll\n---\nBody.",
        )]);
        assert_eq!(agents.len(), 1);
    }

    // Frontmatter parser unit tests.
    #[test]
    fn parses_type_and_body() {
        let fm = parse_frontmatter("---\ntype: my-type\n---\nbody text");
        assert!(fm.has_frontmatter);
        assert_eq!(fm.unknown_keys.get("type").map(String::as_str), Some("my-type"));
        assert_eq!(fm.body, "body text");
    }

    #[test]
    fn parses_description_as_known_key() {
        let fm = parse_frontmatter("---\ndescription: My desc\n---\nbody");
        assert_eq!(fm.description.as_deref(), Some("My desc"));
        assert!(!fm.unknown_keys.contains_key("description"));
    }

    #[test]
    fn normalizes_underscore_key() {
        let fm = parse_frontmatter("---\nread_only_tools: true\n---\nbody");
        assert_eq!(fm.unknown_keys.get("read-only-tools").map(String::as_str), Some("true"));
    }
}
