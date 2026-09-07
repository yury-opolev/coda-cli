//! Context that follows a request through async work without a
//! process-global "current session".
//!
//! A prompt turn, an outer stream attempt, and the HTTP retry loop nested
//! inside it all need to agree on the same session/turn/request identity —
//! but they are separate `async fn`s connected only by `.await`, not by
//! shared mutable state. [`scope`] pins that identity to the *task* that is
//! executing (via [`tokio::task_local!`]), so a concurrent turn on another
//! task can never observe or corrupt this one's identity, and nothing needs
//! a new function parameter threaded through every call site in between.

use std::future::Future;
use std::sync::Arc;

use crate::event::Event;
use crate::writer::Logger;

/// Which kind of process this is. Recorded on every envelope so a shared log
/// directory can be told apart by role even before looking at `run_id`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProcessRole {
    /// The interactive terminal frontend (`coda`, no subcommand, or
    /// standalone `coda-tui`).
    Tui,
    /// A headless one-shot invocation (`coda run`).
    Run,
    /// The JSON-RPC engine (`coda serve`).
    Serve,
}

impl ProcessRole {
    pub fn as_str(self) -> &'static str {
        match self {
            ProcessRole::Tui => "tui",
            ProcessRole::Run => "run",
            ProcessRole::Serve => "serve",
        }
    }
}

/// Requested diagnostic detail. `Normal` still records every essential
/// lifecycle/failure event — this only trades off optional detail, never
/// essential discoverability.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum Verbosity {
    Normal,
    Debug,
    Trace,
}

impl Verbosity {
    pub fn as_str(self) -> &'static str {
        match self {
            Verbosity::Normal => "normal",
            Verbosity::Debug => "debug",
            Verbosity::Trace => "trace",
        }
    }

    /// Parses the explicit `--diagnostic-verbosity` flag value.
    pub fn parse(raw: &str) -> Option<Self> {
        match raw.trim().to_ascii_lowercase().as_str() {
            "normal" => Some(Verbosity::Normal),
            "debug" => Some(Verbosity::Debug),
            "trace" => Some(Verbosity::Trace),
            _ => None,
        }
    }

    /// Derives a safe verbosity hint from a legacy `--log-filter`/`CODA_LOG`
    /// `tracing` `EnvFilter` string, for backward compatibility only.
    ///
    /// This never enables raw tracing output; it only picks the loudest
    /// level named anywhere in the string (module selectors such as
    /// `coda_client=trace` still just mean "trace") so an old invocation
    /// that asked for verbose tracing keeps getting more diagnostic detail
    /// instead of silently going quiet.
    pub fn from_legacy_filter(raw: &str) -> Self {
        let lower = raw.to_ascii_lowercase();
        if lower.contains("trace") {
            Verbosity::Trace
        } else if lower.contains("debug") {
            Verbosity::Debug
        } else {
            Verbosity::Normal
        }
    }
}

/// The identity carried through one async request/turn/process.
///
/// Cheap to clone: everything is either an `Arc` or a small `Copy` value.
#[derive(Clone)]
pub struct DiagnosticContext {
    logger: Arc<Logger>,
    run_id: Arc<str>,
    session_id: Option<Arc<str>>,
    turn_id: Option<Arc<str>>,
    request_id: Option<Arc<str>>,
    provider: Option<Arc<str>>,
    model: Option<Arc<str>>,
}

impl DiagnosticContext {
    /// A fresh root context for a process: no session/turn/request yet.
    pub fn root(logger: Arc<Logger>, run_id: impl Into<Arc<str>>) -> Self {
        Self {
            logger,
            run_id: run_id.into(),
            session_id: None,
            turn_id: None,
            request_id: None,
            provider: None,
            model: None,
        }
    }

    pub fn run_id(&self) -> &str {
        &self.run_id
    }

    pub fn session_id(&self) -> Option<&str> {
        self.session_id.as_deref()
    }

    pub fn turn_id(&self) -> Option<&str> {
        self.turn_id.as_deref()
    }

    pub fn request_id(&self) -> Option<&str> {
        self.request_id.as_deref()
    }

    pub fn provider(&self) -> Option<&str> {
        self.provider.as_deref()
    }

    pub fn model(&self) -> Option<&str> {
        self.model.as_deref()
    }

