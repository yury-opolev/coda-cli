//! Entry point for the Coda terminal UI.

use std::ffi::OsString;

use anyhow::{Context, Result};
use clap::Parser;
use coda_client::EngineCommand;
use coda_render::theme::{ColorDepth, Theme};
use coda_tui::app::App;
use coda_tui::cli::Cli;
use coda_tui::terminal::{install_panic_hook, TerminalGuard};

fn main() -> Result<()> {
    let cli = Cli::parse();
    let _logging = init_logging(&cli)?;

    let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .context("failed to start the async runtime")?;

    runtime.block_on(run(cli))
}

async fn run(cli: Cli) -> Result<()> {
    let working_dir = cli.resolved_directory()?;

    let mut command = EngineCommand::new(cli.engine.as_os_str())
        .arg("serve")
        .working_dir(&working_dir);
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

/// Sets up logging. Returns a guard that must be held for the process lifetime.
fn init_logging(cli: &Cli) -> Result<Option<tracing_appender::non_blocking::WorkerGuard>> {
    use tracing_subscriber::EnvFilter;

    let Some(path) = &cli.log_file else {
        return Ok(None);
    };

    let directory = path.parent().filter(|p| !p.as_os_str().is_empty());
    if let Some(directory) = directory {
        std::fs::create_dir_all(directory)
            .with_context(|| format!("failed to create the log directory {}", directory.display()))?;
    }

    let file = std::fs::File::create(path)
        .with_context(|| format!("failed to create the log file {}", path.display()))?;
    let (writer, guard) = tracing_appender::non_blocking(file);

    tracing_subscriber::fmt()
        .with_env_filter(EnvFilter::new(&cli.log_filter))
        .with_writer(writer)
        .with_ansi(false)
        .init();

    Ok(Some(guard))
}
