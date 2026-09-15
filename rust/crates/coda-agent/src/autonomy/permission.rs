//! Permission resolver for autonomous runs: a request is always answered
//! immediately, and never by a human.
//!
//! Interactively, an `Ask` verdict from [`crate::permission::policy::decide`]
//! means "put up a prompt and wait". Under a goal there is nobody to wait
//! for, so this module resolves every request from policy instead (spec
//! decision #4): allowed proceeds, and not-allowed is denied and the branch
//! is parked rather than the whole run stalling on it. [`PermissionResolver`]
//! is installed as the tool loop's [`PermissionPrompt`] only while a goal is
//! active; an interactive run's ordinary prompt chain (`permission::prompts`)
//! is untouched.
//!
//! ## Why `Ask` still means something here
//!
//! [`crate::permission::policy::decide`] itself never asks anyone — it maps
//! `(mode, tool)` to `Allow`/`Deny`/`Ask`, where `Ask` means "the mode alone
//! doesn't settle it; consult the risk classifier". That classifier
//! (`permission::classifier::ToolActionClassifier`) is the same one
//! interactive `bypassPermissions` mode uses to auto-approve safe actions, so
//! the autonomy envelope and `yolo-safe` agree on what "risky" means, by
//! construction. The classifier's own `Ask` verdict — "a human should
//! confirm" — has no human to reach here either, so it becomes a denial too,
//! exactly as the classifier's documentation already prescribes for a
//! headless host. A classifier that cannot be reached fails the same way its
//! own fail-closed convention already guarantees: an unreachable classifier
//! returns `Ask`, never an error, so there is nothing extra to handle here —
//! it falls out of treating `Ask` as a denial.
//!
//! ## Two-part "no thanks"
//!
//! A denial always writes two ledger entries together: a `Denial`, which is
//! what happened (this tool needed at least this mode), and a
//! `ParkedBlocker`, which is what it means for the run (this branch cannot
//! proceed right now). Only the second is read by the terminal-state proof,
//! but an operator reading the end-of-run report wants the first too, so they
//! do not have to reverse-engineer *why* from the blocker's `needs` field
//! alone.
//!
//! ## A note on paths
//!
//! This module lives at `autonomy::permission`, next to the crate's
//! unrelated top-level `permission` module. Every reference to the latter
//! below is written as a fully-qualified `crate::permission::...` path so the
//! two are never ambiguous at a glance.

use std::sync::Arc;

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use crate::permission::mode_state::SharedModeState;
use crate::permission::{PermissionDecision, PermissionMode, PermissionPrompt, ToolActionClassifier};
use crate::tool::Tool;

use super::ledger::{AssumptionLedger, BlockerKind};
use super::recovery::RecoveryGuard;

/// The least-permissive [`PermissionMode`] under which
/// [`crate::permission::policy::decide`] would return `Allow` for this tool
/// outright, with no classifier involved.
///
/// This is what a `Denial`'s `needed_mode` reports: not "the mode that
/// happened to be active", but an honest answer to "what would the operator
/// have to grant for this to just work". `decide` draws exactly one line for
/// every non-read-only tool: `edit_file` and `write_file` are allowed from
/// `acceptEdits` upward; everything else needs `bypassPermissions`. `default`
/// never appears here — it asks about everything, so it can never be the
/// *least* permissive mode that allows anything outright.
pub fn needed_mode_for(tool_name: &str) -> PermissionMode {
    if is_edit_tool(tool_name) {
        PermissionMode::AcceptEdits
    } else {
        PermissionMode::BypassPermissions
    }
}

/// Mirrors `crate::permission::policy`'s private `is_edit`, which decides
/// which tools `acceptEdits` auto-allows.
///
/// Duplicated rather than imported: `policy::is_edit` is not `pub`, and
/// widening its visibility to answer a question `policy` itself has no
/// reason to ask is a bigger change than this module is chartered to make.
/// Two tool names is cheap enough to keep in sync by hand.
fn is_edit_tool(tool_name: &str) -> bool {
    tool_name == "edit_file" || tool_name == "write_file"
}

