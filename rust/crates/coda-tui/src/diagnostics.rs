//! Process-level diagnostics initialization for every `coda`/`coda-tui`
//! entrypoint (interactive, run, serve, and the standalone `coda-tui`
//! binary). Lives here, rather than duplicated per binary, because `coda`
//! already depends on this crate for the interactive frontend itself.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use anyhow::{Context, Result};
use coda_client::EngineCommand;
use coda_diagnostics::{DiagnosticContext, Limits, Logger, Options, ProcessRole, Verbosity};

/// Resolves and opens this process's diagnostic logger, and returns a root
/// [`DiagnosticContext`] scoped to this process's run id, plus the directory
/// a spawned engine child should be told to log into (the explicit
/// destination's own directory, or the resolved default directory).
///
/// Default-directory failures degrade rather than fail (handled inside
/// [`Logger::open`]); an explicit `--log-file` destination that cannot be
/// opened is a hard startup error, matching `--log-file`'s existing
/// "explicit failure is fatal" contract.
pub fn init(
    role: ProcessRole,
    explicit_file: Option<PathBuf>,
    explicit_verbosity: Option<&str>,
    legacy_filter: &str,
) -> Result<(DiagnosticContext, PathBuf)> {
    let directory = coda_diagnostics::env_dir()
        .unwrap_or_else(|| coda_auth::coda_dir().join("logs").join("diagnostics"));
    init_in_directory(role, explicit_file, explicit_verbosity, legacy_filter, directory)
}

fn init_in_directory(
    role: ProcessRole,
    explicit_file: Option<PathBuf>,
    explicit_verbosity: Option<&str>,
    legacy_filter: &str,
    default_directory: PathBuf,
) -> Result<(DiagnosticContext, PathBuf)> {
    let verbosity = explicit_verbosity
        .and_then(Verbosity::parse)
        .or_else(coda_diagnostics::env_verbosity)
        .unwrap_or_else(|| Verbosity::from_legacy_filter(legacy_filter));

    let explicit_file = explicit_file.map(|path| {
        if path.is_absolute() {
            Ok(path)
        } else {
            std::env::current_dir().map(|cwd| cwd.join(path))
        }
    }).transpose()?;

    // What a spawned engine child should be told to log into: the directory
    // containing an explicit destination, or the resolved default directory.
    // The child always writes its own uniquely-named file there — never the
    // parent's explicit file itself.
    let forward_directory = explicit_file
        .as_ref()
        .and_then(|f| f.parent())
        .filter(|p| !p.as_os_str().is_empty())
        .map(PathBuf::from)
        .unwrap_or_else(|| default_directory.clone());

    let options = Options {
        directory: default_directory,
        file: explicit_file,
        role,
        version: crate::branding::version().to_owned(),
        verbosity,
    };
    let logger =
        Logger::open(options, Limits::default()).context("failed to open the diagnostic log destination")?;

    let run_id = coda_diagnostics::resolve_run_id();
    let ctx = DiagnosticContext::root(Arc::new(logger), run_id);
    ctx.record(coda_diagnostics::Event::ProcessStart);

    Ok((ctx, forward_directory))
}

pub fn record_engine_log_path(ctx: &DiagnosticContext, path: Option<&str>) {
    match path {
        Some(path) if path.len() <= 4096 && !path.chars().any(char::is_control)
            && Path::new(path).is_absolute() =>
        {
            ctx.record(coda_diagnostics::Event::EngineLogPath { path: path.into() });
        }
        _ => {
            ctx.record(coda_diagnostics::Event::StartupFailure { category: "engine_diagnostics_unavailable" });
            eprintln!("coda: engine diagnostic log is unavailable; use /log for logging status");
        }
    }
}

pub fn status_report(frontend: Option<&coda_diagnostics::Status>, engine: Option<&str>) -> String {
    let frontend = match frontend {
        Some(status) => {
            let health = match status.health {
                coda_diagnostics::Health::Healthy => "healthy".into(),
                coda_diagnostics::Health::Degraded(reason) => format!("degraded ({reason})"),
            };
            format!(
                "Frontend diagnostics: {health}\nFrontend log path:    {}\nFrontend mode:        {}\nFrontend verbosity:   {}",
                status.path.as_ref().map(|p| p.display().to_string()).unwrap_or_else(|| "(none)".into()),
                status.mode, status.verbosity.as_str(),
            )
        }
        None => "Frontend diagnostics: (not initialized in this process)".into(),
    };
    format!("{frontend}\nEngine log path:      {}", engine.unwrap_or("(not reported)"))
}

/// Sets the environment variables a spawned engine child reads to correlate
/// its own diagnostic log with this process's run id and directory.
pub fn forward_env(command: EngineCommand, ctx: &DiagnosticContext, directory: &Path) -> EngineCommand {
    command
        .env(coda_diagnostics::RUN_ID_ENV, ctx.run_id())
        .env(coda_diagnostics::DIR_ENV, directory.to_string_lossy().into_owned())
        .env(coda_diagnostics::VERBOSITY_ENV, ctx.logger().verbosity().as_str())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A terminal-independent smoke test for the no-argument interactive
    /// branch: this is exactly what `coda`'s and `coda-tui`'s `main()` call
    /// before ever touching a PTY, so it can be exercised without one.
    #[test]
    fn init_creates_a_default_log_file_without_touching_a_terminal() {
        let dir = tempfile::tempdir().unwrap();

        let (ctx, forward_dir) = init_in_directory(ProcessRole::Tui, None, None, "warn", dir.path().into()).expect("init succeeds");
        assert_eq!(forward_dir, dir.path());
        assert!(ctx.logger().status().path.unwrap().starts_with(dir.path()));
    }

    #[test]
    fn an_explicit_destination_forwards_its_own_directory_to_the_child() {
        let dir = tempfile::tempdir().unwrap();
        let explicit = dir.path().join("frontend.jsonl");

        let (_ctx, forward_dir) =
            init_in_directory(ProcessRole::Tui, Some(explicit.clone()), None, "warn", dir.path().into()).expect("init succeeds");
        assert_eq!(forward_dir, dir.path());
    }

    #[test]
    fn forward_env_sets_the_three_correlation_variables() {
        let dir = tempfile::tempdir().unwrap();
        let (ctx, _) = init_in_directory(ProcessRole::Tui, None, Some("trace"), "warn", dir.path().into()).expect("init succeeds");
        let command = forward_env(EngineCommand::default(), &ctx, dir.path());

        let has = |key: &str| command.env.iter().any(|(k, _)| k == key);
        assert!(has(coda_diagnostics::RUN_ID_ENV));
        assert!(has(coda_diagnostics::DIR_ENV));
        assert!(has(coda_diagnostics::VERBOSITY_ENV));
    }

    #[test]
    fn log_status_reports_degraded_default_and_engine_path() {
        let dir = tempfile::tempdir().unwrap();
        let blocked = dir.path().join("file-not-directory");
        std::fs::write(&blocked, "fixture").unwrap();
        let (ctx, _) = init_in_directory(ProcessRole::Tui, None, None, "warn", blocked).unwrap();
        let report = status_report(Some(&ctx.logger().status()), Some("engine-log.jsonl"));
        assert!(report.contains("degraded"));
        assert!(report.contains("Frontend log path:    (none)"));
        assert!(report.contains("engine-log.jsonl"));
        assert!(report.contains("Frontend mode:        default"));
    }
}
