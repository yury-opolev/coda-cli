//! Entry point for the Coda terminal UI.

use std::ffi::OsString;

use anyhow::{Context, Result};
use clap::Parser;
use coda_client::EngineCommand;
use coda_diagnostics::ProcessRole;
use coda_render::theme::{ColorDepth, Theme};
use coda_tui::app::App;
use coda_tui::cli::Cli;
use coda_tui::terminal::{install_panic_hook, TerminalGuard};

fn main() -> Result<()> {
    let cli = Cli::parse();
    let (ctx, forward_dir) = coda_tui::diagnostics::init(
        ProcessRole::Tui,
        cli.log_file.clone(),
        cli.diagnostic_verbosity.as_deref(),
        &cli.log_filter,
    )?;

    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .context("failed to start the async runtime")?;

    let result = runtime.block_on(coda_diagnostics::scope(ctx.clone(), run(cli, ctx.clone(), forward_dir)));
    ctx.record(coda_diagnostics::Event::ProcessEnd {
        exit_code: if result.is_ok() { 0 } else { 1 },
    });
    result
}

async fn run(cli: Cli, diagnostics: coda_diagnostics::DiagnosticContext, engine_log_dir: std::path::PathBuf) -> Result<()> {
    let working_dir = cli.resolved_directory()?;

    let mut command = EngineCommand::new(cli.engine.as_os_str())
        .arg("serve")
        .working_dir(&working_dir);
    command = coda_tui::diagnostics::forward_env(command, &diagnostics, &engine_log_dir);
    for arg in &cli.engine_args {
        command = command.arg(OsString::from(arg));
    }

    let theme = Theme::default().with_depth(ColorDepth::detect());

    // Connect before touching the terminal, so a failure prints a normal error
    // instead of a blank alternate screen.
    let (app, engine, inbound) = App::connect(command, theme).await?;

    install_panic_hook();
    let started_at = std::time::Instant::now();
    let mut guard = TerminalGuard::enter(!cli.no_mouse).context("failed to set up the terminal")?;

    let result = app.run(&mut guard, inbound, started_at).await;

    // Restore the terminal before shutting the engine down so any engine
    // diagnostics land on a normal screen.
    drop(guard);
    if let Ok(summary) = &result {
        coda_tui::branding::print_exit(summary);
    }
    let _ = engine.shutdown(std::time::Duration::from_secs(5)).await;

    result.map(|_| ())
}