/// The wire spelling of a mode: `"default"`, `"acceptEdits"`, `"plan"`,
/// `"bypassPermissions"`. Matches `coda-serve`'s `wire_permission_mode`
/// exactly. The ledger is summarised back to the model and, eventually, to
/// the operator, so it must use the same names they would type into
/// `/permissions`, not a `Debug` rendering of the enum.
fn mode_wire_name(mode: PermissionMode) -> &'static str {
    match mode {
        PermissionMode::Default => "default",
        PermissionMode::AcceptEdits => "acceptEdits",
        PermissionMode::Plan => "plan",
        PermissionMode::BypassPermissions => "bypassPermissions",
    }
}

/// Resolves every permission request from policy, without ever waiting for a
/// human.
///
/// Holds four things: where to read the live mode from, who to ask when the
/// mode alone is ambiguous, where to record what happened, and how to make an
/// unrecoverable action safe before it runs.
pub struct PermissionResolver {
    mode: SharedModeState,
    classifier: Arc<dyn ToolActionClassifier>,
    ledger: Arc<AssumptionLedger>,
    recovery: RecoveryGuard,
}

impl PermissionResolver {
    pub fn new(
        mode: SharedModeState,
        classifier: Arc<dyn ToolActionClassifier>,
        ledger: Arc<AssumptionLedger>,
        recovery: RecoveryGuard,
    ) -> Self {
        Self { mode, classifier, ledger, recovery }
    }

    /// The mode this resolver would act under right now.
    ///
    /// For diagnostics and tests. [`PermissionResolver::request`] never trusts
    /// a value read here — it re-reads the shared state itself on every call,
    /// which is what makes a mid-run mode change take effect immediately.
    pub fn current_mode(&self) -> PermissionMode {
        self.mode.get()
    }

    /// Record why `tool_name` was refused, and park the branch behind it.
    ///
    /// The exhaustion rule ([`AssumptionLedger::park_blocker`]) requires proof
    /// of an attempt before a blocker can be parked. The attempt is the very
    /// call that was just denied, so `tried` is never empty here and the park
    /// can never be refused — there is nothing for this method to do if it
    /// were, so any error is deliberately discarded rather than handled.
    ///
    /// The blocker is parked at **goal level** (`blocks: None`) rather than
    /// against the tool name. The work item a `WorkItemRef` names is joined
    /// against the live todo list by the termination proof, and a tool name is
    /// not a todo: parking `"run_command"` would put an entry in
    /// `parked_items` that no todo can ever match, while simultaneously
    /// disqualifying the stall path — leaving a run that can reach no terminal
    /// state at all. A permission denial genuinely is a goal-level
    /// capability limit, so that is how it is recorded.
    fn deny_and_park(&self, tool_name: &str, mode: PermissionMode) {
        let needed = needed_mode_for(tool_name);
        self.ledger.record_denial(tool_name, mode_wire_name(mode), mode_wire_name(needed));

        let tried =
            vec![format!("attempted {tool_name} under permission mode {}", mode_wire_name(mode))];
        let _ = self.ledger.park_blocker(
            BlockerKind::InsufficientPermission,
            &tried,
            format!("permission mode {} or higher", mode_wire_name(needed)),
            None,
        );
    }
}

