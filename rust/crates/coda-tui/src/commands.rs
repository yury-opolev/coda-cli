//! Slash commands.
//!
//! Commands are parsed from the composer and dispatched to either a local
//! handler or an engine call. Keeping the registry declarative means `/help`
//! and completion are generated from the same source as dispatch, so they can
//! never drift apart.

use coda_render::tool::ToolDisplayMode;

/// Where a command's work happens.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Scope {
    /// Handled entirely in the front-end.
    Local,
    /// Requires a call to the engine.
    Engine,
}

/// A registered command.
#[derive(Debug, Clone, Copy)]
pub struct CommandSpec {
    pub name: &'static str,
    pub aliases: &'static [&'static str],
    pub args: &'static str,
    pub summary: &'static str,
    pub scope: Scope,
}

/// Every built-in command.
pub const COMMANDS: &[CommandSpec] = &[
    CommandSpec {
        name: "help",
        aliases: &["?"],
        args: "[command]",
        summary: "Show available commands.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "clear",
        aliases: &["cls"],
        args: "",
        summary: "Clear the transcript.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "exit",
        aliases: &["quit", "q"],
        args: "",
        summary: "Leave Coda.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "model",
        aliases: &[],
        args: "[id]",
        summary: "Show or switch the active model.",
        scope: Scope::Engine,
    },
    CommandSpec {
        name: "models",
        aliases: &[],
        args: "[--refresh]",
        summary: "List the provider's models.",
        scope: Scope::Engine,
    },
    CommandSpec {
        name: "effort",
        aliases: &[],
        args: "<low|medium|high|max|auto>",
        summary: "Set the reasoning effort level.",
        scope: Scope::Engine,
    },
    CommandSpec {
        name: "goal",
        aliases: &[],
        args: "[text]",
        summary: "Set or clear the session goal.",
        scope: Scope::Engine,
    },
    CommandSpec {
        name: "theme",
        aliases: &[],
        args: "[name]",
        summary: "Show or switch the colour theme.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "tools",
        aliases: &["tool-display"],
        args: "[full|compact|summary|hidden]",
        summary: "Set how much tool detail is shown.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "status",
        aliases: &[],
        args: "",
        summary: "Show session status.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "context",
        aliases: &[],
        args: "",
        summary: "Show context window usage.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "interrupt",
        aliases: &["stop"],
        args: "",
        summary: "Interrupt the running turn.",
        scope: Scope::Engine,
    },
    CommandSpec {
        name: "history",
        aliases: &[],
        args: "",
        summary: "Show the conversation history the engine holds.",
        scope: Scope::Engine,
    },
    CommandSpec {
        name: "schedule",
        aliases: &["schedules"],
        args: "",
        summary: "List scheduled tasks.",
        scope: Scope::Engine,
    },
    CommandSpec {
        name: "skills",
        aliases: &[],
        args: "",
        summary: "List available skills.",
        scope: Scope::Engine,
    },
    CommandSpec {
        name: "plugins",
        aliases: &[],
        args: "",
        summary: "List installed plugins.",
        scope: Scope::Engine,
    },
    CommandSpec {
        name: "hooks",
        aliases: &[],
        args: "",
        summary: "List configured hooks.",
        scope: Scope::Engine,
    },
    CommandSpec {
        name: "mcp",
        aliases: &[],
        args: "",
        summary: "Browse configured MCP servers.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "tasks",
        aliases: &[],
        args: "",
        summary: "Browse background tasks.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "version",
        aliases: &[],
        args: "",
        summary: "Show the Coda version.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "doctor",
        aliases: &[],
        args: "",
        summary: "Print diagnostic information.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "cost",
        aliases: &[],
        args: "",
        summary: "Show token usage for this session.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "init",
        aliases: &[],
        args: "",
        summary: "Generate a CLAUDE.md memory file for this project.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "memory",
        aliases: &[],
        args: "",
        summary: "Show the project CLAUDE.md memory file.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "output-style",
        aliases: &["style"],
        args: "[<style>]",
        summary: "Show or set the output style (default, concise, explanatory, code-reviewer).",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "permissions",
        aliases: &["mode"],
        args: "[<mode>]",
        summary: "Show or set the tool-permission mode.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "yolo",
        aliases: &[],
        args: "",
        summary: "Allow all tools without asking (bypass permissions).",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "provider",
        aliases: &[],
        args: "[<id>]",
        summary: "Show the active provider, or connect to a different one.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "headers",
        aliases: &[],
        args: "[--set <name> <value> | --remove <name>]",
        summary: "Show or edit custom outgoing HTTP headers.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "log",
        aliases: &[],
        args: "[<level> | stderr on|off | off]",
        summary: "Show or set telemetry logging level.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "marketplace",
        aliases: &[],
        args: "[list | add <source> | remove <name>]",
        summary: "Manage plugin marketplaces.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "plugin",
        aliases: &[],
        args: "[list | info <name> | enable <name> | disable <name>]",
        summary: "Manage plugins: list, enable, disable.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "skill",
        aliases: &[],
        args: "[<name> [args...]]",
        summary: "Run a skill by name, or list skills if no name is given.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "export",
        aliases: &[],
        args: "[<path>]",
        summary: "Export the conversation to a Markdown file.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "diff",
        aliases: &[],
        args: "",
        summary: "Show uncommitted git changes in the working directory.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "image",
        aliases: &[],
        args: "<path>",
        summary: "Attach an image to the next message.",
        scope: Scope::Local,
    },
    CommandSpec {
        name: "setup",
        aliases: &[],
        args: "",
        summary: "Run the setup wizard to connect to a provider.",
        scope: Scope::Local,
    },
];

