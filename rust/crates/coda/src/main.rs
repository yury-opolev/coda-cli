//! The unified `coda` binary.
//!
//! One executable, three modes, matching the C# CLI surface:
//!
//! ```text
//! coda                  interactive TUI
//! coda serve            JSON-RPC engine over stdio
//! coda run -p "<task>"  headless one-shot
//! ```
//!
//! The interactive mode drives the engine over the same JSON-RPC seam the C#
//! build used, but defaults the engine to **this same executable**. That keeps
//! one process model, one protocol, and one set of contract tests, while
//! removing the .NET dependency entirely. Running the agent in-process would
//! be marginally faster but would bypass the very boundary the contract tests
//! exercise, so it is deliberately not done here.

use std::ffi::OsString;
use std::path::PathBuf;

use anyhow::{Context, Result};
use clap::{Args, Parser, Subcommand};
use coda_client::EngineCommand;
use coda_render::theme::{ColorDepth, Theme};
use coda_tui::app::App;
use coda_tui::startup::SessionIntent;
use coda_tui::terminal::{install_panic_hook, TerminalGuard};

#[derive(Debug, Parser)]
#[command(
    name = "coda",
    about = "Coda — an agentic coding assistant",
    // Reported from version.json rather than the crate version, so `--version`
    // agrees with the banner and continues the C# build's version line.
    version = coda_tui::branding::version(),
    disable_help_subcommand = true,
    args_conflicts_with_subcommands = true
)]
struct Cli {
    #[command(subcommand)]
    command: Option<Command>,

    #[command(flatten)]
    interactive: InteractiveArgs,
}

#[derive(Debug, Subcommand)]
enum Command {
    /// Run the engine as a JSON-RPC server over stdio.
    Serve(ServeArgs),
    /// Send a single task, print the result, and exit.
    Run(RunArgs),
}

#[derive(Debug, Args)]
struct InteractiveArgs {
    /// Executable used to launch the engine. Defaults to this binary.
    #[arg(long, env = "CODA_ENGINE")]
    engine: Option<PathBuf>,

    /// Extra arguments passed to the engine after `serve`.
    #[arg(long = "engine-arg", value_name = "ARG", allow_hyphen_values = true)]
    engine_args: Vec<String>,

    /// Working directory for the session. Defaults to the current directory.
    #[arg(long, short = 'C', value_name = "DIR", visible_alias = "cwd")]
    directory: Option<PathBuf>,

    /// Write a debug log to this file.
    #[arg(long, value_name = "FILE")]
    log_file: Option<PathBuf>,

    /// Legacy compatibility hint: a `tracing` `EnvFilter` string (e.g. `debug`
    /// or `coda_client=trace`). Only its loudest named level (`trace` >
    /// `debug` > anything else) is used, as a fallback default for
    /// `--diagnostic-verbosity`. It does not enable arbitrary raw tracing.
    #[arg(long, env = "CODA_LOG", default_value = "warn")]
    log_filter: String,

    /// Diagnostic detail level for the essential operational log: normal,
    /// debug, or trace. Takes precedence over `--log-filter`/`CODA_LOG`.
    #[arg(long, value_name = "LEVEL", value_parser = parse_diagnostic_verbosity)]
    diagnostic_verbosity: Option<String>,

    /// Disable mouse capture, which some terminals handle poorly.
    #[arg(long)]
    no_mouse: bool,

    /// Continue the most recent session in this directory.
    #[arg(long = "continue", short = 'c', conflicts_with_all = ["resume", "fork"])]
    continue_latest: bool,

    /// Resume a session. Without an id, the most recent one.
    ///
    /// The exit summary prints this exact command, so it has to accept what it
    /// advertises.
    #[arg(long, short = 'r', value_name = "ID", num_args = 0..=1, conflicts_with = "fork")]
    resume: Option<Option<String>>,

    /// Open a copy of a session, leaving the original untouched.
    /// Without an id, copies the most recent one.
    #[arg(long, short = 'f', value_name = "ID", num_args = 0..=1)]
    fork: Option<Option<String>>,

    /// Initial reasoning-effort level: low, medium, high, xhigh, or max.
    ///
    /// Applied as a session-only override; does not persist to settings. The
    /// engine uses the saved per-model preference when this is not given.
    #[arg(long, value_name = "LEVEL", value_parser = parse_effort_level)]
    effort: Option<String>,

    /// Model to use for this session (session-only override, not saved).
    #[arg(long, value_name = "MODEL")]
    model: Option<String>,

    /// Provider to use (selects the model saved for that provider in settings).
    /// Has no effect when `--model` is also given.
    #[arg(long, value_name = "PROVIDER")]
    provider: Option<String>,

    /// Permission mode: default, acceptEdits, plan, or bypassPermissions.
    ///
    /// Session-only override. Aliases: ask=default, edits=acceptEdits, yolo=bypassPermissions.
    #[arg(long, value_name = "MODE", conflicts_with = "yolo")]
    permission_mode: Option<String>,

    /// Shorthand for `--permission-mode bypassPermissions`.
    /// Session-only override; does not persist to settings.
    #[arg(long, conflicts_with = "permission_mode")]
    yolo: bool,

    /// Reject with an error — `--yolo-safe` is not yet implemented.
    #[arg(long, hide = true)]
    yolo_safe: bool,

    /// Goal statement the model must fulfil before stopping.
    /// Session-only; the engine judges each turn against this goal.
    #[arg(long, value_name = "TEXT")]
    goal: Option<String>,

