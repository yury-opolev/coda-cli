//! `coda serve`'s argument surface and startup translation, shared verbatim
//! by the standalone `coda-engine` binary.
//!
//! `ServeArgs` is the actual `clap::Args` struct used by both `coda serve`
//! and `coda-engine`'s (sub)command — not a lookalike copy — so the two
//! binaries' `--help` output and accepted flags cannot drift apart. What each
//! binary does with a parsed `ServeArgs` is [`prepare`]: translate the flags
//! into the env vars the engine's own startup already reads, and change into
//! the requested working directory. `prepare` deliberately stops short of
//! calling `coda_serve::serve_stdio()` itself — `coda-boot` does not depend
//! on `coda-serve`, so a binary calls `coda_boot::serve::prepare(&args)?`
//! then `coda_serve::serve_stdio().await` itself, keeping the dependency
//! edge one-directional.

use std::path::PathBuf;

use anyhow::{Context, Result};
use clap::Args;

#[derive(Args)]
pub struct ServeArgs {
    /// Working directory for the session.
    #[arg(long, value_name = "DIR")]
    pub cwd: Option<PathBuf>,

    /// Disable all MCP servers for this session (user and project).
    #[arg(long)]
    pub no_mcp: bool,

    /// Disable only the project `<cwd>/.mcp.json` layer; user servers still load.
    #[arg(long)]
    pub no_project_mcp: bool,

    /// Write a debug log to this file.
    #[arg(long, value_name = "FILE")]
    pub log_file: Option<PathBuf>,

    /// Legacy compatibility hint: a `tracing` `EnvFilter` string. Only its
    /// loudest named level is used, as a fallback default for
    /// `--diagnostic-verbosity`.
    #[arg(long, env = "CODA_LOG", default_value = "warn")]
    pub log_filter: String,

    /// Diagnostic detail level for the essential operational log: normal,
    /// debug, or trace. Takes precedence over `--log-filter`/`CODA_LOG`.
    #[arg(long, value_name = "LEVEL", value_parser = parse_diagnostic_verbosity)]
    pub diagnostic_verbosity: Option<String>,

    /// Initial reasoning-effort level: low, medium, high, xhigh, max, or auto.
    ///
    /// Wired through to the engine startup so a session opened over the raw
    /// serve seam honours the same override the interactive and headless modes
    /// accept.
    #[arg(long, value_name = "LEVEL", value_parser = parse_effort_level)]
    pub effort: Option<String>,

    /// Model to use for the session (session-only override, not saved).
    #[arg(long, value_name = "MODEL")]
    pub model: Option<String>,

    /// Provider hint — used to select the model saved for that provider.
    /// Has no effect when `--model` is also given.
    #[arg(long, value_name = "PROVIDER")]
    pub provider: Option<String>,

    /// Permission mode: default, acceptEdits, plan, or bypassPermissions.
    #[arg(long, value_name = "MODE", conflicts_with = "yolo")]
    pub permission_mode: Option<String>,

    /// Shorthand for `--permission-mode bypassPermissions`.
    #[arg(long, conflicts_with = "permission_mode")]
    pub yolo: bool,

    /// Goal statement the model must fulfil before stopping.
    #[arg(long, value_name = "TEXT")]
    pub goal: Option<String>,

    /// Maximum wall-clock time for the goal. Format: `30m`, `2h`, `90s`.
    #[arg(
        long,
        visible_alias = "goal-max-duration",
        value_name = "DURATION",
        requires = "goal"
    )]
    pub goal_timeout: Option<String>,

    /// Maximum continuation turns the goal supervisor may grant.
    #[arg(
        long,
        visible_alias = "goal-max-continuations",
        value_name = "N",
        requires = "goal"
    )]
    pub max_continuations: Option<i32>,

    /// Custom system prompt for this session (session-only, not saved).
    /// Mutually exclusive with `--system-prompt-file`.
    #[arg(long, value_name = "TEXT", conflicts_with = "system_prompt_file")]
    pub system_prompt: Option<String>,

    /// Read the custom system prompt from this file (UTF-8).
    /// Mutually exclusive with `--system-prompt`.
    #[arg(long, value_name = "FILE", conflicts_with = "system_prompt")]
    pub system_prompt_file: Option<PathBuf>,

    /// Anthropic API key for this session. When provided, the engine uses it
    /// instead of searching the credential store.
    ///
    /// `hide_env_values`: this is a live credential, and clap's default
    /// `--help`/env-derived-default rendering would otherwise print whatever
    /// `CODA_SERVE_API_KEY` currently holds in the invoking shell — a raw
    /// credential leak into a terminal transcript or `--help` capture.
    #[arg(long, value_name = "KEY", env = "CODA_SERVE_API_KEY", hide_env_values = true)]
    pub api_key: Option<String>,

    /// Custom Anthropic API-key endpoint base URL (e.g. a gateway or proxy).
    /// Requires `--api-key`, and outranks `ANTHROPIC_BASE_URL`, which is how a
    /// *stored* or *exported* key is redirected instead. Anthropic API keys
    /// only: GitHub Copilot and Claude.ai resolve their own endpoints.
    #[arg(long, value_name = "URL", requires = "api_key")]
    pub endpoint: Option<String>,
}

