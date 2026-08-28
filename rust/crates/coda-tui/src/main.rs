use clap::Parser;
use coda_tui::cli::Cli;

fn main() -> anyhow::Result<()> {
    let cli = Cli::parse();
    println!("coda-tui: engine={:?} cwd={:?}", cli.engine, cli.resolved_directory()?);
    Ok(())
}
