//! `skill` — load a skill's instructions into the conversation.
//!
//! The catalogue is built by the host (which knows the workspace, the profile
//! and the installed plugins); this tool only resolves a name and reads the
//! skill file **at call time**, so an edit to a skill applies on its next use
//! without restarting the engine.
//!
//! The output starts with the skill's absolute directory (and, for a plugin
//! skill, its plugin root and agent types). Skills are read in place, never
//! copied, so relative references such as `../review/SKILL.md` or
//! `scripts/helper.ps1` resolve against a real directory.

use std::path::{Path, PathBuf};

use async_trait::async_trait;
use serde_json::Value;
use tokio_util::sync::CancellationToken;

use crate::tool::{Tool, ToolContext, ToolOutcome, ToolResult};

/// Descriptions longer than this are cut in the tool description, so a large
/// catalogue cannot crowd out the conversation.
const MAX_LISTED_DESCRIPTION: usize = 250;

/// One skill the model may load.
#[derive(Debug, Clone, PartialEq)]
pub struct SkillEntry {
    /// Unique name: `<plugin>:<skill>` for plugin skills, the plain name otherwise.
    pub name: String,
    /// The name without a plugin prefix.
    pub short_name: String,
    pub description: String,
    /// The `SKILL.md` (or command `.md`) file.
    pub file: PathBuf,
    pub plugin: Option<String>,
    pub plugin_root: Option<PathBuf>,
    /// `false` when the skill opted out with `disable-model-invocation`.
    pub model_invocable: bool,
    /// Precedence: higher wins a short-name clash (project > plugin > user > …).
    pub rank: u8,
    /// Agent types the skill's plugin contributes, for the output header.
    pub plugin_agents: Vec<String>,
}

impl SkillEntry {
    /// The directory relative references resolve against.
    pub fn dir(&self) -> &Path {
        self.file.parent().unwrap_or(Path::new("."))
    }
}

/// Resolves `name` against `entries`.
///
/// Matches the full name or the short name (case-insensitively); among the
/// matches the highest `rank` wins. Several plugin skills sharing the winning
/// rank is an error naming every candidate, never a silent pick.
pub fn resolve<'a>(entries: &'a [SkillEntry], name: &str) -> Result<&'a SkillEntry, String> {
    let name = name.trim().trim_start_matches('/');
    let matches: Vec<&SkillEntry> = entries
        .iter()
        .filter(|e| e.name.eq_ignore_ascii_case(name) || e.short_name.eq_ignore_ascii_case(name))
        .collect();
    let Some(top) = matches.iter().map(|e| e.rank).max() else {
        let mut names: Vec<&str> = entries.iter().map(|e| e.name.as_str()).collect();
        names.sort_unstable();
        return Err(format!(
            "Unknown skill '{name}'. Available skills: {}",
            if names.is_empty() {
                "(none)".to_owned()
            } else {
                names.join(", ")
            }
        ));
    };
    let winners: Vec<&SkillEntry> = matches.into_iter().filter(|e| e.rank == top).collect();
    if let Some(exact) = winners.iter().find(|e| e.name.eq_ignore_ascii_case(name)) {
        return Ok(exact);
    }
    match winners.as_slice() {
        [one] => Ok(one),
        many => {
            let names: Vec<&str> = many.iter().map(|e| e.name.as_str()).collect();
            Err(format!(
                "Skill '{name}' is ambiguous; use one of: {}",
                names.join(", ")
            ))
        }
    }
}

pub struct SkillTool {
    entries: Vec<SkillEntry>,
    description: String,
}