    pub fn logger(&self) -> &Arc<Logger> {
        &self.logger
    }

    /// Returns a copy scoped to `session_id`, leaving the turn/request unset.
    pub fn with_session(&self, session_id: impl Into<Arc<str>>) -> Self {
        let mut next = self.clone();
        next.session_id = Some(session_id.into());
        next.turn_id = None;
        next.request_id = None;
        next
    }

    /// Returns a copy scoped to a new turn under the same session.
    pub fn with_turn(&self, turn_id: impl Into<Arc<str>>) -> Self {
        let mut next = self.clone();
        next.turn_id = Some(turn_id.into());
        next.request_id = None;
        next
    }

    /// Returns a copy scoped to a new outer request/stream attempt.
    pub fn with_request(&self, request_id: impl Into<Arc<str>>) -> Self {
        let mut next = self.clone();
        next.request_id = Some(request_id.into());
        next
    }

    /// Returns a copy with the canonical provider/model recorded.
    pub fn with_provider_model(
        &self,
        provider: Option<impl Into<Arc<str>>>,
        model: Option<impl Into<Arc<str>>>,
    ) -> Self {
        let mut next = self.clone();
        next.provider = provider.map(Into::into);
        next.model = model.map(Into::into);
        next
    }

    /// Records `event` under this context's identity. Never panics or
    /// propagates a write failure — a diagnostic can never take down the
    /// feature it is observing.
    pub fn record(&self, event: Event) {
        self.logger.record(self, event);
    }
}

tokio::task_local! {
    static CURRENT: DiagnosticContext;
}

/// Runs `fut` with `context` as the ambient [`current`] context for its task.
pub async fn scope<F: Future>(context: DiagnosticContext, fut: F) -> F::Output {
    CURRENT.scope(context, fut).await
}

/// The ambient context for the running task, if one was established by an
/// enclosing [`scope`]. `None` in library-only callers and tests that never
/// opted into a real process context — they simply do not log.
pub fn current() -> Option<DiagnosticContext> {
    CURRENT.try_with(|ctx| ctx.clone()).ok()
}

/// Records `event` under the ambient context, if any. A no-op when there is
/// no enclosing [`scope`] (e.g. a unit test that never opened one).
pub fn record(event: Event) {
    if let Some(ctx) = current() {
        ctx.record(event);
    }
}

// ── Parent/child correlation ────────────────────────────────────────────────
//
// A frontend that launches an engine child forwards these three environment
// variables so the child's own logger lands in the same directory, at the
// same requested verbosity, and — crucially — under the *same run id* as the
// parent, even though the two write to distinct files (never the same file:
// "one logger per executable process"). A standalone `coda serve` invoked
// directly simply finds none of these set and falls back to generating its
// own run id and resolving its own default directory.

/// Environment variable a parent sets so a spawned engine child's records
/// share its run id.
pub const RUN_ID_ENV: &str = "CODA_DIAG_RUN_ID";
/// Environment variable a parent sets so a spawned engine child's default
/// diagnostics land in the same directory as the parent's (its own explicit
/// destination's directory, or its own default directory) — the child always
/// gets its own uniquely-named file there, never the parent's file.
pub const DIR_ENV: &str = "CODA_DIAG_DIR";
/// Environment variable a parent sets to propagate its resolved verbosity.
pub const VERBOSITY_ENV: &str = "CODA_DIAG_VERBOSITY";

/// Reads an inherited run id, if a parent process set one.
pub fn env_run_id() -> Option<String> {
    std::env::var(RUN_ID_ENV).ok().filter(|s| !s.is_empty())
}

/// The run id for this process: inherited from a parent, or freshly generated.
pub fn resolve_run_id() -> String {
    env_run_id().unwrap_or_else(|| uuid::Uuid::new_v4().to_string())
}

/// Reads an inherited diagnostics directory, if a parent process set one.
pub fn env_dir() -> Option<std::path::PathBuf> {
    std::env::var_os(DIR_ENV)
        .map(std::path::PathBuf::from)
        .filter(|p| !p.as_os_str().is_empty())
}