impl Default for ServeArgs {
    /// Programmatic defaults. CLI entry points use clap parsing so supported
    /// environment overrides are applied rather than bypassed.
    fn default() -> Self {
        ServeArgs {
            cwd: None,
            no_mcp: false,
            no_project_mcp: false,
            log_file: None,
            log_filter: "warn".to_owned(),
            diagnostic_verbosity: None,
            effort: None,
            model: None,
            provider: None,
            permission_mode: None,
            yolo: false,
            goal: None,
            goal_timeout: None,
            max_continuations: None,
            system_prompt: None,
            system_prompt_file: None,
            api_key: None,
            endpoint: None,
        }
    }
}

impl std::fmt::Debug for ServeArgs {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ServeArgs")
            .field("no_mcp", &self.no_mcp)
            .field("no_project_mcp", &self.no_project_mcp)
            .field("yolo", &self.yolo)
            .field("api_key", &self.api_key.as_ref().map(|_| "<redacted>"))
            .field("endpoint", &self.endpoint.as_ref().map(|_| "<configured>"))
            .field("system_prompt", &self.system_prompt.as_ref().map(|_| "<redacted>"))
            .finish_non_exhaustive()
    }
}

/// Validates a `--effort` value at the parser layer so a syntactically invalid
/// level is rejected before any engine is spawned or terminal entered.
///
/// Accepts the five levels plus `auto` (clear to automatic). The value is
/// lower-cased so `HIGH` and `high` are the same flag. Shared by every launch
/// surface (`coda serve`, `coda run`, interactive `coda`, `coda-engine`) so
/// the accepted set cannot drift between copies.
pub fn parse_effort_level(raw: &str) -> Result<String, String> {
    let level = raw.trim().to_ascii_lowercase();
    match level.as_str() {
        "low" | "medium" | "high" | "xhigh" | "max" | "auto" => Ok(level),
        _ => Err(format!(
            "invalid effort '{raw}' (expected one of: low, medium, high, xhigh, max, auto)"
        )),
    }
}

/// Validates a `--diagnostic-verbosity` value at the parser layer.
pub fn parse_diagnostic_verbosity(raw: &str) -> Result<String, String> {
    let level = raw.trim().to_ascii_lowercase();
    match level.as_str() {
        "normal" | "debug" | "trace" => Ok(level),
        _ => Err(format!(
            "invalid diagnostic verbosity '{raw}' (expected one of: normal, debug, trace)"
        )),
    }
}

