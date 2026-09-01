//! Component commands: skills, plugins, marketplaces, setup, init, memory.
//!
//! Everything that installs, lists or configures something outside the
//! conversation.

use coda_proto::messages::{self, method};

use super::super::App;
use crate::commands;
use crate::config;
use crate::transcript::NoticeLevel;

impl App {
    /// `/setup` — re-run the setup wizard.
    pub(super) fn cmd_setup(&mut self) {
        use crate::setup;
        let providers = setup::provider_selection_prompt();
        self.output(format!(
            "Setup wizard\n\
             \n\
             {providers}\n\
             \n\
             Choose a provider and run /login <id> to authenticate.\n\
             \n\
             {}",
            "[SEAM: OAuth login handoff requires coda-auth login RPCs, \
             being added by another agent.  Once available, /login will \
             complete the authentication flow interactively.]"
        ));
    }

    /// `/init` — ask the agent to generate a CLAUDE.md for this project.
    pub(super) async fn cmd_init(&mut self) {
        let claude_md = self.paths.project_root.join("CLAUDE.md");
        if claude_md.exists() {
            self.notice(
                "CLAUDE.md already exists; not overwriting. Use /memory to view it.",
                NoticeLevel::Warning,
            );
            return;
        }
        let prompt = concat!(
            "Analyze this codebase and write a concise CLAUDE.md that captures: ",
            "the project purpose, key architecture decisions, important conventions, ",
            "build/test commands, and any gotchas worth knowing. ",
            "Write ONLY the raw Markdown content to CLAUDE.md using the write_file tool — ",
            "no additional commentary, no code fences around the file content."
        );
        self.notice("Sending analysis request to agent…", NoticeLevel::Info);
        self.submit_programmatic(prompt.to_string()).await;
    }

    /// `/memory` — display CLAUDE.md if it exists.
    pub(super) fn cmd_memory(&mut self) {
        let claude_md = self.paths.project_root.join("CLAUDE.md");
        self.output(format!("CLAUDE.md path: {}", claude_md.display()));
        match std::fs::read_to_string(&claude_md) {
            Ok(contents) => self.output(contents),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => self.notice(
                "CLAUDE.md not found. Run /init to generate one for this project.",
                NoticeLevel::Warning,
            ),
            Err(e) => self.notice(format!("Could not read CLAUDE.md: {e}"), NoticeLevel::Error),
        }
    }

    /// `/marketplace [list | add <source> | remove <name>]`.
    pub(super) async fn cmd_marketplace(&mut self, invocation: &commands::Invocation) {
        let words = invocation.words();
        let paths = self.paths.clone();

        enum MktOp {
            List,
            Add(String),
            Remove(String),
            Unsupported(String),
            BadUsage,
        }

        let op = match words.as_slice() {
            [] | ["list"] => MktOp::List,
            ["add", source] => MktOp::Add((*source).to_string()),
            ["remove", name] => MktOp::Remove((*name).to_string()),
            [sub, ..] if ["browse", "install", "search", "refresh"].contains(sub) => {
                MktOp::Unsupported((*sub).to_string())
            }
            _ => MktOp::BadUsage,
        };

        if let MktOp::Unsupported(sub) = op {
            self.notice(
                format!("/{sub} is not yet available in the Rust front-end. Use the C# coda tool."),
                NoticeLevel::Warning,
            );
            return;
        }

        if matches!(op, MktOp::BadUsage) {
            self.notice(
                "Usage: /marketplace [list | add <source> | remove <name>]",
                NoticeLevel::Warning,
            );
            return;
        }

        let result = tokio::task::spawn_blocking(move || -> Result<String, config::ConfigError> {
            let mut settings = config::Settings::load(&paths)?;
            match op {
                MktOp::List => {
                    let markets = settings.marketplaces();
                    if markets.is_empty() {
                        return Ok("No marketplaces configured. Use /marketplace add <source> to register one.".to_string());
                    }
                    let mut out = String::from("Marketplaces\n");
                    for (name, source) in &markets {
                        out.push_str(&format!("  {name}  {source}\n"));
                    }
                    Ok(out.trim_end().to_string())
                }
                MktOp::Add(source) => {
                    // Derive a name from the last URL segment or filename.
                    let name = marketplace_name_from_source(&source);
                    settings.add_marketplace(&name, &source);
                    settings.save()?;
                    Ok(format!("Registered marketplace '{name}' ({source})."))
                }
                MktOp::Remove(name) => {
                    if settings.remove_marketplace(&name) {
                        settings.save()?;
                        Ok(format!("Removed marketplace '{name}'."))
                    } else {
                        Ok(format!("No marketplace named '{name}'."))
                    }
                }
                _ => unreachable!(),
            }
        })
        .await;

        match result {
            Ok(Ok(text)) => self.output(text),
            Ok(Err(e)) => self.notice(format!("Settings error: {e}"), NoticeLevel::Error),
            Err(_) => self.notice("Settings operation was interrupted.", NoticeLevel::Error),
        }
    }