/// Reads an inherited verbosity, if a parent process set one.
pub fn env_verbosity() -> Option<Verbosity> {
    std::env::var(VERBOSITY_ENV).ok().and_then(|s| Verbosity::parse(&s))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::writer::{Limits, Logger, Options};

    fn test_logger(dir: &std::path::Path) -> Arc<Logger> {
        Arc::new(
            Logger::open(
                Options {
                    directory: dir.to_path_buf(),
                    file: None,
                    role: ProcessRole::Run,
                    version: "test".into(),
                    verbosity: Verbosity::Normal,
                },
                Limits::default(),
            )
            .expect("logger opens"),
        )
    }

    #[tokio::test]
    async fn current_is_none_outside_any_scope() {
        assert!(current().is_none());
    }

    #[tokio::test]
    async fn scope_makes_the_context_ambient_for_the_duration_of_the_future() {
        let dir = tempfile::tempdir().unwrap();
        let logger = test_logger(dir.path());
        let ctx = DiagnosticContext::root(logger, "run-1");

        scope(ctx, async {
            let observed = current().expect("context is ambient inside scope");
            assert_eq!(observed.run_id(), "run-1");
        })
        .await;

        assert!(current().is_none(), "the context does not leak past its scope");
    }

    #[tokio::test]
    async fn with_turn_narrows_without_disturbing_the_run_id_or_session() {
        let dir = tempfile::tempdir().unwrap();
        let logger = test_logger(dir.path());
        let ctx = DiagnosticContext::root(logger, "run-1").with_session("sess-1");

        let turn_ctx = ctx.with_turn("turn-1");
        assert_eq!(turn_ctx.run_id(), "run-1");
        assert_eq!(turn_ctx.session_id(), Some("sess-1"));
        assert_eq!(turn_ctx.turn_id(), Some("turn-1"));

        let request_ctx = turn_ctx.with_request("req-1");
        assert_eq!(request_ctx.turn_id(), Some("turn-1"));
        assert_eq!(request_ctx.request_id(), Some("req-1"));

        // A second request under the same turn does not see the first's id.
        let second_request = turn_ctx.with_request("req-2");
        assert_eq!(second_request.request_id(), Some("req-2"));
        assert_eq!(request_ctx.request_id(), Some("req-1"), "prior context is untouched");
    }

    #[test]
    fn legacy_filter_picks_the_loudest_named_level() {
        assert_eq!(Verbosity::from_legacy_filter("warn"), Verbosity::Normal);
        assert_eq!(Verbosity::from_legacy_filter("coda_client=debug"), Verbosity::Debug);
        assert_eq!(Verbosity::from_legacy_filter("debug,coda_llm=trace"), Verbosity::Trace);
    }

    #[test]
    fn explicit_verbosity_parses_the_three_documented_levels() {
        assert_eq!(Verbosity::parse("normal"), Some(Verbosity::Normal));
        assert_eq!(Verbosity::parse("Debug"), Some(Verbosity::Debug));
        assert_eq!(Verbosity::parse("TRACE"), Some(Verbosity::Trace));
        assert_eq!(Verbosity::parse("verbose"), None);
    }

    // Env-var tests mutate process-global state; serialise them.
    static ENV_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

    #[test]
    fn resolve_run_id_inherits_the_parent_env_var_when_present() {
        let _guard = ENV_LOCK.lock().unwrap();
        std::env::set_var(RUN_ID_ENV, "inherited-run-id");
        assert_eq!(resolve_run_id(), "inherited-run-id");
        std::env::remove_var(RUN_ID_ENV);
    }

    #[test]
    fn resolve_run_id_generates_a_fresh_one_when_unset() {
        let _guard = ENV_LOCK.lock().unwrap();
        std::env::remove_var(RUN_ID_ENV);
        let a = resolve_run_id();
        let b = resolve_run_id();
        assert_ne!(a, b, "two independently-launched processes must not collide");
    }

    #[test]
    fn env_dir_and_env_verbosity_round_trip() {
        let _guard = ENV_LOCK.lock().unwrap();
        std::env::set_var(DIR_ENV, "/tmp/does-not-need-to-exist-for-this-test");
        std::env::set_var(VERBOSITY_ENV, "trace");
        assert_eq!(
            env_dir(),
            Some(std::path::PathBuf::from("/tmp/does-not-need-to-exist-for-this-test"))
        );
        assert_eq!(env_verbosity(), Some(Verbosity::Trace));
        std::env::remove_var(DIR_ENV);
        std::env::remove_var(VERBOSITY_ENV);
    }
}