    /// Maximum wall-clock time allowed for the goal. Format: `30m`, `2h`, `90s`.
    #[arg(
        long,
        visible_alias = "goal-max-duration",
        value_name = "DURATION",
        requires = "goal"
    )]
    goal_timeout: Option<String>,

    /// Maximum number of continuation turns the goal supervisor may grant.
    #[arg(
        long,
        visible_alias = "goal-max-continuations",
        value_name = "N",
        requires = "goal"
    )]
    max_continuations: Option<i32>,

    /// Custom system prompt for this session (session-only, not saved).
    /// Mutually exclusive with `--system-prompt-file`.
    #[arg(long, value_name = "TEXT", conflicts_with = "system_prompt_file")]
    system_prompt: Option<String>,

    /// Read the custom system prompt from this file (UTF-8).
    /// Mutually exclusive with `--system-prompt`.
    #[arg(long, value_name = "FILE", conflicts_with = "system_prompt")]
    system_prompt_file: Option<PathBuf>,
}

#[derive(Debug, Args)]
struct ServeArgs {
    /// Working directory for the session.
    #[arg(long, value_name = "DIR")]
    cwd: Option<PathBuf>,

    /// Disable all MCP servers for this session (user and project).
    #[arg(long)]
    no_mcp: bool,

    /// Disable only the project `<cwd>/.mcp.json` layer; user servers still load.
    #[arg(long)]
    no_project_mcp: bool,

    /// Write a debug log to this file.
    #[arg(long, value_name = "FILE")]
    log_file: Option<PathBuf>,

    /// Legacy compatibility hint: a `tracing` `EnvFilter` string. Only its
    /// loudest named level is used, as a fallback default for
    /// `--diagnostic-verbosity`.
    #[arg(long, env = "CODA_LOG", default_value = "warn")]
    log_filter: String,

    /// Diagnostic detail level for the essential operational log: normal,
    /// debug, or trace. Takes precedence over `--log-filter`/`CODA_LOG`.
    #[arg(long, value_name = "LEVEL", value_parser = parse_diagnostic_verbosity)]
    diagnostic_verbosity: Option<String>,

    /// Initial reasoning-effort level: low, medium, high, xhigh, max, or auto.
    ///
    /// Wired through to the engine startup so a session opened over the raw
    /// serve seam honours the same override the interactive and headless modes
    /// accept.
    #[arg(long, value_name = "LEVEL", value_parser = parse_effort_level)]
    effort: Option<String>,

    /// Model to use for the session (session-only override, not saved).
    #[arg(long, value_name = "MODEL")]
    model: Option<String>,

    /// Provider hint — used to select the model saved for that provider.
    /// Has no effect when `--model` is also given.
    #[arg(long, value_name = "PROVIDER")]
    provider: Option<String>,

    /// Permission mode: default, acceptEdits, plan, or bypassPermissions.
    #[arg(long, value_name = "MODE", conflicts_with = "yolo")]
    permission_mode: Option<String>,

    /// Shorthand for `--permission-mode bypassPermissions`.
    #[arg(long, conflicts_with = "permission_mode")]
    yolo: bool,

    /// Goal statement the model must fulfil before stopping.
    #[arg(long, value_name = "TEXT")]
    goal: Option<String>,

    /// Maximum wall-clock time for the goal. Format: `30m`, `2h`, `90s`.
    #[arg(
        long,
        visible_alias = "goal-max-duration",
        value_name = "DURATION",
        requires = "goal"
    )]
    goal_timeout: Option<String>,

    /// Maximum continuation turns the goal supervisor may grant.
    #[arg(
        long,
        visible_alias = "goal-max-continuations",
        value_name = "N",
        requires = "goal"
    )]
    max_continuations: Option<i32>,

    /// Custom system prompt for this session (session-only, not saved).
    /// Mutually exclusive with `--system-prompt-file`.
    #[arg(long, value_name = "TEXT", conflicts_with = "system_prompt_file")]
    system_prompt: Option<String>,

    /// Read the custom system prompt from this file (UTF-8).
    /// Mutually exclusive with `--system-prompt`.
    #[arg(long, value_name = "FILE", conflicts_with = "system_prompt")]
    system_prompt_file: Option<PathBuf>,

    /// Anthropic API key for this session. When provided, the engine uses it
    /// instead of searching the credential store.
    #[arg(long, value_name = "KEY", env = "CODA_SERVE_API_KEY")]
    api_key: Option<String>,

    /// Custom API endpoint base URL (e.g. a proxy). Requires `--api-key`.
    #[arg(long, value_name = "URL", requires = "api_key")]
    endpoint: Option<String>,
}