    /// `/plugin [list | info <name> | enable <name> | disable <name>]`.
    pub(super) async fn cmd_plugin(&mut self, invocation: &commands::Invocation) {
        let words = invocation.words();

        enum PluginOp {
            List,
            Info(String),
            SetEnabled(String, bool),
            Unsupported(String),
            BadUsage,
        }

        let op = match words.as_slice() {
            [] | ["list"] => PluginOp::List,
            ["info", name] => PluginOp::Info((*name).to_string()),
            ["enable", name] => PluginOp::SetEnabled((*name).to_string(), true),
            ["disable", name] => PluginOp::SetEnabled((*name).to_string(), false),
            [sub, ..] if ["install", "remove", "update", "prune", "approve", "validate", "new"].contains(sub) => {
                PluginOp::Unsupported((*sub).to_string())
            }
            _ => PluginOp::BadUsage,
        };

        if let PluginOp::Unsupported(sub) = op {
            self.notice(
                format!("plugin {sub} is not yet available in the Rust front-end. Use the C# coda tool or /plugins browser."),
                NoticeLevel::Warning,
            );
            return;
        }

        match op {
            PluginOp::List => {
                // Delegate to the engine for the definitive plugin list.
                match self
                    .fetch::<messages::PluginsListResult>(
                        method::PLUGINS_LIST,
                        Some(serde_json::json!({})),
                    )
                    .await
                {
                    Ok(result) => {
                        let text = if result.plugins.is_empty() {
                            "No plugins installed.".to_string()
                        } else {
                            let mut out = String::from("Plugins\n");
                            for p in &result.plugins {
                                out.push_str(&format!(
                                    "  {} {}\n",
                                    p.name,
                                    p.version.as_deref().unwrap_or("")
                                ));
                            }
                            out.trim_end().to_string()
                        };
                        self.output(text);
                    }
                    Err(e) => self.notice(format!("Could not list plugins: {e}"), NoticeLevel::Error),
                }
            }
            PluginOp::Info(name) => {
                match self
                    .fetch::<messages::PluginsListResult>(
                        method::PLUGINS_LIST,
                        Some(serde_json::json!({})),
                    )
                    .await
                {
                    Ok(result) => {
                        if let Some(plugin) = result.plugins.iter().find(|p| p.name.eq_ignore_ascii_case(&name)) {
                            let version = plugin.version.as_deref().unwrap_or("(unknown)");
                            self.output(format!("Plugin: {}\nVersion: {version}", plugin.name));
                        } else {
                            self.notice(format!("Plugin '{name}' not found."), NoticeLevel::Warning);
                        }
                    }
                    Err(e) => self.notice(format!("Could not fetch plugins: {e}"), NoticeLevel::Error),
                }
            }
            PluginOp::SetEnabled(name, enabled) => {
                let paths = self.paths.clone();
                let result = tokio::task::spawn_blocking(move || -> Result<(), config::ConfigError> {
                    let mut state = config::PluginState::load(&paths)?;
                    state.set_enabled(&name, enabled);
                    state.save()
                })
                .await;

                match result {
                    Ok(Ok(())) => {
                        let word = if enabled { "Enabled" } else { "Disabled" };
                        self.notice(format!("{word} plugin. Restart the engine to apply."), NoticeLevel::Info);
                    }
                    Ok(Err(e)) => self.notice(format!("Could not update plugin state: {e}"), NoticeLevel::Error),
                    Err(_) => self.notice("Plugin state write was interrupted.", NoticeLevel::Error),
                }
            }
            PluginOp::BadUsage => self.notice(
                "Usage: /plugin [list | info <name> | enable <name> | disable <name>]",
                NoticeLevel::Warning,
            ),
            _ => {}
        }
    }