impl CommandSpec {
    /// Whether `name` addresses this command, by its name or an alias.
    pub fn matches(&self, name: &str) -> bool {
        self.name.eq_ignore_ascii_case(name)
            || self.aliases.iter().any(|a| a.eq_ignore_ascii_case(name))
    }

    /// The `/name <args>` usage string.
    pub fn usage(&self) -> String {
        if self.args.is_empty() {
            format!("/{}", self.name)
        } else {
            format!("/{} {}", self.name, self.args)
        }
    }
}

/// A parsed command invocation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Invocation {
    pub name: String,
    pub args: String,
}

impl Invocation {
    /// The arguments split on whitespace.
    pub fn words(&self) -> Vec<&str> {
        self.args.split_whitespace().collect()
    }

    /// The first argument, if any.
    pub fn first(&self) -> Option<&str> {
        self.words().into_iter().next()
    }
}

/// Parses composer text as a command.
///
/// Returns `None` for anything that is not a command, including a bare `/` and
/// text that merely contains a slash.
pub fn parse(input: &str) -> Option<Invocation> {
    let trimmed = input.trim_start();
    let rest = trimmed.strip_prefix('/')?;

    // `//` escapes a literal leading slash rather than naming a command.
    if rest.starts_with('/') {
        return None;
    }

    let (name, args) = match rest.find(char::is_whitespace) {
        Some(index) => (&rest[..index], rest[index..].trim()),
        None => (rest, ""),
    };

    if name.is_empty() {
        return None;
    }

    Some(Invocation {
        name: name.to_string(),
        args: args.to_string(),
    })
}

/// Looks up a command by name or alias.
pub fn lookup(name: &str) -> Option<&'static CommandSpec> {
    COMMANDS.iter().find(|spec| spec.matches(name))
}

/// Commands whose name or aliases start with `prefix`, for completion.
///
/// `prefix` may include the leading slash.
pub fn complete(prefix: &str) -> Vec<&'static CommandSpec> {
    let needle = prefix.trim_start_matches('/').to_ascii_lowercase();
    COMMANDS
        .iter()
        .filter(|spec| {
            spec.name.starts_with(&needle)
                || spec.aliases.iter().any(|a| a.starts_with(&needle))
        })
        .collect()
}

