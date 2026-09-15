//! Recovery guard: manufacturing an undo before the handful of actions that
//! cannot be undone at all.
//!
//! An autonomous run must never block on a dangerous action — that would
//! reintroduce exactly the stall the rest of this module tree exists to
//! remove. The design's recoverable/unrecoverable axis is what makes that
//! safe: almost everything a coding agent does — commits, pushes, deploys,
//! migrations, `git reset --hard`, rebases — can be undone after the fact
//! (`git revert`, redeploy, rollback, reflog), so it gets **no special
//! handling here at all**. Only the small set that has *no* way back is
//! worth manufacturing an undo for first.
//!
//! ## Fail open, never fail blocked (spec decision #6)
//!
//! If the undo itself cannot be created — no network, no permission to write
//! a ref, whatever — the action proceeds anyway. The alternative is safety
//! machinery that can itself become the blocker it was built to prevent,
//! which is a worse outcome than the risk it was guarding against.
//! [`RecoveryGuard::prepare`] therefore has no failure return value at all:
//! there is nothing for a caller to check before going ahead, by
//! construction rather than by convention.
//!
//! ## What `classify` actually looks at, and what it cannot see
//!
//! This is a heuristic over a command string, not a shell parser, and it is
//! deliberately narrow:
//!
//! - Only a fixed allowlist of shell-command-shaped tool names is inspected
//!   at all (`run_command` and a few common aliases an MCP server might use).
//!   A shell tool under an unrecognised name is invisible to this check.
//! - The command is tokenized on whitespace, not parsed as shell syntax, so
//!   quoting, escaping, `git` invoked via an absolute path or wrapper script,
//!   and argv-array command payloads can all evade it.
//! - It does not understand `--dry-run`-style flags that would make an
//!   otherwise-matching command a no-op; those are treated the same as the
//!   real thing, which only costs an unnecessary backup, never a missed one.
//!
//! Given all of that, the bias throughout is toward recognising the exact
//! patterns the design calls out precisely — correctly, including their safe
//! near-lookalikes — rather than toward guessing broadly. A false negative
//! here is a gap to close later; a false positive that reads
//! `--force-with-lease` as `--force` inverts a safety mechanism into a
//! liability.

use std::sync::Arc;

use async_trait::async_trait;

use super::ledger::AssumptionLedger;

// ── Classification ───────────────────────────────────────────────────────────

/// What kind of undo to manufacture before an unrecoverable action proceeds.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RecoveryKind {
    /// A ref pointing at history a force push is about to make unreachable
    /// from any branch (e.g. `refs/coda/backup/<timestamp>`).
    GitBackupRef,
    /// A stash of the uncommitted/untracked work `git clean` is about to
    /// delete.
    GitStash,
    /// A snapshot of a data store or package-registry state before an
    /// irreversible drop, truncate, or publish.
    Snapshot,
}

impl RecoveryKind {
    pub fn as_str(self) -> &'static str {
        match self {
            RecoveryKind::GitBackupRef => "gitBackupRef",
            RecoveryKind::GitStash => "gitStash",
            RecoveryKind::Snapshot => "snapshot",
        }
    }
}

/// Tool names treated as shell-command-shaped: their JSON input carries a raw
/// command line worth inspecting for the patterns below.
///
/// This is a fixed allowlist, not an understanding of what a tool does.
/// `run_command` is the crate's own built-in; the rest are names commonly
/// chosen by MCP-provided shell tools. A shell tool under an unrecognised
/// name is invisible to this check — a documented limit, not an oversight.
const SHELL_COMMAND_TOOL_NAMES: &[&str] =
    &["run_command", "run_shell_command", "shell", "bash", "execute_command", "terminal"];

fn is_shell_command_tool(tool_name: &str) -> bool {
    SHELL_COMMAND_TOOL_NAMES.contains(&tool_name)
}