impl SkillTool {
    pub fn new(entries: Vec<SkillEntry>) -> Self {
        let mut description = String::from(
            "Load a skill: packaged instructions for a specific kind of task. Call this when the \
             user asks for a skill by name (e.g. `/review` or `plugin:review`) or when a task \
             clearly matches a skill's description, then follow the returned instructions. \
             Relative paths in a skill resolve against its base directory.\n\nAvailable skills:",
        );
        let mut listed: Vec<&SkillEntry> = entries.iter().filter(|e| e.model_invocable).collect();
        listed.sort_by(|a, b| a.name.cmp(&b.name));
        for e in listed {
            description.push_str(&format!("\n- {}: {}", e.name, truncate(&e.description)));
        }
        Self {
            entries,
            description,
        }
    }

    pub fn entries(&self) -> &[SkillEntry] {
        &self.entries
    }

    /// The text returned for `entry` invoked with `arguments`.
    pub fn render(entry: &SkillEntry, arguments: &[String]) -> Result<String, String> {
        let text = std::fs::read_to_string(&entry.file).map_err(|e| {
            format!(
                "Cannot read skill '{}' ({}): {e}",
                entry.name,
                entry.file.display()
            )
        })?;
        let mut body = strip_front_matter(&text).to_owned();
        if !arguments.is_empty() {
            let args: Vec<&str> = arguments.iter().map(String::as_str).collect();
            body = bind_skill_args(&body, &args);
        }
        let dir = entry.dir().to_string_lossy().into_owned();
        body = body.replace("${CLAUDE_SKILL_DIR}", &dir);
        let mut out = format!("Base directory for this skill: {dir}\n");
        if let Some(root) = &entry.plugin_root {
            let root = root.to_string_lossy();
            body = body
                .replace("${CLAUDE_PLUGIN_ROOT}", &root)
                .replace("${CODA_PLUGIN_ROOT}", &root);
            out.push_str(&format!(
                "Plugin: {} (root: {root}; also available as ${{CLAUDE_PLUGIN_ROOT}} / ${{CODA_PLUGIN_ROOT}})\n",
                entry.plugin.as_deref().unwrap_or("?")
            ));
            if !entry.plugin_agents.is_empty() {
                out.push_str(&format!(
                    "Subagent types from this plugin (use with the `task` tool's subagentType): {}\n",
                    entry.plugin_agents.join(", ")
                ));
            }
        }
        out.push('\n');
        out.push_str(body.trim_start());
        Ok(out)
    }
}

#[async_trait]
impl Tool for SkillTool {
    fn name(&self) -> &str {
        "skill"
    }

    fn description(&self) -> &str {
        &self.description
    }

    fn input_schema_json(&self) -> &str {
        r#"{
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "The skill name, e.g. 'review' or the namespaced 'deep-review:review'."
            },
            "arguments": {
              "type": "string",
              "description": "Optional arguments for the skill, separated by spaces."
            }
          },
          "required": ["name"]
        }"#
    }

    fn is_read_only(&self) -> bool {
        true
    }

    async fn execute(
        &self,
        input: &Value,
        _ctx: &ToolContext,
        _cancel: CancellationToken,
    ) -> ToolOutcome {
        let Some(name) = input
            .get("name")
            .and_then(Value::as_str)
            .filter(|n| !n.trim().is_empty())
        else {
            return ToolResult::error("Missing required 'name'.");
        };
        let entry = match resolve(&self.entries, name) {
            Ok(e) => e,
            Err(e) => return ToolResult::error(e),
        };
        if !entry.model_invocable {
            return ToolResult::error(format!(
                "Skill '{}' has disable-model-invocation set; only the user can run it (/skill {}).",
                entry.name, entry.name
            ));
        }
        let arguments: Vec<String> = match input.get("arguments") {
            Some(Value::String(s)) => s.split_whitespace().map(str::to_owned).collect(),
            Some(Value::Array(items)) => items
                .iter()
                .filter_map(Value::as_str)
                .map(str::to_owned)
                .collect(),
            _ => Vec::new(),
        };
        match Self::render(entry, &arguments) {
            Ok(text) => ToolResult::ok(text),
            Err(e) => ToolResult::error(e),
        }
    }
}