#[derive(Debug, Args)]
struct RunArgs {
    /// The task to run.
    ///
    /// `allow_hyphen_values` matters here: a perfectly ordinary task such as
    /// `-p "--explain this flag"` would otherwise be rejected as an unknown
    /// argument.
    #[arg(
        long,
        short = 'p',
        value_name = "TEXT",
        required = true,
        allow_hyphen_values = true
    )]
    prompt: String,

    /// Emit machine-readable JSON instead of prose.
    #[arg(long)]
    json: bool,

    /// Working directory for the session.
    #[arg(long, value_name = "DIR", visible_alias = "directory")]
    cwd: Option<PathBuf>,

    /// Executable used to launch the engine. Defaults to this binary.
    #[arg(long, env = "CODA_ENGINE")]
    engine: Option<PathBuf>,

    /// Write a debug log to this file.
    #[arg(long, value_name = "FILE")]
    log_file: Option<PathBuf>,

    /// Legacy compatibility hint: a `tracing` `EnvFilter` string. Only its
    /// loudest named level is used, as a fallback default for
    /// `--diagnostic-verbosity`.
    #[arg(long, env = "CODA_LOG", default_value = "warn")]
    log_filter: String,

    /// Diagnostic detail level for the essential operational log: normal,
    /// debug, or trace. Takes precedence over `--log-filter`/`CODA_LOG`.
    #[arg(long, value_name = "LEVEL", value_parser = parse_diagnostic_verbosity)]
    diagnostic_verbosity: Option<String>,

    /// Initial reasoning-effort level: low, medium, high, xhigh, or max.
    #[arg(long, value_name = "LEVEL", value_parser = parse_effort_level)]
    effort: Option<String>,

    /// Model to use for this run (session-only override, not saved).
    #[arg(long, value_name = "MODEL")]
    model: Option<String>,

    /// Provider hint — used to select the model saved for that provider.
    #[arg(long, value_name = "PROVIDER")]
    provider: Option<String>,

    /// Permission mode: default, acceptEdits, plan, or bypassPermissions.
    #[arg(long, value_name = "MODE", conflicts_with = "yolo")]
    permission_mode: Option<String>,

    /// Shorthand for `--permission-mode bypassPermissions`.
    #[arg(long, conflicts_with = "permission_mode")]
    yolo: bool,

    /// Goal statement for the run.
    #[arg(long, value_name = "TEXT")]
    goal: Option<String>,

    /// Maximum wall-clock time for the goal. Format: `30m`, `2h`, `90s`.
    #[arg(
        long,
        visible_alias = "goal-max-duration",
        value_name = "DURATION",
        requires = "goal"
    )]
    goal_timeout: Option<String>,

    /// Maximum continuation turns the goal supervisor may grant.
    #[arg(
        long,
        visible_alias = "goal-max-continuations",
        value_name = "N",
        requires = "goal"
    )]
    max_continuations: Option<i32>,

    /// Custom system prompt for this run (session-only, not saved).
    /// Mutually exclusive with `--system-prompt-file`.
    #[arg(long, value_name = "TEXT", conflicts_with = "system_prompt_file")]
    system_prompt: Option<String>,

    /// Read the custom system prompt from this file (UTF-8).
    /// Mutually exclusive with `--system-prompt`.
    #[arg(long, value_name = "FILE", conflicts_with = "system_prompt")]
    system_prompt_file: Option<PathBuf>,

    /// Continue the most recent session in this directory.
    #[arg(long = "continue", short = 'c', conflicts_with_all = ["resume", "fork"])]
    continue_latest: bool,

    /// Resume a session. Without an id, the most recent one.
    #[arg(long, short = 'r', value_name = "ID", num_args = 0..=1, conflicts_with = "fork")]
    resume: Option<Option<String>>,

    /// Open a copy of a session, leaving the original untouched.
    #[arg(long, short = 'f', value_name = "ID", num_args = 0..=1)]
    fork: Option<Option<String>>,
}

/// Validates a `--effort` value at the parser layer so a syntactically invalid
/// level is rejected before any engine is spawned or terminal entered.
///
/// Accepts the five levels plus `auto` (clear to automatic). The value is
/// lower-cased so `HIGH` and `high` are the same flag.
fn parse_effort_level(raw: &str) -> Result<String, String> {
    let level = raw.trim().to_ascii_lowercase();
    match level.as_str() {
        "low" | "medium" | "high" | "xhigh" | "max" | "auto" => Ok(level),
        _ => Err(format!(
            "invalid effort '{raw}' (expected one of: low, medium, high, xhigh, max, auto)"
        )),
    }
}

/// Validates a `--diagnostic-verbosity` value at the parser layer.
fn parse_diagnostic_verbosity(raw: &str) -> Result<String, String> {
    let level = raw.trim().to_ascii_lowercase();
    match level.as_str() {
        "normal" | "debug" | "trace" => Ok(level),
        _ => Err(format!(
            "invalid diagnostic verbosity '{raw}' (expected one of: normal, debug, trace)"
        )),
    }
}

/// Resolves and opens this process's diagnostic logger for `coda`'s three
/// entrypoints (interactive/run/serve). Delegates to `coda_tui::diagnostics`,
/// which is shared with the standalone `coda-tui` binary — both `coda`
/// (no subcommand) and `coda-tui` are the same `ProcessRole::Tui` frontend.
fn init_diagnostics(
    role: coda_diagnostics::ProcessRole,
    explicit_file: Option<PathBuf>,
    explicit_verbosity: Option<&str>,
    legacy_filter: &str,
) -> Result<(coda_diagnostics::DiagnosticContext, PathBuf)> {
    coda_tui::diagnostics::init(role, explicit_file, explicit_verbosity, legacy_filter)
}

/// Sets the environment variables a spawned engine child reads to correlate
/// its own diagnostic log with this process's run id and directory.
fn forward_diagnostics_env(command: EngineCommand, ctx: &coda_diagnostics::DiagnosticContext, directory: &std::path::Path) -> EngineCommand {
    coda_tui::diagnostics::forward_env(command, ctx, directory)
}

/// Resolves the system prompt from either an inline string or a file.
///
/// The two are mutually exclusive (enforced by clap). A file is read as
/// UTF-8; a non-UTF-8 file is a hard error rather than a silent lossy read.
/// Returns `None` when neither flag was given.
fn resolve_system_prompt(
    inline: Option<&str>,
    file: Option<&std::path::Path>,
) -> Result<Option<String>> {
    if let Some(text) = inline {
        return Ok(Some(text.to_owned()));
    }
    if let Some(path) = file {
        let content = std::fs::read(path)
            .with_context(|| format!("failed to read system prompt file: {}", path.display()))?;
        let text = String::from_utf8(content).with_context(|| {
            format!(
                "system prompt file is not valid UTF-8: {}",
                path.display()
            )
        })?;
        return Ok(Some(text));
    }
    Ok(None)
}