/// Pull the command line out of a tool's raw JSON input.
///
/// The real `run_command` tool always sends a well-formed `{"command": "..."}`
/// object, which is the fast path below. Everything else — malformed JSON, a
/// bare JSON string, a differently-shaped object — falls back to scanning
/// `input_json` itself verbatim, so the pattern checks in
/// [`classify_command`] still get a chance to see anything command-shaped it
/// contains. This never panics: every branch produces a `String`.
fn extract_command(input_json: &str) -> String {
    match serde_json::from_str::<serde_json::Value>(input_json) {
        Ok(serde_json::Value::Object(map)) => {
            for key in ["command", "cmd"] {
                if let Some(s) = map.get(key).and_then(|v| v.as_str()) {
                    return s.to_owned();
                }
            }
            // A valid object, but not the shape we expected: fall back to the
            // raw text so the substring/token checks still get a look at it.
            input_json.to_owned()
        }
        // A bare JSON string is unpacked so token boundaries aren't thrown off
        // by the surrounding quote characters.
        Ok(serde_json::Value::String(s)) => s,
        // Malformed JSON, or any other JSON shape (array, number, null): scan
        // the raw text as-is. Never panic.
        _ => input_json.to_owned(),
    }
}

/// Whether `git push` is being invoked as a force push.
///
/// Three forms, all of which overwrite history:
/// - `--force` as an exact token,
/// - a short-flag cluster containing `f` (`-f`, `-uf`, `-fu`),
/// - a refspec with a leading `+` (`git push origin +main`), which is the
///   standard scripted force and carries no flag at all.
///
/// Exact token equality for `--force` is the whole point: `--force-with-lease`
/// is the safe form — it refuses to overwrite a ref it hasn't seen — and
/// tokenizing on whitespace means it is simply never equal to the token
/// `--force`. A `contains("--force")` substring check would have matched it by
/// accident, which is the one mistake this function exists to avoid.
///
/// The cluster and `+refspec` checks are deliberately generous. A false
/// positive costs one unnecessary backup ref; a false negative is history
/// overwritten with no way back. No other `git push` short flag uses the letter
/// `f`, and no branch or remote name may begin with `+`, so neither check has a
/// realistic downside.
fn is_force_push(tokens: &[&str]) -> bool {
    if !(tokens.contains(&"git") && tokens.contains(&"push")) {
        return false;
    }
    tokens.iter().any(|t| {
        if *t == "--force" {
            return true;
        }
        // A refspec forced with a leading '+'.
        if t.starts_with('+') && t.len() > 1 {
            return true;
        }
        is_short_flag_cluster_containing(t, &['f'])
    })
}

/// Whether `token` is a short-option cluster — exactly one leading `-`
/// followed by flag letters, as in `-f`, `-fd` or `-xdf` — containing any of
/// `letters`.
///
/// The `!starts_with('-')` check on the remainder is what keeps long options
/// out: `--force-with-lease` strips to `-force-with-lease`, which is rejected
/// before its letters are ever inspected.
fn is_short_flag_cluster_containing(token: &str, letters: &[char]) -> bool {
    match token.strip_prefix('-') {
        Some(rest) if !rest.is_empty() && !rest.starts_with('-') => {
            rest.chars().any(|c| letters.contains(&c))
        }
        _ => false,
    }
}

/// Whether `git clean` is being invoked with any of `-x`, `-d`, `-f` — alone,
/// or bundled into one short-flag cluster such as `-xdf` — or the long form
/// `--force`. Any of these can destroy uncommitted or untracked work with no
/// way back, which is why `git clean` is unrecoverable rather than merely
/// recoverable-with-effort the way `git reset --hard` is.
fn is_destructive_git_clean(tokens: &[&str]) -> bool {
    if !(tokens.contains(&"git") && tokens.contains(&"clean")) {
        return false;
    }
    tokens
        .iter()
        .any(|t| *t == "--force" || is_short_flag_cluster_containing(t, &['x', 'd', 'f']))
}

/// Whether the command contains a SQL `DROP DATABASE`, `DROP TABLE`, or
/// `TRUNCATE`, case-insensitively. A plain substring search rather than a SQL
/// parser — deliberately simple, and biased toward catching the real thing at
/// the cost of also flagging the phrase inside an unrelated string.
fn is_sql_drop_or_truncate(command: &str) -> bool {
    let upper = command.to_ascii_uppercase();
    upper.contains("DROP DATABASE") || upper.contains("DROP TABLE") || upper.contains("TRUNCATE")
}

