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
    #[arg(long, short = 'C', value_name = "DIR")]
    directory: Option<PathBuf>,

    /// Write a debug log to this file.
    #[arg(long, value_name = "FILE")]
    log_file: Option<PathBuf>,

    /// Log filter, e.g. `debug` or `coda_client=trace`.
    #[arg(long, env = "CODA_LOG", default_value = "warn")]
    log_filter: String,

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
    #[arg(long, value_name = "DIR")]
    cwd: Option<PathBuf>,

    /// Executable used to launch the engine. Defaults to this binary.
    #[arg(long, env = "CODA_ENGINE")]
    engine: Option<PathBuf>,
}

fn main() -> Result<()> {
    let cli = Cli::parse();

    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .context("failed to start the async runtime")?;

    match cli.command {
        Some(Command::Serve(args)) => runtime.block_on(run_serve(args)),
        Some(Command::Run(args)) => {
            let code = runtime.block_on(run_headless(args))?;
            std::process::exit(code);
        }
        None => {
            let _logging = init_logging(&cli.interactive)?;
            runtime.block_on(run_interactive(cli.interactive))
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
    coda_serve::serve_stdio().await
}

async fn run_interactive(args: InteractiveArgs) -> Result<()> {
    let working_dir = match &args.directory {
        Some(dir) => dir.clone(),
        None => std::env::current_dir().context("failed to read the current directory")?,
    };

    let engine = resolve_engine(args.engine.clone())?;
    let mut command = EngineCommand::new(engine.as_os_str())
        .arg("serve")
        .working_dir(&working_dir);
    for arg in &args.engine_args {
        command = command.arg(OsString::from(arg));
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
async fn run_headless(args: RunArgs) -> Result<i32> {
    use coda_client::Inbound;
    use coda_proto::messages::{method, InitializeParams, PromptParams};

    let working_dir = match &args.cwd {
        Some(dir) => dir.clone(),
        None => std::env::current_dir().context("failed to read the current directory")?,
    };

    let engine = resolve_engine(args.engine.clone())?;
    let command = EngineCommand::new(engine.as_os_str())
        .arg("serve")
        .working_dir(&working_dir);

    let (engine_process, mut inbound) =
        coda_client::Engine::spawn(command).context("failed to start the engine")?;
    let connection = engine_process.connection();

    let init = serde_json::to_value(InitializeParams::new("coda-run"))
        .context("failed to serialise the handshake")?;
    connection
        .request(method::INITIALIZE, Some(init))
        .await
        .context("the engine handshake failed")?;

    let params = serde_json::to_value(PromptParams::text(&args.prompt))
        .context("failed to serialise the prompt")?;
    let pending = connection.send_request(method::PROMPT, Some(params))?;

    // Drain notifications until the turn ends, collecting assistant text.
    let mut reply = String::new();
    let mut failure: Option<String> = None;
    loop {
        match inbound.recv().await {
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
        }
    }

    let result = pending.await.ok().and_then(|r| r.ok());
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

/// Sets up logging. Returns a guard that must be held for the process lifetime.
fn init_logging(
    args: &InteractiveArgs,
) -> Result<Option<tracing_appender::non_blocking::WorkerGuard>> {
    use tracing_subscriber::EnvFilter;

    let Some(path) = &args.log_file else {
        return Ok(None);
    };

    let directory = path.parent().filter(|p| !p.as_os_str().is_empty());
    if let Some(directory) = directory {
        std::fs::create_dir_all(directory).with_context(|| {
            format!("failed to create the log directory {}", directory.display())
        })?;
    }

    let file = std::fs::File::create(path)
        .with_context(|| format!("failed to create the log file {}", path.display()))?;
    let (writer, guard) = tracing_appender::non_blocking(file);

    tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::new(&args.log_filter))
        .with_writer(writer)
        .with_ansi(false)
        .init();

    Ok(Some(guard))
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
}}