    /// `/skill [<name> [args...]]` — list skills or run one by name.
    pub(super) async fn cmd_skill(&mut self, invocation: &commands::Invocation) {
        let words = invocation.words();

        if words.is_empty() {
            // No arguments: list available skills via the engine.
            match self
                .fetch::<messages::SkillsListResult>(
                    method::SKILLS_LIST,
                    Some(serde_json::json!({})),
                )
                .await
            {
                    Ok(result) => {
                        let text = if result.skills.is_empty() {
                            "No skills available.".to_string()
                        } else {
                            let mut out = String::from("Skills\n");
                            for s in &result.skills {
                                let mark = if s.enabled { "*" } else { " " };
                                out.push_str(&format!(
                                    "  {mark} {}  {}\n",
                                    s.name,
                                    s.description.as_deref().unwrap_or("")
                                ));
                            }
                            out.trim_end().to_string()
                        };
                        self.output(text);
                    }
                    Err(e) => self.notice(format!("Could not list skills: {e}"), NoticeLevel::Error),
                }
                return;
            }

        let name = words[0];
        let args: Vec<&str> = words[1..].to_vec();

        // Look up the SKILL.md in project-local and user-scoped dirs.
        let body = find_local_skill_body(&self.paths, name, &args);
        match body {
            Some(text) => {
                self.notice(format!("Running skill '{name}'…"), NoticeLevel::Info);
                self.submit_programmatic(text).await;
            }
            None => {
                // Report what IS available to help the user.
                let available = list_local_skill_names(&self.paths);
                let list = if available.is_empty() {
                    "(none found locally)".to_string()
                } else {
                    available.join(", ")
                };
                self.notice(
                    format!("Skill '{name}' not found. Available locally: {list}"),
                    NoticeLevel::Warning,
                );
            }
        }
    }
}

/// Derives a marketplace name from a source URL or path.
fn marketplace_name_from_source(source: &str) -> String {
    // Use the last non-empty path segment, stripping common extensions.
    source
        .trim_end_matches('/')
        .rsplit('/')
        .find(|s| !s.is_empty())
        .unwrap_or(source)
        .trim_end_matches(".json")
        .trim_end_matches(".git")
        .to_string()
}

/// Reads a skill body from local SKILL.md files, binding positional args.
///
/// Searches project-scoped then user-scoped skill directories. Returns the
/// bound body, or `None` when no matching skill is found.
fn find_local_skill_body(paths: &config::Paths, name: &str, args: &[&str]) -> Option<String> {
    for dir in [paths.skills_project(), paths.skills_user()] {
        let skill_md = dir.join(name).join("SKILL.md");
        if let Ok(body) = std::fs::read_to_string(&skill_md) {
            return Some(bind_skill_args(&body, args));
        }
    }
    None
}