fn main() -> Result<()> {
    let cli = Cli::parse();

    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .context("failed to start the async runtime")?;

    match cli.command {
        Some(Command::Serve(args)) => {
            let (ctx, _forward_dir) = init_diagnostics(
                coda_diagnostics::ProcessRole::Serve,
                args.log_file.clone(),
                args.diagnostic_verbosity.as_deref(),
                &args.log_filter,
            )?;
            let result = runtime.block_on(coda_diagnostics::scope(ctx.clone(), run_serve(args)));
            ctx.record(coda_diagnostics::Event::ProcessEnd {
                exit_code: if result.is_ok() { 0 } else { 1 },
            });
            result
        }
        Some(Command::Run(args)) => {
            let (ctx, forward_dir) = init_diagnostics(
                coda_diagnostics::ProcessRole::Run,
                args.log_file.clone(),
                args.diagnostic_verbosity.as_deref(),
                &args.log_filter,
            )?;
            let result = runtime.block_on(coda_diagnostics::scope(
                ctx.clone(),
                run_headless(args, ctx.clone(), forward_dir),
            ));
            match result {
                Ok(code) => {
                    // Recorded explicitly: `process::exit` skips `Drop`, so
                    // this is the only chance to note the process ending.
                    ctx.record(coda_diagnostics::Event::ProcessEnd { exit_code: code });
                    std::process::exit(code);
                }
                Err(err) => {
                    ctx.record(coda_diagnostics::Event::StartupFailure { category: "headless_setup" });
                    ctx.record(coda_diagnostics::Event::ProcessEnd { exit_code: 1 });
                    Err(err)
                }
            }
        }
        None => {
            let (ctx, forward_dir) = init_diagnostics(
                coda_diagnostics::ProcessRole::Tui,
                cli.interactive.log_file.clone(),
                cli.interactive.diagnostic_verbosity.as_deref(),
                &cli.interactive.log_filter,
            )?;
            let result = runtime.block_on(coda_diagnostics::scope(
                ctx.clone(),
                run_interactive(cli.interactive, ctx.clone(), forward_dir),
            ));
            ctx.record(coda_diagnostics::Event::ProcessEnd {
                exit_code: if result.is_ok() { 0 } else { 1 },
            });
            result
        }
    }
}

/// Resolves the engine executable, defaulting to this binary so a standalone
/// `coda.exe` needs nothing else installed.
fn resolve_engine(explicit: Option<PathBuf>) -> Result<PathBuf> {
    match explicit {
        Some(path) => Ok(path),
        None => std::env::current_exe().context("failed to locate the running executable"),
    }
}