/// Whether the command publishes to a package registry: `npm publish`,
/// `cargo publish`, or `dotnet nuget push`. Once published, a version cannot
/// be un-published from most registries, so this is unrecoverable the same
/// way a data-store drop is.
fn is_registry_publish(tokens: &[&str]) -> bool {
    (tokens.contains(&"npm") && tokens.contains(&"publish"))
        || (tokens.contains(&"cargo") && tokens.contains(&"publish"))
        || (tokens.contains(&"dotnet") && tokens.contains(&"nuget") && tokens.contains(&"push"))
}

/// Classify a single command line against the unrecoverable patterns.
///
/// Order only matters in that the first match wins; the patterns are disjoint
/// in practice (a command is never both a force push and a registry publish).
fn classify_command(command: &str) -> Option<RecoveryKind> {
    let tokens: Vec<&str> = command.split_whitespace().collect();
    if is_force_push(&tokens) {
        return Some(RecoveryKind::GitBackupRef);
    }
    if is_destructive_git_clean(&tokens) {
        return Some(RecoveryKind::GitStash);
    }
    if is_sql_drop_or_truncate(command) {
        return Some(RecoveryKind::Snapshot);
    }
    if is_registry_publish(&tokens) {
        return Some(RecoveryKind::Snapshot);
    }
    None
}

// ── Executor seam ─────────────────────────────────────────────────────────────

/// Seam that actually creates the undo. A real implementation shells out to
/// `git` or the relevant store; tests fake it.
#[async_trait]
pub trait RecoveryExecutor: Send + Sync {
    /// Returns the undo reference (a ref name, stash tag, snapshot id) or an
    /// error string describing why one could not be made.
    async fn create_undo(&self, kind: RecoveryKind, context: &str) -> Result<String, String>;
}

// ── Guard ─────────────────────────────────────────────────────────────────────

/// Manufactures an undo for unrecoverable actions, and never blocks one.
pub struct RecoveryGuard {
    executor: Arc<dyn RecoveryExecutor>,
    ledger: Arc<AssumptionLedger>,
}

impl RecoveryGuard {
    /// Build a guard.
    ///
    /// `ledger` must be the supervisor's own handle, not a freshly constructed
    /// one — the same rule [`super::answerer::ProxyAnswerer::new`] documents.
    /// A separate ledger would accept every recovery record and surface none
    /// of them to the report or the termination proof.
    pub fn new(executor: Arc<dyn RecoveryExecutor>, ledger: Arc<AssumptionLedger>) -> Self {
        Self { executor, ledger }
    }

    /// Decide whether `tool_name`/`input_json` names an unrecoverable action,
    /// and if so, what kind of undo it needs.
    ///
    /// Pure and synchronous by design: this is the piece the design calls out
    /// as independently testable, with no I/O of its own. Returns `None` for
    /// every recoverable action and for every tool this module does not
    /// inspect at all.
    pub fn classify(tool_name: &str, input_json: &str) -> Option<RecoveryKind> {
        if !is_shell_command_tool(tool_name) {
            return None;
        }
        classify_command(&extract_command(input_json))
    }

