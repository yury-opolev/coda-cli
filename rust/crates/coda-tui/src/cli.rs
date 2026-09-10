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
    /// Executable used to launch the engine. Defaults to `coda`.
    ///
    /// Deliberately an `Option` rather than a defaulted value: "the user
    /// pointed this front-end at an engine of their own" is a different
    /// situation from "we launched the engine we ship with", and it decides
    /// whether this client may maintain local files the engine reads. A
    /// `default_value` would erase that distinction at parse time.
    #[arg(long, env = "CODA_ENGINE")]
    pub engine: Option<PathBuf>,

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

    /// Legacy compatibility hint: a `tracing` `EnvFilter` string. Only its
    /// loudest named level is used, as a fallback default for
    /// `--diagnostic-verbosity`.
    #[arg(long, env = "CODA_LOG", default_value = "warn")]
    pub log_filter: String,

    /// Diagnostic detail level for the essential operational log: normal,
    /// debug, or trace. Takes precedence over `--log-filter`/`CODA_LOG`.
    #[arg(long, value_name = "LEVEL", value_parser = parse_diagnostic_verbosity)]
    pub diagnostic_verbosity: Option<String>,

    /// Disable mouse capture, which some terminals handle poorly.
    #[arg(long)]
    pub no_mouse: bool,
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

impl Cli {
    /// The session working directory, resolved against the process CWD.
    pub fn resolved_directory(&self) -> std::io::Result<PathBuf> {
        match &self.directory {
            Some(dir) => Ok(std::path::absolute(dir)?),
            None => std::env::current_dir(),
        }
    }

    /// The engine binary to launch: the user's own when they named one, and
    /// the shipped `coda` otherwise.
    pub fn engine_program(&self) -> PathBuf {
        self.engine.clone().unwrap_or_else(|| PathBuf::from("coda"))
    }

    /// The local-maintenance contract this launch runs under.
    ///
    /// An explicit `--engine`/`CODA_ENGINE` is somebody else's binary or a
    /// proxy: its files are not necessarily these files, so this client
    /// refuses local maintenance rather than editing a machine the engine
    /// will never read. The default — launching the engine we ship — keeps
    /// every editor working exactly as before.
    pub fn access_mode(&self) -> crate::local::AccessMode {
        crate::local::AccessMode::for_launch(self.engine.is_some())
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
        assert_eq!(cli.engine_program(), PathBuf::from("coda"));
        assert!(!cli.no_mouse);
        assert!(cli.prompt.is_none());
    }

    #[test]
    fn the_default_launch_keeps_local_maintenance_and_a_named_engine_does_not() {
        // The gate is this client's own knowledge of how it started. A
        // front-end that hard-coded "trusted local" here would edit MCP and
        // plugin files that an engine on another machine never reads.
        let default = Cli::try_parse_from(["coda-tui"]).expect("parse");
        assert_eq!(default.access_mode(), crate::local::AccessMode::TrustedLocal);

        let custom = Cli::try_parse_from(["coda-tui", "--engine", "/opt/other/coda"])
            .expect("parse");
        assert_eq!(custom.access_mode(), crate::local::AccessMode::ApiOnly);
        assert!(!custom.access_mode().allows_local_maintenance());
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