/// Translates parsed `ServeArgs` into the working directory and env vars the
/// engine's own startup reads, ahead of a caller running `coda_serve::
/// serve_stdio()`.
///
/// Kept separate from `serve_stdio` itself: `coda-boot` does not depend on
/// `coda-serve` (that dependency would run the other way once `coda-serve`
/// itself wants any of this crate's diagnostics/version helpers), so callers
/// chain the two:
///
/// ```ignore
/// coda_boot::serve::prepare(&args)?;
/// coda_serve::serve_stdio().await
/// ```
pub fn prepare(args: &ServeArgs) -> Result<()> {
    if let Some(dir) = &args.cwd {
        std::env::set_current_dir(dir)
            .with_context(|| format!("failed to enter {}", dir.display()))?;
    }
    // Translate the MCP flags into the env vars the engine reads at startup.
    // A flag only ever sets the toggle; it never clears one the environment
    // already provided, so `CODA_SERVE_DISABLE_MCP=1 coda serve` still works.
    if args.no_mcp {
        std::env::set_var("CODA_SERVE_DISABLE_MCP", "1");
    }
    if args.no_project_mcp {
        std::env::set_var("CODA_DISABLE_PROJECT_MCP", "1");
    }
    // Wire the startup effort override through the env-var seam the engine
    // reads at build time (parallel to the MCP flags above).
    if let Some(level) = &args.effort {
        std::env::set_var("CODA_SERVE_EFFORT", level);
    }
    // Startup model override.
    if let Some(model) = &args.model {
        std::env::set_var("CODA_SERVE_MODEL", model);
    }
    // Provider selection: the engine chooses the requested account at startup
    // and fails closed if it is unavailable (never a different provider).
    if let Some(provider) = &args.provider {
        std::env::set_var("CODA_SERVE_PROVIDER", provider);
    }
    // Permission mode: `--yolo` is shorthand for bypassPermissions.
    let perm_mode = if args.yolo {
        Some("bypassPermissions".to_owned())
    } else {
        args.permission_mode.clone()
    };
    if let Some(mode) = &perm_mode {
        std::env::set_var("CODA_SERVE_PERMISSION_MODE", mode);
    }
    // Goal parameters.
    if let Some(goal) = &args.goal {
        std::env::set_var("CODA_SERVE_GOAL", goal);
    }
    if let Some(timeout) = &args.goal_timeout {
        std::env::set_var("CODA_SERVE_GOAL_TIMEOUT", timeout);
    }
    if let Some(n) = args.max_continuations {
        std::env::set_var("CODA_SERVE_GOAL_MAX_CONTINUATIONS", n.to_string());
    }
    // System prompt (inline or from file, mutually exclusive).
    let system_prompt = crate::resolve_system_prompt(
        args.system_prompt.as_deref(),
        args.system_prompt_file.as_deref(),
    )?;
    if let Some(prompt) = &system_prompt {
        std::env::set_var("CODA_SERVE_SYSTEM_PROMPT", prompt);
    }
    // API key and endpoint for explicit credential override.
    // The endpoint requires the key (enforced by clap `requires`).
    if let Some(key) = &args.api_key {
        // Don't log or forward; only set the env var the transport reads.
        std::env::set_var("CODA_SERVE_API_KEY", key);
    }
    if let Some(url) = &args.endpoint {
        std::env::set_var("CODA_SERVE_ENDPOINT", url);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn effort_level_parser_accepts_the_documented_set() {
        for level in ["low", "medium", "high", "xhigh", "max", "auto"] {
            assert_eq!(parse_effort_level(level).as_deref(), Ok(level));
        }
        assert!(parse_effort_level("nonsense").is_err());
    }

    #[test]
    fn effort_level_parser_lowercases_the_input() {
        assert_eq!(parse_effort_level("XHIGH").as_deref(), Ok("xhigh"));
    }

    #[test]
    fn diagnostic_verbosity_parser_accepts_the_documented_set() {
        for level in ["normal", "debug", "trace"] {
            assert_eq!(parse_diagnostic_verbosity(level).as_deref(), Ok(level));
        }
        assert!(parse_diagnostic_verbosity("nonsense").is_err());
    }

    #[test]
    fn debug_output_excludes_sensitive_startup_values() {
        let args = ServeArgs {
            api_key: Some("FAKE_SECRET_CANARY".into()),
            system_prompt: Some("PRIVATE_PROMPT_CANARY".into()),
            endpoint: Some("https://example.invalid/?key=URL_SECRET_CANARY".into()),
            ..Default::default()
        };
        let debug = format!("{args:?}");
        for secret in ["FAKE_SECRET_CANARY", "PRIVATE_PROMPT_CANARY", "URL_SECRET_CANARY"] {
            assert!(!debug.contains(secret));
        }
    }

    #[test]
    fn default_matches_an_empty_parse() {
        use clap::Parser;
        #[derive(clap::Parser)]
        struct Wrapper {
            #[command(flatten)]
            serve: ServeArgs,
        }
        let parsed = Wrapper::try_parse_from(["wrapper"]).expect("parse").serve;
        let default = ServeArgs::default();
        assert_eq!(parsed.log_filter, std::env::var("CODA_LOG").unwrap_or_else(|_| default.log_filter.clone()));
        assert_eq!(parsed.no_mcp, default.no_mcp);
        assert_eq!(parsed.yolo, default.yolo);
        assert!(parsed.cwd.is_none() && default.cwd.is_none());
        assert!(parsed.effort.is_none() && default.effort.is_none());
    }

    #[test]
    fn prepare_translates_effort_into_the_env_var_the_engine_reads() {
        // Run environment mutation in a separate process, never alongside
        // sibling unit tests that may read the process environment.
        if std::env::var("CODA_BOOT_PREPARE_TEST_CHILD").as_deref() != Ok("1") {
            let status = std::process::Command::new(std::env::current_exe().unwrap())
                .args(["--exact", "serve::tests::prepare_translates_effort_into_the_env_var_the_engine_reads"])
                .env("CODA_BOOT_PREPARE_TEST_CHILD", "1")
                .status().unwrap();
            assert!(status.success());
            return;
        }
        let args = ServeArgs { effort: Some("high".into()), ..ServeArgs::default() };
        prepare(&args).expect("prepare succeeds");
        assert_eq!(std::env::var("CODA_SERVE_EFFORT").as_deref(), Ok("high"));
        std::env::remove_var("CODA_SERVE_EFFORT");
    }

    #[test]
    fn prepare_rejects_an_unreadable_system_prompt_file() {
        let args = ServeArgs {
            system_prompt_file: Some(PathBuf::from("no-such-file-anywhere.txt")),
            ..ServeArgs::default()
        };
        assert!(prepare(&args).is_err());
    }
}