    /// Manufacture an undo if `tool_name`/`input_json` needs one. Never fails
    /// the action.
    ///
    /// Recoverable actions (the overwhelming majority) return immediately and
    /// touch neither the executor nor the ledger — commits, pushes, deploys
    /// and migrations get no special handling, per the design.
    ///
    /// For an unrecoverable action, [`RecoveryExecutor::create_undo`] is
    /// called and the outcome is always recorded, but the call proceeds
    /// either way: success records `Recovery { undo_ref: Some(_), error: None
    /// }`, and failure records `Recovery { undo_ref: None, error: Some(_) }`.
    /// There is no third outcome and no return value for a caller to branch
    /// on — safety machinery must never become a blocker (spec decision #6).
    pub async fn prepare(&self, tool_name: &str, input_json: &str) {
        let Some(kind) = Self::classify(tool_name, input_json) else {
            return;
        };
        let action = format!("{tool_name}: {}", extract_command(input_json));
        match self.executor.create_undo(kind, &action).await {
            Ok(undo_ref) => self.ledger.record_recovery(&action, Some(&undo_ref), None),
            Err(error) => self.ledger.record_recovery(&action, None, Some(&error)),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn command_input(command: &str) -> String {
        serde_json::json!({ "command": command }).to_string()
    }

    // ── classify: force push ─────────────────────────────────────────────────

    #[test]
    fn a_bare_dash_f_force_push_is_flagged() {
        let input = command_input("git push -f origin main");
        assert_eq!(RecoveryGuard::classify("run_command", &input), Some(RecoveryKind::GitBackupRef));
    }

    #[test]
    fn a_long_form_force_push_is_flagged() {
        let input = command_input("git push --force origin main");
        assert_eq!(RecoveryGuard::classify("run_command", &input), Some(RecoveryKind::GitBackupRef));
    }

    /// SECURITY: `--force-with-lease` is the safe form of a force push — it
    /// refuses to overwrite a ref it hasn't seen — and must never be treated
    /// the same as a bare `--force`. A naive `contains("--force")` check would
    /// have matched this by accident; this test guards against exactly that.
    #[test]
    fn force_with_lease_is_not_flagged() {
        let input = command_input("git push --force-with-lease origin main");
        assert_eq!(RecoveryGuard::classify("run_command", &input), None);
    }

    /// SECURITY: `git push origin +main` is a full history overwrite that
    /// carries no flag at all — it is the standard scripted force — and was
    /// previously invisible to this guard, so it would have run with no undo.
    #[test]
    fn a_plus_refspec_force_push_is_flagged() {
        for command in [
            "git push origin +main",
            "git push origin +refs/heads/main:refs/heads/main",
            "git push --quiet origin +topic",
        ] {
            let input = command_input(command);
            assert_eq!(
                RecoveryGuard::classify("run_command", &input),
                Some(RecoveryKind::GitBackupRef),
                "{command} overwrites history and needs an undo first"
            );
        }
    }

    /// A bundled short-flag cluster is still a force push. Over-detecting costs
    /// one unused backup ref; under-detecting costs the history.
    #[test]
    fn a_bundled_short_flag_force_push_is_flagged() {
        for command in ["git push -uf origin main", "git push -fu origin main"] {
            let input = command_input(command);
            assert_eq!(
                RecoveryGuard::classify("run_command", &input),
                Some(RecoveryKind::GitBackupRef),
                "{command} is a force push"
            );
        }
    }

    /// The generosity must not spill over: ordinary pushes stay unflagged.
    #[test]
    fn ordinary_push_flags_are_not_mistaken_for_a_force() {
        for command in [
            "git push origin main",
            "git push -u origin main",
            "git push --set-upstream origin main",
            "git push --quiet origin main",
            "git push --tags",
        ] {
            let input = command_input(command);
            assert_eq!(
                RecoveryGuard::classify("run_command", &input),
                None,
                "{command} is an ordinary push"
            );
        }
    }

    #[test]
    fn force_with_lease_with_a_ref_argument_is_still_not_flagged() {
        let input = command_input("git push --force-with-lease=origin/main origin main");
        assert_eq!(RecoveryGuard::classify("run_command", &input), None);
    }

    #[test]
    fn a_plain_git_push_with_no_force_flag_is_not_flagged() {
        let input = command_input("git push origin main");
        assert_eq!(RecoveryGuard::classify("run_command", &input), None);
    }

    // ── classify: destructive git clean ──────────────────────────────────────

    #[test]
    fn git_clean_with_combined_short_flags_is_flagged() {
        for flags in ["-xfd", "-fdx", "-fd", "-df", "-f", "-d", "-x"] {
            let input = command_input(&format!("git clean {flags}"));
            assert_eq!(
                RecoveryGuard::classify("run_command", &input),
                Some(RecoveryKind::GitStash),
                "git clean {flags} should be flagged"
            );
        }
    }

    #[test]
    fn git_clean_with_long_form_force_is_flagged() {
        let input = command_input("git clean --force -d");
        assert_eq!(RecoveryGuard::classify("run_command", &input), Some(RecoveryKind::GitStash));
    }

    #[test]
    fn git_clean_dry_run_or_interactive_alone_is_not_flagged() {
        for flags in ["-n", "--dry-run", "-i", "-q"] {
            let input = command_input(&format!("git clean {flags}"));
            assert_eq!(
                RecoveryGuard::classify("run_command", &input),
                None,
                "git clean {flags} must not be flagged"
            );
        }
    }

    #[test]
    fn git_clean_without_git_in_the_command_is_not_flagged() {
        // "clean" and "-f" both present, but this isn't `git clean` at all.
        let input = command_input("npm run clean -- -f");
        assert_eq!(RecoveryGuard::classify("run_command", &input), None);
    }

    // ── classify: SQL drop / truncate ────────────────────────────────────────

    #[test]
    fn drop_database_is_flagged_case_insensitively() {
        for cmd in ["psql -c 'DROP DATABASE prod'", "psql -c 'drop database prod'"] {
            let input = command_input(cmd);
            assert_eq!(RecoveryGuard::classify("run_command", &input), Some(RecoveryKind::Snapshot));
        }
    }

    #[test]
    fn drop_table_is_flagged() {
        let input = command_input("mysql -e 'DROP TABLE users'");
        assert_eq!(RecoveryGuard::classify("run_command", &input), Some(RecoveryKind::Snapshot));
    }

    #[test]
    fn truncate_is_flagged() {
        let input = command_input("psql -c 'truncate orders'");
        assert_eq!(RecoveryGuard::classify("run_command", &input), Some(RecoveryKind::Snapshot));
    }

    // ── classify: registry publish ───────────────────────────────────────────

    #[test]
    fn npm_publish_is_flagged() {
        let input = command_input("npm publish --access public");
        assert_eq!(RecoveryGuard::classify("run_command", &input), Some(RecoveryKind::Snapshot));
    }

    #[test]
    fn cargo_publish_is_flagged() {
        let input = command_input("cargo publish");
        assert_eq!(RecoveryGuard::classify("run_command", &input), Some(RecoveryKind::Snapshot));
    }

    #[test]
    fn dotnet_nuget_push_is_flagged() {
        let input = command_input("dotnet nuget push bin/pkg.nupkg -k KEY -s https://example.test");
        assert_eq!(RecoveryGuard::classify("run_command", &input), Some(RecoveryKind::Snapshot));
    }

    #[test]
    fn dotnet_push_without_nuget_is_not_flagged() {
        // Not a real command, but guards against matching on "dotnet" + "push"
        // alone, which would also fire on an unrelated `dotnet push-changes`.
        let input = command_input("dotnet push-changes");
        assert_eq!(RecoveryGuard::classify("run_command", &input), None);
    }

    // ── classify: ordinary/recoverable commands are never flagged ────────────

    #[test]
    fn ordinary_recoverable_commands_are_never_flagged() {
        for cmd in [
            "git commit -m \"wip\"",
            "git push origin main",
            "git push",
            "cargo build --release",
            "git reset --hard HEAD~1",
            "git rebase main",
            "git rebase -i HEAD~3",
            "kubectl apply -f k8s/deploy.yaml",
            "alembic upgrade head",
            "npm run migrate",
            "terraform apply",
        ] {
            let input = command_input(cmd);
            assert_eq!(
                RecoveryGuard::classify("run_command", &input),
                None,
                "{cmd:?} is recoverable and must not be flagged"
            );
        }
    }

    // ── classify: scope limits ────────────────────────────────────────────────

    #[test]
    fn non_shell_tools_are_never_inspected() {
        // Even a payload that looks dangerous must not be flagged for a tool
        // that isn't shell-command-shaped: classify only looks at command
        // tools, never at arbitrary tool input.
        let input = command_input("git push --force");
        for tool in ["edit_file", "write_file", "read_file", "todo_write", "ask_user_question"] {
            assert_eq!(RecoveryGuard::classify(tool, &input), None, "{tool} must not be inspected");
        }
    }

    #[test]
    fn malformed_json_falls_back_to_scanning_the_raw_text_without_panicking() {
        let input = "not valid json but mentions git push --force in passing";
        assert_eq!(RecoveryGuard::classify("run_command", input), Some(RecoveryKind::GitBackupRef));
    }

    #[test]
    fn malformed_json_with_no_dangerous_text_classifies_as_recoverable() {
        let input = "{not: json, at: all";
        assert_eq!(RecoveryGuard::classify("run_command", input), None);
    }

    #[test]
    fn a_json_value_that_is_not_an_object_does_not_panic() {
        for input in ["[1,2,3]", "42", "null", "true"] {
            assert_eq!(RecoveryGuard::classify("run_command", input), None, "input {input:?}");
        }
    }

    #[test]
    fn a_bare_json_string_command_is_still_parsed() {
        let input = serde_json::to_string("git push --force").unwrap();
        assert_eq!(RecoveryGuard::classify("run_command", &input), Some(RecoveryKind::GitBackupRef));
    }

    #[test]
    fn the_cmd_key_is_also_recognised() {
        let input = serde_json::json!({ "cmd": "git push --force" }).to_string();
        assert_eq!(RecoveryGuard::classify("run_command", &input), Some(RecoveryKind::GitBackupRef));
    }

    #[test]
    fn empty_input_does_not_panic() {
        assert_eq!(RecoveryGuard::classify("run_command", ""), None);
    }

    // ── prepare ────────────────────────────────────────────────────────────────

    struct ScriptedExecutor {
        result: Result<String, String>,
    }

    #[async_trait]
    impl RecoveryExecutor for ScriptedExecutor {
        async fn create_undo(&self, _kind: RecoveryKind, _context: &str) -> Result<String, String> {
            self.result.clone()
        }
    }

    /// Fails the test if `create_undo` is ever invoked — used to prove a
    /// recoverable action never reaches the executor at all.
    struct PanicIfCalledExecutor;

    #[async_trait]
    impl RecoveryExecutor for PanicIfCalledExecutor {
        async fn create_undo(&self, _kind: RecoveryKind, _context: &str) -> Result<String, String> {
            panic!("create_undo must not be called for a recoverable action");
        }
    }

    #[tokio::test]
    async fn prepare_records_the_undo_ref_on_success() {
        let ledger = Arc::new(AssumptionLedger::new());
        let executor = Arc::new(ScriptedExecutor { result: Ok("refs/coda/backup/123".to_owned()) });
        let guard = RecoveryGuard::new(executor, Arc::clone(&ledger));

        guard.prepare("run_command", &command_input("git push --force origin main")).await;

        match &ledger.entries()[0] {
            crate::autonomy::LedgerEntry::Recovery { undo_ref, error, .. } => {
                assert_eq!(undo_ref.as_deref(), Some("refs/coda/backup/123"));
                assert!(error.is_none());
            }
            other => panic!("expected a Recovery entry, got {other:?}"),
        }
    }

    /// CRITICAL (spec decision #6): when the undo cannot be created, the
    /// action still proceeds. `prepare` has no failure return at all, so
    /// there is nothing here for a caller to branch on — the only observable
    /// effect of a failed undo is this ledger entry.
    #[tokio::test]
    async fn prepare_records_the_error_but_never_prevents_the_action() {
        let ledger = Arc::new(AssumptionLedger::new());
        let executor = Arc::new(ScriptedExecutor { result: Err("no network".to_owned()) });
        let guard = RecoveryGuard::new(executor, Arc::clone(&ledger));

        guard.prepare("run_command", &command_input("git clean -xfd")).await;

        match &ledger.entries()[0] {
            crate::autonomy::LedgerEntry::Recovery { undo_ref, error, .. } => {
                assert!(undo_ref.is_none());
                assert_eq!(error.as_deref(), Some("no network"));
            }
            other => panic!("expected a Recovery entry, got {other:?}"),
        }
    }

    #[tokio::test]
    async fn a_recoverable_action_never_reaches_the_executor_and_records_nothing() {
        let ledger = Arc::new(AssumptionLedger::new());
        let executor = Arc::new(PanicIfCalledExecutor);
        let guard = RecoveryGuard::new(executor, Arc::clone(&ledger));

        guard.prepare("run_command", &command_input("git commit -m \"wip\"")).await;

        assert!(ledger.is_empty(), "a recoverable action must not write to the ledger at all");
    }

    #[tokio::test]
    async fn a_non_shell_tool_never_reaches_the_executor_even_with_dangerous_looking_input() {
        let ledger = Arc::new(AssumptionLedger::new());
        let executor = Arc::new(PanicIfCalledExecutor);
        let guard = RecoveryGuard::new(executor, Arc::clone(&ledger));

        guard.prepare("edit_file", &command_input("git push --force")).await;

        assert!(ledger.is_empty());
    }
}