#[async_trait]
impl PermissionPrompt for PermissionResolver {
    /// Resolve a request without ever suspending the run.
    ///
    /// 1. Read the live mode and hand `(mode, tool)` to
    ///    [`crate::permission::policy::decide`].
    /// 2. `Allow` → let [`RecoveryGuard::prepare`] make the action safe (a
    ///    no-op for the overwhelming majority of recoverable actions), then
    ///    allow.
    /// 3. `Deny` → deny and park.
    /// 4. `Ask` → consult the classifier. Its `Allow` is step 2; anything
    ///    else (`Ask`, or the `Deny` the classifier's own contract says it
    ///    will never produce) is step 3.
    async fn request(
        &self,
        tool: &dyn Tool,
        input_preview: &str,
        cancel: CancellationToken,
    ) -> bool {
        let mode = self.mode.get();
        match crate::permission::policy::decide(mode, tool) {
            PermissionDecision::Allow => {
                self.recovery.prepare(tool.name(), input_preview).await;
                true
            }
            PermissionDecision::Deny => {
                self.deny_and_park(tool.name(), mode);
                false
            }
            PermissionDecision::Ask => {
                let verdict = self.classifier.classify(tool.name(), input_preview, cancel).await;
                match verdict.decision {
                    PermissionDecision::Allow => {
                        self.recovery.prepare(tool.name(), input_preview).await;
                        true
                    }
                    PermissionDecision::Deny | PermissionDecision::Ask => {
                        self.deny_and_park(tool.name(), mode);
                        false
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::time::Duration;

    use super::*;
    use crate::autonomy::LedgerEntry;
    use crate::autonomy::recovery::{RecoveryExecutor, RecoveryKind};
    use crate::permission::mode_state::PermissionModeState;
    use crate::permission::{PermissionMode::*, ToolActionVerdict};
    use crate::tool::{ToolContext, ToolOutcome, ToolResult};

    // ── Shared test fixtures ──────────────────────────────────────────────────

    struct MockTool {
        name: &'static str,
        read_only: bool,
    }

    #[async_trait]
    impl Tool for MockTool {
        fn name(&self) -> &str {
            self.name
        }
        fn description(&self) -> &str {
            ""
        }
        fn input_schema_json(&self) -> &str {
            "{}"
        }
        fn is_read_only(&self) -> bool {
            self.read_only
        }
        async fn execute(
            &self,
            _: &serde_json::Value,
            _: &ToolContext,
            _: CancellationToken,
        ) -> ToolOutcome {
            ToolResult::ok("")
        }
    }

    fn mutating(name: &'static str) -> MockTool {
        MockTool { name, read_only: false }
    }

    /// A classifier that always returns the same verdict, and records whether
    /// it was ever consulted.
    struct FixedClassifier {
        verdict: ToolActionVerdict,
        called: AtomicBool,
    }

    impl FixedClassifier {
        fn new(verdict: ToolActionVerdict) -> Arc<Self> {
            Arc::new(Self { verdict, called: AtomicBool::new(false) })
        }
        fn was_called(&self) -> bool {
            self.called.load(Ordering::SeqCst)
        }
    }

    #[async_trait]
    impl ToolActionClassifier for FixedClassifier {
        async fn classify(&self, _: &str, _: &str, _: CancellationToken) -> ToolActionVerdict {
            self.called.store(true, Ordering::SeqCst);
            self.verdict.clone()
        }
    }

    /// A `RecoveryExecutor` fake that always succeeds, for tests that only
    /// care about the permission decision, not recovery bookkeeping.
    struct AlwaysRecovers;

    #[async_trait]
    impl RecoveryExecutor for AlwaysRecovers {
        async fn create_undo(&self, _kind: RecoveryKind, _context: &str) -> Result<String, String> {
            Ok("backup-ref".to_owned())
        }
    }

    /// Fails the test if `create_undo` is ever invoked — proves a denial
    /// never reaches the recovery guard.
    struct PanicIfRecovered;

    #[async_trait]
    impl RecoveryExecutor for PanicIfRecovered {
        async fn create_undo(&self, _kind: RecoveryKind, _context: &str) -> Result<String, String> {
            panic!("a denied action must never reach the recovery guard");
        }
    }

    fn resolver(
        mode: PermissionMode,
        classifier: Arc<dyn ToolActionClassifier>,
        recovery_executor: Arc<dyn RecoveryExecutor>,
    ) -> (PermissionResolver, Arc<PermissionModeState>, Arc<AssumptionLedger>) {
        let state = Arc::new(PermissionModeState::new(mode));
        let ledger = Arc::new(AssumptionLedger::new());
        let recovery = RecoveryGuard::new(recovery_executor, Arc::clone(&ledger));
        (
            PermissionResolver::new(Arc::clone(&state), classifier, Arc::clone(&ledger), recovery),
            state,
            ledger,
        )
    }

    fn allow_classifier() -> Arc<FixedClassifier> {
        FixedClassifier::new(ToolActionVerdict::allow())
    }

    fn ask_classifier() -> Arc<FixedClassifier> {
        FixedClassifier::new(ToolActionVerdict::ask("risky"))
    }

    // ── needed_mode_for ───────────────────────────────────────────────────────

    #[test]
    fn edit_tools_need_only_accept_edits() {
        assert_eq!(needed_mode_for("edit_file"), AcceptEdits);
        assert_eq!(needed_mode_for("write_file"), AcceptEdits);
    }

    #[test]
    fn every_other_tool_needs_bypass_permissions() {
        for tool in ["run_command", "delete_file", "task_start", "todo_write", "anything_else"] {
            assert_eq!(needed_mode_for(tool), BypassPermissions, "{tool}");
        }
    }

    // ── Allow ─────────────────────────────────────────────────────────────────

    /// Spec data flow: mode allows → recovery runs → Allow. The classifier is
    /// never even consulted, because `decide` already settled it.
    #[tokio::test]
    async fn bypass_permissions_allows_without_consulting_the_classifier() {
        let classifier = ask_classifier();
        let (resolver, _state, _ledger) =
            resolver(BypassPermissions, classifier.clone(), Arc::new(AlwaysRecovers));

        let allowed = resolver
            .request(&mutating("run_command"), "{}", CancellationToken::new())
            .await;

        assert!(allowed);
        assert!(!classifier.was_called(), "bypassPermissions must not need the classifier");
    }

    #[tokio::test]
    async fn accept_edits_allows_edit_tools_without_consulting_the_classifier() {
        let classifier = ask_classifier();
        let (resolver, _state, ledger) =
            resolver(AcceptEdits, classifier.clone(), Arc::new(AlwaysRecovers));

        let allowed =
            resolver.request(&mutating("edit_file"), "{}", CancellationToken::new()).await;

        assert!(allowed);
        assert!(!classifier.was_called());
        assert!(ledger.is_empty(), "an outright allow records nothing");
    }

    #[tokio::test]
    async fn read_only_tools_are_allowed_in_every_mode() {
        let ro = MockTool { name: "read_file", read_only: true };
        for mode in [Default, AcceptEdits, Plan, BypassPermissions] {
            let classifier = ask_classifier();
            let (resolver, _state, _ledger) =
                resolver(mode, classifier.clone(), Arc::new(AlwaysRecovers));
            let allowed = resolver.request(&ro, "{}", CancellationToken::new()).await;
            assert!(allowed, "read-only must allow in {mode:?}");
            assert!(!classifier.was_called(), "read-only never needs the classifier in {mode:?}");
        }
    }

    /// The recovery guard runs on the Allow path, and only there: an
    /// unrecoverable command that is nonetheless permitted gets its undo
    /// manufactured before proceeding.
    #[tokio::test]
    async fn an_allowed_unrecoverable_action_gets_a_recovery_entry() {
        let (resolver, _state, ledger) =
            resolver(BypassPermissions, allow_classifier(), Arc::new(AlwaysRecovers));

        let input = serde_json::json!({ "command": "git push --force origin main" }).to_string();
        let allowed = resolver.request(&mutating("run_command"), &input, CancellationToken::new()).await;

        assert!(allowed);
        match &ledger.entries()[0] {
            LedgerEntry::Recovery { undo_ref, .. } => {
                assert_eq!(undo_ref.as_deref(), Some("backup-ref"));
            }
            other => panic!("expected a Recovery entry, got {other:?}"),
        }
    }

    /// Same as above, but reached through the `Ask` → classifier `Allow` path
    /// rather than a direct mode Allow — recovery must run on both.
    #[tokio::test]
    async fn an_ask_resolved_to_allow_also_gets_a_recovery_entry() {
        let (resolver, _state, ledger) =
            resolver(Default, allow_classifier(), Arc::new(AlwaysRecovers));

        let input = serde_json::json!({ "command": "git clean -xfd" }).to_string();
        let allowed = resolver.request(&mutating("run_command"), &input, CancellationToken::new()).await;

        assert!(allowed);
        assert!(
            ledger
                .entries()
                .iter()
                .any(|e| matches!(e, LedgerEntry::Recovery { .. })),
            "the ask-resolved-to-allow path must still run recovery"
        );
    }

    /// CRITICAL (spec decision #6), proven at the resolver boundary rather
    /// than just inside `RecoveryGuard`: an undo that fails to be created must
    /// never turn an Allow into a Deny. `prepare` has no failure return value
    /// at all, so there is nothing here for `request` to branch on — the only
    /// way this could regress is a future change adding one.
    #[tokio::test]
    async fn an_allowed_action_proceeds_even_when_recovery_fails() {
        struct FailingRecovery;
        #[async_trait]
        impl RecoveryExecutor for FailingRecovery {
            async fn create_undo(&self, _: RecoveryKind, _: &str) -> Result<String, String> {
                Err("no network".to_owned())
            }
        }

        let (resolver, _state, ledger) =
            resolver(BypassPermissions, allow_classifier(), Arc::new(FailingRecovery));

        let input = serde_json::json!({ "command": "git push --force" }).to_string();
        let allowed = resolver.request(&mutating("run_command"), &input, CancellationToken::new()).await;

        assert!(allowed, "a failed undo must never block an otherwise-allowed action");
        match &ledger.entries()[0] {
            LedgerEntry::Recovery { undo_ref, error, .. } => {
                assert!(undo_ref.is_none());
                assert_eq!(error.as_deref(), Some("no network"));
            }
            other => panic!("expected a Recovery entry, got {other:?}"),
        }
    }

    // ── Deny ──────────────────────────────────────────────────────────────────

    /// Spec data flow: mode denies outright (`plan`) → Denial + ParkedBlocker,
    /// no classifier, no recovery.
    #[tokio::test]
    async fn plan_mode_denies_and_parks_without_the_classifier_or_recovery() {
        let classifier = allow_classifier();
        let (resolver, _state, ledger) =
            resolver(Plan, classifier.clone(), Arc::new(PanicIfRecovered));

        let allowed =
            resolver.request(&mutating("run_command"), "{}", CancellationToken::new()).await;

        assert!(!allowed);
        assert!(!classifier.was_called(), "an outright deny must not consult the classifier");
        assert!(
            ledger.snapshot().has_parked_blockers(),
            "a denial must park the branch behind a blocker"
        );
        assert_eq!(
            ledger.snapshot().goal_level_blockers(),
            1,
            "a permission limit blocks the goal, not one named todo"
        );

        let denial = ledger
            .entries()
            .into_iter()
            .find_map(|e| match e {
                LedgerEntry::Denial { tool, mode, needed_mode } => {
                    Some((tool, mode, needed_mode))
                }
                _ => None,
            })
            .expect("a Denial must be recorded");
        assert_eq!(denial, ("run_command".into(), "plan".into(), "bypassPermissions".into()));
    }

    /// "anything under plan needs at least acceptEdits or bypassPermissions":
    /// an edit tool's needed mode is the lower of the two, not bypass.
    #[tokio::test]
    async fn plan_mode_denying_an_edit_tool_reports_accept_edits_as_sufficient() {
        let (resolver, _state, ledger) =
            resolver(Plan, allow_classifier(), Arc::new(PanicIfRecovered));

        resolver.request(&mutating("edit_file"), "{}", CancellationToken::new()).await;

        let needed = ledger.entries().into_iter().find_map(|e| match e {
            LedgerEntry::Denial { needed_mode, .. } => Some(needed_mode),
            _ => None,
        });
        assert_eq!(needed.as_deref(), Some("acceptEdits"));
    }

    /// Spec data flow: mode says `Ask`, classifier says `Ask` too → treated as
    /// Deny, not as "prompt a human". This is THE key case: there is no
    /// human to ask, ever.
    #[tokio::test]
    async fn default_mode_with_a_risky_classifier_verdict_denies_and_parks() {
        let classifier = ask_classifier();
        let (resolver, _state, ledger) =
            resolver(Default, classifier.clone(), Arc::new(PanicIfRecovered));

        let allowed =
            resolver.request(&mutating("run_command"), "{}", CancellationToken::new()).await;

        assert!(!allowed);
        assert!(classifier.was_called(), "default mode must consult the classifier");
        assert!(ledger.snapshot().has_parked_blockers());
        assert_eq!(
            ledger.snapshot().goal_level_blockers(),
            1,
            "a permission limit blocks the goal, not one named todo"
        );
        assert!(
            ledger.snapshot().parked_items().is_empty(),
            "a tool name is not a work item and must never enter the todo join"
        );
        let denial = ledger.entries().into_iter().find_map(|e| match e {
            LedgerEntry::Denial { mode, needed_mode, .. } => {
                Some((mode, needed_mode))
            }
            _ => None,
        });
        assert_eq!(denial, Some(("default".into(), "bypassPermissions".into())));
    }

    #[tokio::test]
    async fn default_mode_with_a_safe_classifier_verdict_allows() {
        let (resolver, _state, ledger) =
            resolver(Default, allow_classifier(), Arc::new(AlwaysRecovers));

        let allowed =
            resolver.request(&mutating("run_command"), "{}", CancellationToken::new()).await;

        assert!(allowed);
        assert!(
            !ledger
                .entries()
                .iter()
                .any(|e| matches!(e, LedgerEntry::Denial { .. })),
            "an allowed action must not also be recorded as denied"
        );
    }

    #[tokio::test]
    async fn accept_edits_asking_about_a_non_edit_tool_can_still_deny() {
        let (resolver, _state, ledger) =
            resolver(AcceptEdits, ask_classifier(), Arc::new(PanicIfRecovered));

        let allowed =
            resolver.request(&mutating("run_command"), "{}", CancellationToken::new()).await;

        assert!(!allowed);
        let denial = ledger.entries().into_iter().find_map(|e| match e {
            LedgerEntry::Denial { mode, needed_mode, .. } => {
                Some((mode, needed_mode))
            }
            _ => None,
        });
        assert_eq!(denial, Some(("acceptEdits".into(), "bypassPermissions".into())));
    }

    /// A classifier that cannot be reached fails exactly like its documented
    /// convention: it returns `Ask`, never an error, and `Ask` is a denial
    /// here. There is nothing extra for the resolver to catch.
    #[tokio::test]
    async fn a_classifier_that_cannot_be_reached_fails_closed() {
        struct Unreachable;
        #[async_trait]
        impl ToolActionClassifier for Unreachable {
            async fn classify(&self, _: &str, _: &str, _: CancellationToken) -> ToolActionVerdict {
                ToolActionVerdict::ask("classifier unavailable — blocking for safety")
            }
        }

        let (resolver, _state, ledger) =
            resolver(Default, Arc::new(Unreachable), Arc::new(PanicIfRecovered));

        let allowed =
            resolver.request(&mutating("run_command"), "{}", CancellationToken::new()).await;

        assert!(!allowed, "an unreachable classifier must fail closed");
        assert!(ledger.snapshot().has_parked_blockers());
    }

    // ── Live mode changes ─────────────────────────────────────────────────────

    /// The whole point of reading `SharedModeState` fresh on every call: a
    /// `/yolo` mid-run takes effect on the very next permission request, with
    /// no need to rebuild the resolver.
    #[tokio::test]
    async fn a_mid_run_mode_change_is_observed_by_the_next_request() {
        let (resolver, state, _ledger) =
            resolver(Default, ask_classifier(), Arc::new(AlwaysRecovers));

        let first = resolver
            .request(&mutating("run_command"), "{}", CancellationToken::new())
            .await;
        assert!(!first, "default + an Ask classifier verdict must deny");

        state.set(BypassPermissions);

        let second = resolver
            .request(&mutating("run_command"), "{}", CancellationToken::new())
            .await;
        assert!(second, "bypassPermissions must be observed on the very next request");
        assert_eq!(resolver.current_mode(), BypassPermissions);
    }

    // ── Never blocks ──────────────────────────────────────────────────────────

    /// Every mode, every decision path, with a cancellation token that is
    /// already cancelled before the call even starts: the resolver must still
    /// return promptly rather than waiting on anything.
    #[tokio::test]
    async fn every_mode_resolves_promptly_even_with_a_pre_cancelled_token() {
        for mode in [Default, AcceptEdits, Plan, BypassPermissions] {
            for classifier in [allow_classifier() as Arc<dyn ToolActionClassifier>, ask_classifier()] {
                let (resolver, _state, _ledger) =
                    resolver(mode, classifier, Arc::new(AlwaysRecovers));
                let cancel = CancellationToken::new();
                cancel.cancel();

                let result = tokio::time::timeout(
                    Duration::from_millis(200),
                    resolver.request(&mutating("run_command"), "{}", cancel),
                )
                .await;

                assert!(
                    result.is_ok(),
                    "mode {mode:?} must resolve promptly instead of waiting on a human"
                );
            }
        }
    }
}