fn truncate(s: &str) -> String {
    let s = s.trim();
    if s.chars().count() <= MAX_LISTED_DESCRIPTION {
        return s.to_owned();
    }
    let cut: String = s.chars().take(MAX_LISTED_DESCRIPTION).collect();
    format!("{cut}…")
}

/// The document without its leading `---` front matter block.
pub fn strip_front_matter(text: &str) -> &str {
    let t = text.trim_start_matches('\u{feff}');
    let mut lines = t.split_inclusive('\n');
    match lines.next() {
        Some(first) if first.trim() == "---" => {}
        _ => return t,
    }
    let mut offset = t.split_inclusive('\n').next().map_or(0, str::len);
    for line in lines {
        offset += line.len();
        if line.trim() == "---" {
            return &t[offset..];
        }
    }
    t
}

/// Substitutes skill argument placeholders in a single pass, preventing
/// re-expansion of substituted values.
///
/// Rules (matching C# `SkillArgumentBinder`):
/// - `$$`         → literal `$`
/// - `$ARGUMENTS` → all positional args joined by a single space (case-sensitive)
/// - `$N` (N ≥ 1) → the N-th positional arg, or empty if out of range
/// - `$identifier` → empty (named args are not bound here)
/// - bare `$`     → kept as-is
pub fn bind_skill_args(body: &str, args: &[&str]) -> String {
    let mut out = String::with_capacity(body.len());
    let mut chars = body.char_indices().peekable();

    while let Some((_, c)) = chars.next() {
        if c != '$' {
            out.push(c);
            continue;
        }

        match chars.peek() {
            Some((_, '$')) => {
                chars.next();
                out.push('$');
            }
            Some((_, d)) if d.is_ascii_digit() => {
                let mut num_str = String::new();
                while let Some(&(_, nd)) = chars.peek() {
                    if nd.is_ascii_digit() {
                        num_str.push(nd);
                        chars.next();
                    } else {
                        break;
                    }
                }
                let n: usize = num_str.parse().unwrap_or(0);
                if n >= 1 && n <= args.len() {
                    out.push_str(args[n - 1]);
                }
            }
            Some((_, d)) if d.is_alphabetic() || *d == '_' => {
                let mut name = String::new();
                while let Some(&(_, nc)) = chars.peek() {
                    if nc.is_alphanumeric() || nc == '_' {
                        name.push(nc);
                        chars.next();
                    } else {
                        break;
                    }
                }
                if name == "ARGUMENTS" {
                    out.push_str(&args.join(" "));
                }
            }
            _ => out.push('$'),
        }
    }

    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn entry(name: &str, plugin: Option<&str>, rank: u8, file: PathBuf) -> SkillEntry {
        SkillEntry {
            name: plugin.map_or(name.to_owned(), |p| format!("{p}:{name}")),
            short_name: name.to_owned(),
            description: format!("{name} desc"),
            file,
            plugin: plugin.map(str::to_owned),
            plugin_root: None,
            model_invocable: true,
            rank,
            plugin_agents: Vec::new(),
        }
    }

    #[test]
    fn resolution_prefers_higher_rank_and_reports_ambiguity() {
        let f = PathBuf::from("x/SKILL.md");
        let user = entry("review", None, 2, f.clone());
        let a = entry("review", Some("a"), 3, f.clone());
        let b = entry("review", Some("b"), 3, f.clone());
        let project = entry("review", None, 4, f.clone());

        let all = vec![user.clone(), a.clone(), b.clone(), project.clone()];
        assert_eq!(resolve(&all, "review").unwrap().rank, 4, "project wins");
        assert_eq!(
            resolve(&all, "b:review").unwrap().name,
            "b:review",
            "qualified is exact"
        );

        let err = resolve(&[user.clone(), a.clone(), b.clone()], "review").unwrap_err();
        assert!(
            err.contains("a:review") && err.contains("b:review"),
            "{err}"
        );

        assert_eq!(
            resolve(&[user.clone(), a.clone()], "review").unwrap().name,
            "a:review",
            "plugin beats user"
        );
        assert!(resolve(&[user], "nope")
            .unwrap_err()
            .contains("Available skills: review"));
    }

    #[tokio::test]
    async fn invoking_a_plugin_skill_returns_its_directory_and_expanded_body() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path().join("deep-review");
        let skill_dir = root.join("skills/extreme-review");
        std::fs::create_dir_all(&skill_dir).unwrap();
        let file = skill_dir.join("SKILL.md");
        std::fs::write(
            &file,
            "---\nname: extreme-review\ndescription: d\n---\nRead ../review/SKILL.md then run \
             ${CLAUDE_PLUGIN_ROOT}/scripts/x.ps1 on $ARGUMENTS. Costs $$5.",
        )
        .unwrap();
        let mut e = entry("extreme-review", Some("deep-review"), 3, file.clone());
        e.plugin_root = Some(root.clone());
        e.plugin_agents = vec!["deep-review:skeptic".into()];
        let tool = SkillTool::new(vec![e]);
        assert!(tool
            .description()
            .contains("- deep-review:extreme-review: extreme-review desc"));

        let ctx = ToolContext::new(".");
        let out = tool
            .execute(
                &json!({"name": "extreme-review", "arguments": "PR-7"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        let text = out.content.clone();
        assert!(!out.is_error, "{text}");
        assert!(
            text.starts_with(&format!(
                "Base directory for this skill: {}",
                skill_dir.display()
            )),
            "{text}"
        );
        assert!(text.contains("deep-review:skeptic"));
        assert!(
            text.contains(&format!("{}/scripts/x.ps1 on PR-7", root.display())),
            "{text}"
        );
        assert!(text.contains("Costs $5."));
        assert!(!text.contains("description: d"), "front matter is stripped");

        // Edits apply on the next call without rebuilding the catalogue.
        std::fs::write(&file, "---\nname: extreme-review\n---\nEdited.").unwrap();
        let out = tool
            .execute(
                &json!({"name": "deep-review:extreme-review"}),
                &ctx,
                CancellationToken::new(),
            )
            .await;
        assert!(out.content.clone().ends_with("Edited."));
    }

    #[tokio::test]
    async fn a_skill_that_opted_out_of_model_invocation_is_refused() {
        let dir = tempfile::tempdir().unwrap();
        let file = dir.path().join("SKILL.md");
        std::fs::write(&file, "body").unwrap();
        let mut e = entry("quiet", None, 2, file);
        e.model_invocable = false;
        let tool = SkillTool::new(vec![e]);
        assert!(!tool.description().contains("quiet"));
        let out = tool
            .execute(
                &json!({"name": "quiet"}),
                &ToolContext::new("."),
                CancellationToken::new(),
            )
            .await;
        assert!(out.is_error);
    }

    #[test]
    fn front_matter_is_stripped_only_when_present() {
        assert_eq!(strip_front_matter("---\na: b\n---\nbody"), "body");
        assert_eq!(strip_front_matter("no fm"), "no fm");
        assert_eq!(strip_front_matter("---\nunterminated"), "---\nunterminated");
    }

    #[test]
    fn bind_skill_args_rules() {
        assert_eq!(
            bind_skill_args("Translate to $1: $ARGUMENTS", &["French", "Hi"]),
            "Translate to French: French Hi"
        );
        assert_eq!(bind_skill_args("Do $1 and $3", &["first"]), "Do first and ");
        assert_eq!(bind_skill_args("$$1", &["x"]), "$1");
        assert_eq!(bind_skill_args("$1", &["$ARGUMENTS"]), "$ARGUMENTS");
        assert_eq!(bind_skill_args("$arguments $", &["v"]), " $");
    }
}