/// The `/help` text, either an overview or one command's detail.
pub fn help(topic: Option<&str>) -> String {
    if let Some(topic) = topic.filter(|t| !t.is_empty()) {
        return match lookup(topic) {
            Some(spec) => {
                let mut out = format!("{}\n  {}", spec.usage(), spec.summary);
                if !spec.aliases.is_empty() {
                    out.push_str(&format!("\n  aliases: {}", spec.aliases.join(", ")));
                }
                out
            }
            None => format!("Unknown command: /{topic}"),
        };
    }

    let width = COMMANDS
        .iter()
        .map(|spec| spec.usage().len())
        .max()
        .unwrap_or(0);

    let mut out = String::from("Commands:\n");
    for spec in COMMANDS {
        out.push_str(&format!(
            "  {:<width$}  {}\n",
            spec.usage(),
            spec.summary,
            width = width
        ));
    }
    out.push_str("\nAnything else is sent to the agent.");
    out
}

/// Parses a `/tools` argument into a display mode.
pub fn parse_display_mode(arg: Option<&str>) -> Result<ToolDisplayMode, String> {
    match ToolDisplayMode::parse(arg) {
        (mode, true) => Ok(mode),
        (_, false) => Err(format!(
            "Unknown tool display mode: {}. Use full, compact, summary or hidden.",
            arg.unwrap_or("")
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_a_bare_command() {
        let invocation = parse("/help").expect("a command");
        assert_eq!(invocation.name, "help");
        assert!(invocation.args.is_empty());
    }

    #[test]
    fn parses_a_command_with_arguments() {
        let invocation = parse("/model  gpt-5  ").expect("a command");
        assert_eq!(invocation.name, "model");
        assert_eq!(invocation.args, "gpt-5");
        assert_eq!(invocation.first(), Some("gpt-5"));
    }

    #[test]
    fn parses_multiple_arguments() {
        let invocation = parse("/goal ship the thing").expect("a command");
        assert_eq!(invocation.args, "ship the thing");
        assert_eq!(invocation.words(), vec!["ship", "the", "thing"]);
    }

    #[test]
    fn tolerates_leading_whitespace() {
        assert_eq!(parse("   /help").expect("a command").name, "help");
    }

    #[test]
    fn plain_text_is_not_a_command() {
        assert!(parse("hello").is_none());
        assert!(parse("send /help to the agent").is_none());
    }

    #[test]
    fn a_bare_slash_is_not_a_command() {
        assert!(parse("/").is_none());
        assert!(parse("  / ").is_none());
    }

    #[test]
    fn a_double_slash_escapes_to_literal_text() {
        assert!(parse("//not-a-command").is_none());
    }

    #[test]
    fn empty_input_is_not_a_command() {
        assert!(parse("").is_none());
        assert!(parse("   ").is_none());
    }

    #[test]
    fn looks_commands_up_by_name() {
        assert_eq!(lookup("help").expect("help").name, "help");
        assert_eq!(lookup("model").expect("model").name, "model");
    }

    #[test]
    fn looks_commands_up_by_alias() {
        assert_eq!(lookup("q").expect("q").name, "exit");
        assert_eq!(lookup("quit").expect("quit").name, "exit");
        assert_eq!(lookup("cls").expect("cls").name, "clear");
        assert_eq!(lookup("stop").expect("stop").name, "interrupt");
    }

    #[test]
    fn lookup_is_case_insensitive() {
        assert_eq!(lookup("HELP").expect("help").name, "help");
        assert_eq!(lookup("Quit").expect("quit").name, "exit");
    }

    #[test]
    fn unknown_commands_are_not_found() {
        assert!(lookup("frobnicate").is_none());
    }

    #[test]
    fn completes_by_prefix() {
        let names: Vec<&str> = complete("model").iter().map(|s| s.name).collect();
        assert_eq!(names, vec!["model", "models"]);
    }

    #[test]
    fn completion_accepts_a_leading_slash() {
        assert_eq!(complete("/mod").len(), complete("mod").len());
    }

    #[test]
    fn completion_matches_aliases_too() {
        let names: Vec<&str> = complete("cl").iter().map(|s| s.name).collect();
        assert!(names.contains(&"clear"), "got {names:?}");
    }

    #[test]
    fn an_empty_prefix_completes_to_everything() {
        assert_eq!(complete("").len(), COMMANDS.len());
    }

    #[test]
    fn an_unmatched_prefix_completes_to_nothing() {
        assert!(complete("zzz").is_empty());
    }

    #[test]
    fn usage_includes_arguments_when_a_command_takes_them() {
        let spec = lookup("model").expect("model");
        assert_eq!(spec.usage(), "/model [id]");

        let spec = lookup("clear").expect("clear");
        assert_eq!(spec.usage(), "/clear");
    }

    #[test]
    fn help_lists_every_command() {
        let text = help(None);
        for spec in COMMANDS {
            assert!(
                text.contains(spec.name),
                "/{} missing from help",
                spec.name
            );
        }
    }

    #[test]
    fn help_for_one_command_shows_its_usage_and_aliases() {
        let text = help(Some("exit"));
        assert!(text.contains("/exit"));
        assert!(text.contains("Leave Coda."));
        assert!(text.contains("quit"));
    }

    #[test]
    fn help_reports_an_unknown_topic() {
        assert!(help(Some("nope")).contains("Unknown command"));
    }

    #[test]
    fn parses_tool_display_modes() {
        assert_eq!(parse_display_mode(Some("full")), Ok(ToolDisplayMode::Full));
        assert_eq!(
            parse_display_mode(Some("hidden")),
            Ok(ToolDisplayMode::Hidden)
        );
    }

    #[test]
    fn rejects_an_unknown_tool_display_mode() {
        let error = parse_display_mode(Some("loud")).expect_err("should be rejected");
        assert!(error.contains("loud"));
    }

    #[test]
    fn every_command_name_and_alias_is_unique() {
        let mut seen = std::collections::HashSet::new();
        for spec in COMMANDS {
            assert!(seen.insert(spec.name), "duplicate command /{}", spec.name);
            for alias in spec.aliases {
                assert!(seen.insert(alias), "duplicate alias /{alias}");
            }
        }
    }

    #[test]
    fn every_command_has_a_summary() {
        for spec in COMMANDS {
            assert!(!spec.summary.is_empty(), "/{} has no summary", spec.name);
            assert!(
                spec.summary.ends_with('.'),
                "/{} summary should be a sentence",
                spec.name
            );
        }
    }

    #[test]
    fn every_command_resolves_through_lookup() {
        for spec in COMMANDS {
            assert!(lookup(spec.name).is_some(), "/{} not found", spec.name);
            for alias in spec.aliases {
                assert!(lookup(alias).is_some(), "/{alias} not found");
            }
        }
    }

    #[test]
    fn output_style_resolves_via_its_style_alias() {
        let spec = lookup("style").expect("style alias");
        assert_eq!(spec.name, "output-style");
    }

    #[test]
    fn permissions_resolves_via_its_mode_alias() {
        let spec = lookup("mode").expect("mode alias");
        assert_eq!(spec.name, "permissions");
    }

    #[test]
    fn all_new_commands_are_local_scope() {
        let new_names = [
            "init", "memory", "output-style", "permissions", "yolo", "provider",
            "headers", "log", "marketplace", "plugin", "skill", "export", "diff", "image",
        ];
        for name in new_names {
            let spec = lookup(name).unwrap_or_else(|| panic!("/{name} not found"));
            assert_eq!(
                spec.scope,
                Scope::Local,
                "/{name} should be Local scope"
            );
        }
    }

    #[test]
    fn help_text_includes_every_new_command() {
        let text = help(None);
        let new_names = [
            "init", "memory", "output-style", "permissions", "yolo", "provider",
            "headers", "log", "marketplace", "plugin", "skill", "export", "diff", "image",
        ];
        for name in new_names {
            assert!(text.contains(name), "/{name} missing from help output");
        }
    }
}
