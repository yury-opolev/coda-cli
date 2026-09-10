//! `coda-engine`: Coda's core engine as a standalone, TUI-free binary.
//!
//! ```text
//! coda-engine serve [FLAGS]   JSON-RPC engine over stdio (identical to `coda serve`)
//! coda-engine [FLAGS]         same thing — bare invocation defaults to `serve`
//! ```
//!
//! This binary exists to make one claim checkable rather than asserted:
//! `cargo tree -p coda-engine -e normal` never resolves `coda-tui`,
//! `coda-render`, `ratatui`, `crossterm`, `arboard`, or `png`
//! (`tests/independence.rs` enforces it). It is not a second supported UX —
//! `coda`'s own `coda serve` remains the primary, identical way to run the
//! same engine, and both share every byte of argument parsing and startup
//! translation through `coda-boot`'s `ServeArgs`/`prepare`. `--engine`/
//! `CODA_ENGINE` on `coda`/`coda-tui` can point at this binary instead of the
//! unified one and nothing about the JSON-RPC contract changes: they spawn
//! `<engine> serve ...`, which is the explicit subcommand form below.
//!
//! This binary does not daemonize, detach, or supervise anything — same as
//! `coda serve`, an external orchestrator/worker/bridge owns the persistent
//! process this becomes.

use anyhow::{Context, Result};
use clap::{Parser, Subcommand};
use coda_boot::ServeArgs;

#[derive(Debug, Parser)]
#[command(
    name = "coda-engine",
    about = "Coda's core engine — JSON-RPC over stdio, no TUI",
    version = coda_boot::version(),
    disable_help_subcommand = true,
    args_conflicts_with_subcommands = true
)]
struct Cli {
    #[command(subcommand)]
    command: Option<Command>,

    /// A bare invocation (no subcommand) is `serve` with these flags — the
    /// same `ServeArgs` `coda serve` accepts, so `--engine coda-engine.exe`
    /// and a directly-run `coda-engine <flags>` behave identically to
    /// `coda-engine serve <flags>`.
    #[command(flatten)]
    serve: ServeArgs,
}

#[derive(Debug, Subcommand)]
enum Command {
    /// Run the engine as a JSON-RPC server over stdio (the default).
    Serve(ServeArgs),
    /// Connect, disconnect, or report this machine's provider sign-in.
    ///
    /// Host-local maintenance of *this* profile's credential store: it is
    /// deliberately not a serve-API method, because an application that
    /// supervises Coda owns the engine's lifecycle, not its keychain. Shared
    /// byte for byte with `coda auth` through `coda-boot`.
    Auth(coda_boot::auth_args::AuthArgs),
}

fn main() -> Result<()> {
    let cli = Cli::parse();
    let args = match cli.command {
        // Runs before diagnostics are opened, and exits without returning: an
        // authorization URL or a device code must never reach a log file.
        Some(Command::Auth(auth)) => std::process::exit(coda_boot::auth_cli::run(auth)),
        Some(Command::Serve(args)) => args,
        None => cli.serve,
    };

    let (ctx, _forward_dir) = coda_boot::diagnostics::init(
        coda_diagnostics::ProcessRole::Serve,
        args.log_file.clone(),
        args.diagnostic_verbosity.as_deref(),
        &args.log_filter,
    )?;

    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .context("failed to start the async runtime")?;

    let result = runtime.block_on(coda_diagnostics::scope(ctx.clone(), run(args)));
    ctx.record(coda_diagnostics::Event::ProcessEnd {
        exit_code: if result.is_ok() { 0 } else { 1 },
    });
    result
}

async fn run(args: ServeArgs) -> Result<()> {
    coda_boot::serve::prepare(&args)?;
    coda_serve::serve_stdio().await
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
    fn no_arguments_means_serve_with_defaults() {
        let cli = Cli::try_parse_from(["coda-engine"]).expect("parse");
        assert!(cli.command.is_none());
        assert!(cli.serve.effort.is_none());
    }

    /// The whole point of flattening `ServeArgs` at the top level: a bare
    /// invocation must accept the same flags `serve` does, not just an empty
    /// argument list — this is what `--engine coda-engine.exe` plus
    /// `--engine-arg`s relies on when a caller does not add `serve` itself.
    #[test]
    fn bare_invocation_accepts_serve_flags_directly() {
        let cli = Cli::try_parse_from(["coda-engine", "--no-mcp", "--effort", "high"]).expect("parse");
        assert!(cli.command.is_none());
        assert!(cli.serve.no_mcp);
        assert_eq!(cli.serve.effort.as_deref(), Some("high"));
    }

    #[test]
    fn serve_is_an_explicit_subcommand() {
        let cli = Cli::try_parse_from(["coda-engine", "serve"]).expect("parse");
        assert!(matches!(cli.command, Some(Command::Serve(_))));
    }

    #[test]
    fn serve_accepts_the_same_flags_coda_serve_does() {
        let cli = Cli::try_parse_from([
            "coda-engine",
            "serve",
            "--effort",
            "high",
            "--no-mcp",
            "--yolo",
        ])
        .expect("parse");
        match cli.command {
            Some(Command::Serve(args)) => {
                assert_eq!(args.effort.as_deref(), Some("high"));
                assert!(args.no_mcp);
                assert!(args.yolo);
            }
            other => panic!("expected serve, got {other:?}"),
        }
    }

    #[test]
    fn an_invalid_effort_level_is_rejected_at_parse_time() {
        assert!(Cli::try_parse_from(["coda-engine", "serve", "--effort", "ludicrous"]).is_err());
    }

    #[test]
    fn a_bare_invalid_effort_level_is_also_rejected_at_parse_time() {
        assert!(Cli::try_parse_from(["coda-engine", "--effort", "ludicrous"]).is_err());
    }
}