async fn run_serve(args: ServeArgs) -> Result<()> {
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
    let system_prompt = resolve_system_prompt(
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
    coda_serve::serve_stdio().await
}

async fn run_interactive(
    args: InteractiveArgs,
    diagnostics: coda_diagnostics::DiagnosticContext,
    engine_log_dir: PathBuf,
) -> Result<()> {
    // `--yolo-safe` is not yet implemented — reject explicitly rather than
    // accept-and-ignore, which would create a false sense of safety.
    if args.yolo_safe {
        anyhow::bail!(
            "--yolo-safe is not supported in this build; \
             use --permission-mode acceptEdits or --yolo instead"
        );
    }

    let working_dir = match &args.directory {
        Some(dir) => dir.clone(),
        None => std::env::current_dir().context("failed to read the current directory")?,
    };

    // Resolve the system prompt before touching the terminal.
    let system_prompt = resolve_system_prompt(
        args.system_prompt.as_deref(),
        args.system_prompt_file.as_deref(),
    )?;

    let engine = resolve_engine(args.engine.clone())?;
    let mut command = EngineCommand::new(engine.as_os_str())
        .arg("serve")
        .working_dir(&working_dir);
    command = forward_diagnostics_env(command, &diagnostics, &engine_log_dir);
    for arg in &args.engine_args {
        command = command.arg(OsString::from(arg));
    }
    // Forward an explicit `--provider` to the engine so it selects that account
    // at startup and fails closed when it is unavailable — never silently
    // connecting a different provider (Finding C2). Passed as a child-only env
    // var so the parent process environment is untouched.
    if let Some(ref provider) = args.provider {
        command = command.env("CODA_SERVE_PROVIDER", provider.as_str());
    }

    let theme = Theme::default().with_depth(ColorDepth::detect());

    // Resolved before the terminal is touched, so "no such session" prints as
    // an ordinary error rather than flashing up behind an alternate screen.
    let intent = SessionIntent::from_flags(
        args.continue_latest,
        args.resume.clone(),
        args.fork.clone(),
    );
    let resuming = coda_tui::startup::resolve(&intent, &working_dir).await?;

    // Connect before touching the terminal, so a failure prints a normal error
    // instead of a blank alternate screen.
    let (mut app, engine_process, inbound) =
        App::connect_to_session(command, theme, resuming.clone()).await?;

    // Helper: abort cleanly if any pre-launch RPC fails.
    macro_rules! apply_or_abort {
        ($fut:expr) => {{
            if let Err(err) = $fut {
                let _ = engine_process.shutdown(std::time::Duration::from_secs(5)).await;
                return Err(err);
            }
        }};
    }

    // All startup overrides run before the terminal opens so any rejection
    // surfaces as a normal error rather than appearing behind an alternate screen.
    //
    // Order matters (Finding C1): the model/provider (the active identity) is
    // established FIRST, and reasoning-effort is applied LAST. Effort is
    // recorded per-model, so applying it before a model switch would leave the
    // explicit level attached to the previous model and silently dropped.
    if let Some(ref model) = args.model {
        apply_or_abort!(app.apply_cli_model(model).await);
    } else if let Some(ref provider) = args.provider {
        // --provider without --model: look up the saved model for this provider.
        let provider_model = coda_serve::settings::resolve_for_provider(Some(provider.as_str())).model;
        apply_or_abort!(app.apply_cli_model(&provider_model).await);
    }
    // Verify the engine actually connected the requested provider. A custom
    // engine that ignored the request, or a native engine that fell back, is
    // caught here and fails the launch rather than talking to the wrong account.
    if let Some(ref provider) = args.provider {
        apply_or_abort!(app.verify_cli_provider(provider).await);
    }
    let perm_mode = if args.yolo {
        Some("bypassPermissions".to_owned())
    } else {
        args.permission_mode.clone()
    };
    if let Some(ref mode) = perm_mode {
        apply_or_abort!(app.apply_cli_permission_mode(mode).await);
    }
    if args.goal.is_some() || args.goal_timeout.is_some() || args.max_continuations.is_some() {
        apply_or_abort!(
            app.apply_cli_goal(
                args.goal.as_deref(),
                args.goal_timeout.as_deref(),
                args.max_continuations,
            )
            .await
        );
    }
    if let Some(ref prompt) = system_prompt {
        apply_or_abort!(app.apply_cli_system_prompt(prompt).await);
    }
    // Effort last, after the active model is settled (Finding C1).
    if let Some(ref level) = args.effort {
        apply_or_abort!(app.apply_cli_effort(level).await);
    }

    // The banner is seeded into the transcript rather than printed to the raw
    // console: printed before the alternate screen it would be wiped the
    // instant that screen is entered, so the user would never see it. The exit
    // summary still prints, because by then the screen has been released.
    app.push_banner(&working_dir.to_string_lossy());

    install_panic_hook();
    let started_at = std::time::Instant::now();
    let mut guard = TerminalGuard::enter(!args.no_mouse).context("failed to set up the terminal")?;

    let result = app.run(&mut guard, inbound, started_at).await;

    // Restore the terminal before shutting the engine down so any engine
    // diagnostics land on a normal screen.
    drop(guard);

    // The summary is written after the alternate screen is released, so it
    // survives in the scrollback. It reports what the session cost and how to
    // get back to it — both lost entirely if it is skipped.
    match &result {
        Ok(summary) => coda_tui::branding::print_exit(summary),
        // A failed run has no meaningful summary; the error is the message.
        Err(_) => {}
    }

    let _ = engine_process.shutdown(std::time::Duration::from_secs(5)).await;

    result.map(|_| ())
}

/// Runs a single task without a terminal UI and returns the process exit code.
///
/// Streams assistant text to stdout as it arrives so a long task shows
/// progress rather than appearing hung. With `--json` a single object is
/// emitted at the end instead, so the output stays machine-parseable.
async fn run_headless(
    args: RunArgs,
    diagnostics: coda_diagnostics::DiagnosticContext,
    engine_log_dir: PathBuf,
) -> Result<i32> {
    use coda_client::Inbound;
    use coda_proto::messages::{method, InitializeParams, PromptParams};

    let working_dir = match &args.cwd {
        Some(dir) => dir.clone(),
        None => std::env::current_dir().context("failed to read the current directory")?,
    };

    // Resolve system prompt before spawning the engine.
    let system_prompt = resolve_system_prompt(
        args.system_prompt.as_deref(),
        args.system_prompt_file.as_deref(),
    )?;

    // Resolve the session intent (resume/continue/fork).
    let intent = SessionIntent::from_flags(
        args.continue_latest,
        args.resume.clone(),
        args.fork.clone(),
    );
    let session_id = coda_tui::startup::resolve(&intent, &working_dir).await?;

    let engine = resolve_engine(args.engine.clone())?;
    let mut command = EngineCommand::new(engine.as_os_str())
        .arg("serve")
        .working_dir(&working_dir);
    command = forward_diagnostics_env(command, &diagnostics, &engine_log_dir);
    // Forward an explicit `--provider` so the engine selects that account at
    // startup and fails closed if unavailable (Finding C2).
    if let Some(ref provider) = args.provider {
        command = command.env("CODA_SERVE_PROVIDER", provider.as_str());
    }

    let (engine_process, mut inbound) =
        coda_client::Engine::spawn(command).context("failed to start the engine")?;
    let connection = engine_process.connection();

    let mut init = InitializeParams::new("coda-run");
    if let Some(ref sid) = session_id {
        init = init.resume(sid.clone());
    }
    let init_val = serde_json::to_value(init)
        .context("failed to serialise the handshake")?;
    let initialized: coda_proto::messages::InitializeResult = serde_json::from_value(connection
        .request(method::INITIALIZE, Some(init_val))
        .await
        .context("the engine handshake failed")?)?;
    let diagnostics = diagnostics.with_session(initialized.session_id.clone());
    coda_tui::diagnostics::record_engine_log_path(
        &diagnostics, initialized.telemetry_log_path.as_deref(),
    );

    // Helper: apply an RPC that must succeed before the prompt is sent.
    // A rejection or transport error shuts down the engine and returns an error.
    macro_rules! must_apply {
        ($method:expr, $params:expr, $flag:expr) => {{
            let response = connection
                .request($method, Some($params))
                .await
                .with_context(|| format!("failed to apply {}", $flag))?;
            let ok = response.get("ok").and_then(|b| b.as_bool()).unwrap_or(false);
            if !ok {
                let note = response
                    .get("note")
                    .or_else(|| response.get("error"))
                    .and_then(|n| n.as_str())
                    .unwrap_or("rejected");
                let _ = engine_process.shutdown(std::time::Duration::from_secs(5)).await;
                anyhow::bail!("{} was not applied: {note}", $flag);
            }
        }};
    }

    // Apply startup overrides with the active identity (model/provider) FIRST
    // and reasoning-effort LAST (Finding C1): effort is recorded per-model, so
    // applying it before a model switch would attach it to the wrong model and
    // silently drop it.
    if let Some(ref model) = args.model {
        must_apply!(
            method::SET_MODEL,
            serde_json::json!({ "model": model }),
            format!("--model {model}")
        );
    } else if let Some(ref provider) = args.provider {
        let provider_model =
            coda_serve::settings::resolve_for_provider(Some(provider.as_str())).model;
        must_apply!(
            method::SET_MODEL,
            serde_json::json!({ "model": provider_model }),
            format!("--provider {provider}")
        );
    }
    // Verify the engine connected the requested provider; fail closed on a
    // mismatch rather than running against a different account (Finding C2).
    if let Some(ref provider) = args.provider {
        let cap = connection
            .request(method::REASONING_CAPABILITY, None)
            .await
            .with_context(|| format!("failed to verify --provider {provider}"))?;
        let active = cap.get("providerId").and_then(|v| v.as_str()).unwrap_or("");
        let want = coda_serve::host::canonical_provider(provider);
        if !coda_serve::host::canonical_provider(active).eq_ignore_ascii_case(&want) {
            let _ = engine_process.shutdown(std::time::Duration::from_secs(5)).await;
            anyhow::bail!(
                "requested provider '{provider}' but the engine connected '{active}'; \
                 refusing to run against a different account"
            );
        }
    }
    let perm_mode = if args.yolo {
        Some("bypassPermissions".to_owned())
    } else {
        args.permission_mode.clone()
    };
    if let Some(ref mode) = perm_mode {
        must_apply!(
            method::SET_PERMISSION_MODE,
            serde_json::json!({ "mode": mode }),
            format!("--permission-mode {mode}")
        );
    }
    if args.goal.is_some() || args.goal_timeout.is_some() || args.max_continuations.is_some() {
        must_apply!(
            method::SET_GOAL,
            serde_json::json!({
                "goal": args.goal,
                "maxDuration": args.goal_timeout,
                "maxContinuations": args.max_continuations,
            }),
            "--goal"
        );
    }
    if let Some(ref prompt_text) = system_prompt {
        must_apply!(
            method::SET_SYSTEM_PROMPT,
            serde_json::json!({ "text": prompt_text }),
            "--system-prompt"
        );
    }
    // Effort last, after the active model is settled (Finding C1).
    if let Some(ref level) = args.effort {
        must_apply!(
            method::SET_EFFORT,
            serde_json::json!({ "effort": level }),
            format!("--effort {level}")
        );
    }

    let params = serde_json::to_value(PromptParams::text(&args.prompt))
        .context("failed to serialise the prompt")?;
    let mut pending = connection.send_request(method::PROMPT, Some(params))?;

    // Drain notifications until the turn ends, collecting assistant text.
    let mut reply = String::new();
    let mut failure: Option<String> = None;
    let mut response = None;
    loop {
        tokio::select! {
        biased;
        incoming = inbound.recv() => match incoming {
            Some(Inbound::Notification { method, params }) => {
                let event = coda_proto::events::Event::parse(&method, params.as_ref());
                match event {
                    coda_proto::events::Event::AssistantText { delta } => {
                        if !args.json {
                            print!("{delta}");
                            use std::io::Write;
                            let _ = std::io::stdout().flush();
                        }
                        reply.push_str(&delta);
                    }
                    coda_proto::events::Event::Error { message } => {
                        failure = Some(message);
                    }
                    e if e.ends_turn() => break,
                    _ => {}
                }
            }
            // A server-initiated request cannot be answered without a user, so
            // let the responder drop: the engine's fail-closed default applies
            // and a permission prompt denies rather than silently allowing.
            Some(Inbound::Request { .. }) => {}
            None => break,
        },
        received = &mut pending => {
            // A preflight RPC rejection emits no turn-complete notification.
            response = Some(received);
            break;
        }
        }
    }

    let received = match response {
        Some(response) => response,
        None => pending.await,
    };
    let result = match received {
        Ok(Ok(value)) => Some(value),
        Ok(Err(error)) => {
            failure.get_or_insert(error.message);
            None
        }
        Err(_) => {
            failure.get_or_insert("engine disconnected before returning the turn result".into());
            None
        }
    };
    // `shutdown` closes the child's stdin by dropping the engine's *own*
    // internal connection/sender; this clone must go first, or the writer
    // task (and the child's stdin) stays alive and the engine sits blocked
    // reading for the whole grace period before being force-killed.
    drop(connection);
    let _ = engine_process.shutdown(std::time::Duration::from_secs(5)).await;

    let ok = result
        .as_ref()
        .and_then(|v| v.get("ok").and_then(|b| b.as_bool()))
        .unwrap_or(false);
    let error = failure.or_else(|| {
        result
            .as_ref()
            .and_then(|v| v.get("error").and_then(|e| e.as_str().map(str::to_owned)))
    });

    if args.json {
        let payload = serde_json::json!({
            "ok": ok && error.is_none(),
            "reply": reply,
            "error": error,
        });
        println!("{}", serde_json::to_string(&payload).unwrap_or_default());
    } else {
        if !reply.is_empty() {
            println!();
        }
        if let Some(message) = &error {
            eprintln!("error: {message}");
        }
    }

    Ok(if ok && error.is_none() { 0 } else { 1 })
}

#[cfg(test)]
mod tests {
    use super::*;
    use clap::CommandFactory;

    #[test]
    fn the_cli_definition_is_valid() {
        Cli::command().debug_assert();
    }

    #[test]
    fn no_arguments_selects_interactive_mode() {
        let cli = Cli::try_parse_from(["coda"]).expect("parse");
        assert!(cli.command.is_none());
    }

    #[test]
    fn serve_is_a_subcommand() {
        let cli = Cli::try_parse_from(["coda", "serve"]).expect("parse");
        assert!(matches!(cli.command, Some(Command::Serve(_))));
    }

    #[test]
    fn run_requires_a_prompt() {
        assert!(Cli::try_parse_from(["coda", "run"]).is_err());
        let cli = Cli::try_parse_from(["coda", "run", "-p", "do a thing"]).expect("parse");
        match cli.command {
            Some(Command::Run(args)) => assert_eq!(args.prompt, "do a thing"),
            other => panic!("expected run, got {other:?}"),
        }
    }

    /// A task beginning with `-` must not be mistaken for a flag.
    #[test]
    fn a_prompt_may_begin_with_a_dash() {
        let cli = Cli::try_parse_from(["coda", "run", "-p", "--explain this"]).expect("parse");
        match cli.command {
            Some(Command::Run(args)) => assert_eq!(args.prompt, "--explain this"),
            other => panic!("expected run, got {other:?}"),
        }
    }

    /// A syntactically invalid `--effort` is rejected by the parser, before any
    /// engine is spawned or terminal entered (Finding 5).
    #[test]
    fn run_rejects_an_invalid_effort_level() {
        assert!(
            Cli::try_parse_from(["coda", "run", "-p", "x", "--effort", "ludicrous"]).is_err(),
            "an unknown effort level must fail at parse time"
        );
    }

    #[test]
    fn run_accepts_and_lowercases_a_valid_effort_level() {
        let cli =
            Cli::try_parse_from(["coda", "run", "-p", "x", "--effort", "XHIGH"]).expect("parse");
        match cli.command {
            Some(Command::Run(args)) => assert_eq!(args.effort.as_deref(), Some("xhigh")),
            other => panic!("expected run, got {other:?}"),
        }
    }

    #[test]
    fn serve_accepts_an_effort_level() {
        let cli = Cli::try_parse_from(["coda", "serve", "--effort", "high"]).expect("parse");
        match cli.command {
            Some(Command::Serve(args)) => assert_eq!(args.effort.as_deref(), Some("high")),
            other => panic!("expected serve, got {other:?}"),
        }
    }

    #[test]
    fn interactive_rejects_an_invalid_effort_level() {
        assert!(
            Cli::try_parse_from(["coda", "--effort", "ludicrous"]).is_err(),
            "an unknown effort level must fail at parse time for interactive mode too"
        );
    }

    #[test]
    fn effort_level_parser_accepts_the_documented_set() {
        for level in ["low", "medium", "high", "xhigh", "max", "auto"] {
            assert_eq!(parse_effort_level(level).as_deref(), Ok(level));
        }
        assert!(parse_effort_level("nonsense").is_err());
    }

    /// Without `--engine`, the engine is this executable, so a standalone
    /// binary needs nothing else on the system.
    #[test]
    fn the_engine_defaults_to_the_running_executable() {
        let resolved = resolve_engine(None).expect("resolve");
        assert_eq!(resolved, std::env::current_exe().expect("current exe"));
    }

    #[test]
    fn an_explicit_engine_overrides_the_default() {
        let resolved = resolve_engine(Some(PathBuf::from("other.exe"))).expect("resolve");
        assert_eq!(resolved, PathBuf::from("other.exe"));
    }

#[cfg(test)]
mod tests {
    use super::*;
    use clap::CommandFactory;

    #[test]
    fn cli_definition_is_valid() {
        Cli::command().debug_assert();
    }

    fn interactive(args: &[&str]) -> InteractiveArgs {
        let mut argv = vec!["coda"];
        argv.extend_from_slice(args);
        Cli::try_parse_from(argv).expect("parse").interactive
    }

    #[test]
    fn resume_accepts_the_form_the_exit_summary_prints() {
        // The exit summary ends with `coda --resume <id>`. Rejecting that was
        // the app advertising a command it did not have.
        let args = interactive(&["--resume", "90260319-1f67-4c7c-9b41-2f6ff38d96f1"]);
        assert_eq!(
            SessionIntent::from_flags(args.continue_latest, args.resume, args.fork),
            SessionIntent::Resume("90260319-1f67-4c7c-9b41-2f6ff38d96f1".into())
        );
    }

    #[test]
    fn a_flag_after_the_id_is_not_swallowed_as_the_id() {
        // `--resume <id> --yolo` is what a user actually types. If the id were
        // greedy the trailing flag would vanish into it.
        let args = interactive(&["--resume", "abc123", "--no-mouse"]);
        assert!(args.no_mouse, "the trailing flag was swallowed");
        assert_eq!(
            SessionIntent::from_flags(args.continue_latest, args.resume, args.fork),
            SessionIntent::Resume("abc123".into())
        );
    }

    #[test]
    fn the_short_forms_match_the_long_ones() {
        for (short, long) in [("-c", "--continue"), ("-r", "--resume"), ("-f", "--fork")] {
            let a = interactive(&[short]);
            let b = interactive(&[long]);
            assert_eq!(
                SessionIntent::from_flags(a.continue_latest, a.resume, a.fork),
                SessionIntent::from_flags(b.continue_latest, b.resume, b.fork),
                "{short} and {long} disagree"
            );
        }
    }

    #[test]
    fn bare_resume_and_continue_both_mean_the_most_recent() {
        let resume = interactive(&["--resume"]);
        assert_eq!(
            SessionIntent::from_flags(resume.continue_latest, resume.resume, resume.fork),
            SessionIntent::Latest
        );
    }

    #[test]
    fn the_session_flags_refuse_to_be_combined() {
        // Two intents is a mistake with no sensible reading, and picking one
        // silently is how a resume ends up starting an empty session.
        for pair in [
            ["--continue", "--resume"],
            ["--continue", "--fork"],
            ["--resume", "--fork"],
        ] {
            let mut argv = vec!["coda"];
            argv.extend_from_slice(&pair);
            assert!(
                Cli::try_parse_from(argv).is_err(),
                "{pair:?} were accepted together"
            );
        }
    }

    #[test]
    fn no_session_flag_starts_fresh() {
        let args = interactive(&[]);
        assert_eq!(
            SessionIntent::from_flags(args.continue_latest, args.resume, args.fork),
            SessionIntent::New
        );
    }

    // ─── New parity flag tests ──────────────────────────────────────────────

    fn run(args: &[&str]) -> RunArgs {
        let mut argv = vec!["coda", "run", "-p", "x"];
        argv.extend_from_slice(args);
        match Cli::try_parse_from(argv).expect("parse").command {
            Some(Command::Run(a)) => a,
            other => panic!("expected run, got {other:?}"),
        }
    }

    fn serve(args: &[&str]) -> ServeArgs {
        let mut argv = vec!["coda", "serve"];
        argv.extend_from_slice(args);
        match Cli::try_parse_from(argv).expect("parse").command {
            Some(Command::Serve(a)) => a,
            other => panic!("expected serve, got {other:?}"),
        }
    }

    #[test]
    fn run_accepts_model_flag() {
        let a = run(&["--model", "claude-opus-5"]);
        assert_eq!(a.model.as_deref(), Some("claude-opus-5"));
    }

    #[test]
    fn run_accepts_provider_flag() {
        let a = run(&["--provider", "anthropic"]);
        assert_eq!(a.provider.as_deref(), Some("anthropic"));
    }

    #[test]
    fn run_accepts_permission_mode_flag() {
        let a = run(&["--permission-mode", "acceptEdits"]);
        assert_eq!(a.permission_mode.as_deref(), Some("acceptEdits"));
    }

    #[test]
    fn run_yolo_sets_bypass_shorthand() {
        let a = run(&["--yolo"]);
        assert!(a.yolo);
        assert!(!a.permission_mode.is_some());
    }

    #[test]
    fn run_yolo_and_permission_mode_are_mutually_exclusive() {
        assert!(
            Cli::try_parse_from(["coda", "run", "-p", "x", "--yolo", "--permission-mode", "plan"])
                .is_err(),
            "--yolo and --permission-mode must conflict"
        );
    }

    #[test]
    fn run_accepts_goal_flag() {
        let a = run(&["--goal", "complete the task"]);
        assert_eq!(a.goal.as_deref(), Some("complete the task"));
    }

    #[test]
    fn run_goal_timeout_requires_goal() {
        assert!(
            Cli::try_parse_from(["coda", "run", "-p", "x", "--goal-timeout", "30m"]).is_err(),
            "--goal-timeout without --goal must fail"
        );
    }

    #[test]
    fn run_accepts_goal_with_timeout_and_continuations() {
        let a = run(&["--goal", "ship it", "--goal-timeout", "2h", "--max-continuations", "5"]);
        assert_eq!(a.goal.as_deref(), Some("ship it"));
        assert_eq!(a.goal_timeout.as_deref(), Some("2h"));
        assert_eq!(a.max_continuations, Some(5));
    }

    #[test]
    fn run_goal_max_duration_alias_works() {
        let a = run(&["--goal", "x", "--goal-max-duration", "30m"]);
        assert_eq!(a.goal_timeout.as_deref(), Some("30m"));
    }

    #[test]
    fn run_goal_max_continuations_alias_works() {
        let a = run(&["--goal", "x", "--goal-max-continuations", "3"]);
        assert_eq!(a.max_continuations, Some(3));
    }

    #[test]
    fn run_accepts_system_prompt_flag() {
        let a = run(&["--system-prompt", "Be terse."]);
        assert_eq!(a.system_prompt.as_deref(), Some("Be terse."));
    }

    #[test]
    fn run_system_prompt_and_file_are_mutually_exclusive() {
        assert!(
            Cli::try_parse_from([
                "coda", "run", "-p", "x",
                "--system-prompt", "text",
                "--system-prompt-file", "file.txt"
            ])
            .is_err(),
            "--system-prompt and --system-prompt-file must conflict"
        );
    }

    #[test]
    fn run_accepts_resume_continue_fork() {
        let a = run(&["--resume", "abc123"]);
        assert_eq!(
            SessionIntent::from_flags(a.continue_latest, a.resume, a.fork),
            SessionIntent::Resume("abc123".into())
        );
        let a = run(&["--continue"]);
        assert_eq!(
            SessionIntent::from_flags(a.continue_latest, a.resume, a.fork),
            SessionIntent::Latest
        );
        let a = run(&["--fork"]);
        assert_eq!(
            SessionIntent::from_flags(a.continue_latest, a.resume, a.fork),
            SessionIntent::Fork(None)
        );
    }

    #[test]
    fn serve_accepts_model_flag() {
        let a = serve(&["--model", "gpt-5"]);
        assert_eq!(a.model.as_deref(), Some("gpt-5"));
    }

    #[test]
    fn serve_yolo_flag_is_accepted() {
        let a = serve(&["--yolo"]);
        assert!(a.yolo);
    }

    #[test]
    fn serve_api_key_and_endpoint_accepted() {
        let a = serve(&["--api-key", "sk-test", "--endpoint", "https://proxy.example.com"]);
        assert_eq!(a.api_key.as_deref(), Some("sk-test"));
        assert_eq!(a.endpoint.as_deref(), Some("https://proxy.example.com"));
    }

    #[test]
    fn serve_endpoint_requires_api_key() {
        assert!(
            Cli::try_parse_from(["coda", "serve", "--endpoint", "https://x.com"]).is_err(),
            "--endpoint without --api-key must fail"
        );
    }

    #[test]
    fn interactive_accepts_model_and_system_prompt() {
        let a = interactive(&["--model", "gpt-5", "--system-prompt", "Be helpful."]);
        assert_eq!(a.model.as_deref(), Some("gpt-5"));
        assert_eq!(a.system_prompt.as_deref(), Some("Be helpful."));
    }

    #[test]
    fn resolve_system_prompt_inline_wins() {
        let result = resolve_system_prompt(Some("inline text"), None).expect("resolve");
        assert_eq!(result.as_deref(), Some("inline text"));
    }

    #[test]
    fn resolve_system_prompt_none_when_neither_given() {
        let result = resolve_system_prompt(None, None).expect("resolve");
        assert!(result.is_none());
    }

    #[test]
    fn resolve_system_prompt_from_file() {
        let dir = std::env::temp_dir();
        let path = dir.join(format!("coda-test-sp-{}.txt", std::process::id()));
        std::fs::write(&path, "from file").expect("write");
        let result = resolve_system_prompt(None, Some(&path)).expect("resolve");
        assert_eq!(result.as_deref(), Some("from file"));
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn resolve_system_prompt_file_not_found_is_error() {
        let result = resolve_system_prompt(None, Some(std::path::Path::new("no-such-file.txt")));
        assert!(result.is_err(), "missing file must be an error");
    }
}}
