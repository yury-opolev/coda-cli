//! Command-line surface for the Rust front-end.
//!
//! The engine is launched as `coda serve`, so anything that changes how we
//! connect to it is exposed here.

use std::path::PathBuf;

use clap::Parser;

#[derive(Debug, Clone, Parser)]
#[command(
    name = "coda-tui",
    about = "Coda terminal user interface",
    version,
    disable_help_subcommand = true
)]
pub struct Cli {
    /// Executable used to launch the engine.
    #[arg(long, env = "CODA_ENGINE", default_value = "coda")]
    pub engine: PathBuf,

    /// Extra arguments passed to the engine after `serve`.
    #[arg(long = "engine-arg", value_name = "ARG", allow_hyphen_values = true)]
    pub engine_args: Vec<String>,

    /// Working directory for the session. Defaults to the current directory.
    #[arg(long, short = 'C', value_name = "DIR")]
    pub directory: Option<PathBuf>,

    /// Send a single prompt, print the reply, and exit.
    #[arg(long, short = 'p', value_name = "TEXT")]
    pub prompt: Option<String>,

    /// Write a debug log to this file.
    #[arg(long, value_name = "FILE")]
    pub log_file: Option<PathBuf>,

    /// Log filter, e.g. `debug` or `coda_client=trace`.
    #[arg(long, env = "CODA_LOG", default_value = "warn")]
    pub log_filter: String,

    /// Disable mouse capture, which some terminals handle poorly.
    #[arg(long)]
    pub no_mouse: bool,
}

impl Cli {
    /// The session working directory, resolved against the process CWD.
    pub fn resolved_directory(&self) -> std::io::Result<PathBuf> {
        match &self.directory {
            Some(dir) => Ok(std::path::absolute(dir)?),
            None => std::env::current_dir(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clap::CommandFactory;

    #[test]
    fn cli_definition_is_valid() {
        Cli::command().debug_assert();
    }

    #[test]
    fn defaults_to_the_coda_engine() {
        let cli = Cli::try_parse_from(["coda-tui"]).expect("parse");
        assert_eq!(cli.engine, PathBuf::from("coda"));
        assert!(!cli.no_mouse);
        assert!(cli.prompt.is_none());
    }

    #[test]
    fn collects_repeated_engine_arguments() {
        let cli = Cli::try_parse_from([
            "coda-tui",
            "--engine-arg",
            "--api-key",
            "--engine-arg",
            "secret",
        ])
        .expect("parse");
        assert_eq!(cli.engine_args, vec!["--api-key", "secret"]);
    }

    #[test]
    fn accepts_a_one_shot_prompt() {
        let cli = Cli::try_parse_from(["coda-tui", "-p", "hello"]).expect("parse");
        assert_eq!(cli.prompt.as_deref(), Some("hello"));
    }

    #[test]
    fn resolves_a_relative_directory_to_an_absolute_path() {
        let cli = Cli::try_parse_from(["coda-tui", "-C", "."]).expect("parse");
        let resolved = cli.resolved_directory().expect("resolve");
        assert!(resolved.is_absolute());
    }
}
