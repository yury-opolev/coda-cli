//! Entry point for the Coda terminal UI.

use anyhow::{Context, Result};
use clap::Parser;
use coda_diagnostics::ProcessRole;
use coda_render::theme::{ColorDepth, Theme};
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
    let theme = Theme::default().with_depth(ColorDepth::detect());

    // Before the engine, not after it. A launch whose selected provider has no
    // credential would otherwise exit on the engine's own startup error, with
    // the one screen that could fix it unreachable. An API-only launch returns
    // from here having opened no credential store at all.
    let command = coda_tui::startup::engine_command(&cli, |command| {
        coda_tui::diagnostics::forward_env(command, &diagnostics, &engine_log_dir)
    })?;
    let intent = coda_tui::preflight::LaunchIntent::from_launch(
        cli.access_mode(),
        None,
        &cli.engine_args,
        |key| std::env::var(key).ok(),
    );
    let command = match coda_tui::preflight::prepare_launch(
        command,
        &intent,
        &coda_tui::local::auth::AuthPort::profile(),
        &theme,
        !cli.no_mouse,
    )
    .await
    {
        coda_tui::preflight::Launch::Proceed { command, notes, .. } => {
            for note in notes {
                eprintln!("{note}");
            }
            command
        }
        // Cancelled, or impossible here: no engine is started and nothing was
        // changed.
        coda_tui::preflight::Launch::Abandoned(message) => {
            println!("{message}");
            return Ok(());
        }
    };

    // Connect before touching the terminal, so a failure prints a normal error
    // instead of a blank alternate screen. The launch — including the
    // local-maintenance contract it implies — is assembled by the library, so
    // this binary cannot disagree with the one `coda` runs.
    let (app, engine, inbound) =
        coda_tui::startup::connect(command, theme, cli.access_mode()).await?;

    install_panic_hook();
    let started_at = std::time::Instant::now();
    let mut guard = TerminalGuard::enter(!cli.no_mouse).context("failed to set up the terminal")?;

    let result = app.run(&mut guard, inbound, engine, started_at).await;

    // Restore the terminal before shutting the engine down so any engine
    // diagnostics land on a normal screen. The engine itself is the loop's:
    // an authentication transition has to stop and await it before writing a
    // credential, which a caller holding it could only be told about after
    // the fact.
    drop(guard);
    if let Ok(summary) = &result {
        coda_tui::branding::print_exit(summary);
    }

    result.map(|_| ())
}