/// Lists the names of locally available skills.
fn list_local_skill_names(paths: &config::Paths) -> Vec<String> {
    let mut names = Vec::new();
    for dir in [paths.skills_project(), paths.skills_user()] {
        let Ok(entries) = std::fs::read_dir(&dir) else {
            continue;
        };
        for entry in entries.flatten() {
            if entry.path().join("SKILL.md").is_file() {
                if let Some(n) = entry.file_name().to_str() {
                    if !names.iter().any(|e: &String| e.eq_ignore_ascii_case(n)) {
                        names.push(n.to_string());
                    }
                }
            }
        }
    }
    names.sort();
    names
}

/// Substitutes skill argument placeholders in a single pass, preventing
/// re-expansion of substituted values.
///
/// Rules (matching C# `SkillArgumentBinder`):
/// - `$$`         → literal `$`
/// - `$ARGUMENTS` → all positional args joined by a single space (case-sensitive)
/// - `$N` (N ≥ 1) → the N-th positional arg, or empty if out of range
/// - `$identifier` → empty (named args from frontmatter; treated as unknown
///                   here because the Rust TUI does not parse frontmatter)
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
                // $$ → literal $
                chars.next();
                out.push('$');
            }
            Some((_, d)) if d.is_ascii_digit() => {
                let d = *d;
                let mut num_str = String::new();
                num_str.push(d);
                chars.next();
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
                // $0 or out-of-range → push nothing (renders as empty)
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
                // Any other named identifier → empty (unknown; no frontmatter)
            }
            _ => {
                // Bare `$` not followed by a recognisable pattern → keep.
                out.push('$');
            }
        }
    }

    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn marketplace_name_strips_extension_and_uses_last_segment() {
        assert_eq!(marketplace_name_from_source("https://example.com/plugins.json"), "plugins");
        assert_eq!(marketplace_name_from_source("https://example.com/my-marketplace"), "my-marketplace");
        assert_eq!(marketplace_name_from_source("git@github.com:org/repo.git"), "repo");
        assert_eq!(marketplace_name_from_source("/local/path/plugins/"), "plugins");
    }

    #[test]
    fn bind_skill_args_substitutes_positional_placeholders() {
        let body = "Translate to $1: $ARGUMENTS";
        let result = bind_skill_args(body, &["French", "Hello world"]);
        assert_eq!(result, "Translate to French: French Hello world");
    }

    #[test]
    fn bind_skill_args_handles_missing_args_gracefully() {
        // Out-of-range positionals render as empty (not left as literal `$3`).
        let result = bind_skill_args("Do $1 and $3", &["first"]);
        assert_eq!(result, "Do first and ");
    }

    #[test]
    fn bind_skill_args_double_dollar_produces_a_literal_dollar() {
        assert_eq!(bind_skill_args("Cost: $$10", &[]), "Cost: $10");
    }

    #[test]
    fn bind_skill_args_double_dollar_is_not_re_expanded() {
        // $$ → $, then $1 on the next pass must NOT be expanded.
        assert_eq!(bind_skill_args("$$1", &["ignored"]), "$1");
    }

    #[test]
    fn bind_skill_args_substituted_value_is_not_re_expanded() {
        // The value "$ARGUMENTS" inserted for $1 must not trigger a second pass.
        assert_eq!(bind_skill_args("$1", &["$ARGUMENTS"]), "$ARGUMENTS");
    }

    #[test]
    fn bind_skill_args_positional_zero_renders_empty() {
        assert_eq!(bind_skill_args("$0", &["a"]), "");
    }

    #[test]
    fn bind_skill_args_unknown_identifier_renders_empty() {
        // $nonexistent is not $ARGUMENTS and not positional → empty.
        assert_eq!(bind_skill_args("$nonexistent", &["val"]), "");
    }

    #[test]
    fn bind_skill_args_arguments_is_case_sensitive() {
        // $arguments (lowercase) is NOT the special $ARGUMENTS token → empty.
        assert_eq!(bind_skill_args("$arguments", &["val"]), "");
    }
}

