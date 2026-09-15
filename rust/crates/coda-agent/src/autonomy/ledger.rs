//! The assumption ledger: what the agent decided, parked, or worked around
//! while nobody was watching.
//!
//! Every autonomous choice leaves a record here. The ledger has two jobs:
//!
//! 1. **Accountability.** An operator who walks away for eight hours needs to
//!    read back exactly what was assumed on their behalf, and why.
//! 2. **Proving termination.** The stop decision asks the ledger whether every
//!    open work item is parked behind a blocker. That question is only
//!    answerable because parking is recorded as structured data rather than
//!    prose, which is what turns "I think we're stuck" into a claim the loop
//!    can actually verify.
//!
//! ## The exhaustion rule
//!
//! [`AssumptionLedger::park_blocker`] refuses an empty `tried` list. A blocker
//! is only genuine once the agent has actually attempted a resolution, and
//! without this rule "blocked" would quietly become the new "asked" — the very
//! behaviour the autonomy work exists to remove. The rule is enforced here, at
//! the only place parking can happen, rather than trusted to each caller.
//!
//! ## Redaction
//!
//! Ledger text is summarised back to the model and rendered in the end-of-run
//! report, so it is a leak path. Every free-text field is redacted on the way
//! in, using the same [`StreamingSecretRedactor`] the task logs use. Redacting
//! on write rather than on read means a secret is never at rest in memory, and
//! no future reader can forget to redact.

use std::sync::Mutex;

use crate::tasks::streaming_secret_redactor::StreamingSecretRedactor;

/// How much the proxy answerer trusted its own choice.
///
/// Deliberately coarse. A model asked for a percentage will happily invent
/// "87%", and that false precision would then be rendered to an operator as
/// though it meant something. Three buckets carry the only distinction the
/// report actually draws: whether this decision deserves a second look.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Confidence {
    High,
    Medium,
    Low,
}

impl Confidence {
    /// Whether the end-of-run report should lead with this entry.
    pub fn needs_review(self) -> bool {
        matches!(self, Confidence::Low)
    }

    pub fn as_str(self) -> &'static str {
        match self {
            Confidence::High => "high",
            Confidence::Medium => "medium",
            Confidence::Low => "low",
        }
    }
}

/// Why a branch of work could not proceed.
///
/// These are the classes that are genuinely about **capability** — the agent
/// cannot do the thing, by any available means. Risk, ambiguity and "this
/// looks scary" are deliberately absent: those are resolvable, and resolving
/// them is the agent's job.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BlockerKind {
    /// A credential or secret that is not in the environment or the store.
    MissingCredential,
    /// A repository, API, or network resource the agent cannot reach.
    MissingAccess,
    /// The active permission mode forbids the only action that would work.
    InsufficientPermission,
    /// A third-party service is down or unreachable.
    ExternalUnavailable,
    /// No available tool can perform the action at all.
    Infeasible,
}

impl BlockerKind {
    pub fn as_str(self) -> &'static str {
        match self {
            BlockerKind::MissingCredential => "missingCredential",
            BlockerKind::MissingAccess => "missingAccess",
            BlockerKind::InsufficientPermission => "insufficientPermission",
            BlockerKind::ExternalUnavailable => "externalUnavailable",
            BlockerKind::Infeasible => "infeasible",
        }
    }
}

/// A reference to the work item a blocker is parked against.
///
/// Two fields because the same value has to do two incompatible jobs. The
/// report needs human-readable text, which must be redacted before it is shown
/// or summarised back to the model. The terminal-state proof needs to join
/// blockers against the live todo list, and redaction is not
/// identity-preserving: a todo whose text happens to contain something
/// resembling a credential would be stored redacted, never match its
/// unredacted self, and silently defeat the proof. A run could then fail to
/// reach *any* terminal state — exactly the hang this design exists to remove.
///
/// So `display` carries the redacted prose and `key` carries a hash of the
/// original. A hash cannot leak the secret it was derived from, and it is
/// unaffected by redaction, so the join stays sound however the text reads.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorkItemRef {
    display: String,
    key: u64,
}

impl WorkItemRef {
    /// Build a reference from the item's original (unredacted) text.
    pub fn new(original: &str) -> Self {
        Self { display: redact(original), key: work_item_key(original) }
    }

    /// The redacted text, safe to render or summarise.
    pub fn display(&self) -> &str {
        &self.display
    }

    /// Whether this reference denotes `original`, compared on the
    /// redaction-independent key rather than on prose.
    pub fn matches(&self, original: &str) -> bool {
        self.key == work_item_key(original)
    }
}

/// Hash a work item's original text into a join key.
///
/// Only ever compared against other keys produced in the same process during
/// the same run — the ledger is in-memory session state — so a
/// process-stable hash is sufficient and no cross-version guarantee is needed.
fn work_item_key(original: &str) -> u64 {
    use std::hash::{Hash, Hasher};
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    original.trim().hash(&mut hasher);
    hasher.finish()
}

/// One recorded autonomous decision.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LedgerEntry {
    /// A question was answered on the operator's behalf.
    Assumption {
        question: String,
        options: Vec<String>,
        chosen: String,
        rationale: String,
        confidence: Confidence,
    },
    /// A branch of work stopped, and why.
    ParkedBlocker {
        kind: BlockerKind,
        /// What was actually attempted before giving up. Never empty — see
        /// the exhaustion rule.
        tried: Vec<String>,
        /// What a human would have to supply to unblock it.
        needs: String,
        /// The work item this blocks, when it maps to one. `None` means it
        /// blocks the goal as a whole rather than a single todo.
        blocks: Option<WorkItemRef>,
    },
    /// An undo was manufactured before an unrecoverable action.
    Recovery {
        action: String,
        /// The backup ref, stash tag or snapshot id, when one could be made.
        undo_ref: Option<String>,
        /// Why the undo could not be made. The action still proceeded.
        error: Option<String>,
    },
    /// The permission envelope refused an action.
    Denial {
        tool: String,
        mode: String,
        needed_mode: String,
    },
}

/// Rejected ledger writes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LedgerError {
    /// The exhaustion rule: parking requires at least one real attempt.
    NothingTried,
}

impl std::fmt::Display for LedgerError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            LedgerError::NothingTried => write!(
                f,
                "a blocker cannot be parked before at least one resolution has been attempted"
            ),
        }
    }
}

impl std::error::Error for LedgerError {}

/// Redact secrets from a single string.
///
/// The underlying redactor is a streaming state machine; this drives one
/// complete string through it and flushes, so an incomplete candidate at the
/// end of the input is emitted verbatim rather than swallowed.
fn redact(text: &str) -> String {
    let mut redactor = StreamingSecretRedactor::new();
    let mut out = String::with_capacity(text.len());
    redactor.process(text, &mut out);
    redactor.flush(&mut out);
    out
}

fn redact_all(texts: &[String]) -> Vec<String> {
    texts.iter().map(|t| redact(t)).collect()
}

/// A consistent view of the ledger, taken under a single lock.
///
/// Exists so the terminal-state proof reasons about one moment in time rather
/// than several. See [`AssumptionLedger::snapshot`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LedgerSnapshot {
    parked_items: Vec<WorkItemRef>,
    has_parked: bool,
    goal_level_blockers: usize,
    total: usize,
}

impl LedgerSnapshot {
    /// Whether any blocker is parked at all, against an item or the goal.
    pub fn has_parked_blockers(&self) -> bool {
        self.has_parked
    }

    /// Blockers parked against the goal as a whole rather than a single item.
    pub fn goal_level_blockers(&self) -> usize {
        self.goal_level_blockers
    }

    /// Total entries of every kind.
    pub fn total_entries(&self) -> usize {
        self.total
    }

    /// Whether `original` — the live, unredacted todo text — is parked.
    ///
    /// Matching is by redaction-independent key, so a todo whose text resembles
    /// a credential still joins correctly.
    pub fn is_parked(&self, original: &str) -> bool {
        self.parked_items.iter().any(|item| item.matches(original))
    }

    /// Whether every one of `open_items` is parked behind a blocker.
    ///
    /// This is the subset check at the heart of the `GenuinelyBlocked` proof.
    /// An empty `open_items` is **not** sufficient on its own: a run with no
    /// todos at all has not proven anything, so the caller must also require
    /// [`LedgerSnapshot::has_parked_blockers`].
    pub fn all_parked<'a>(&self, open_items: impl IntoIterator<Item = &'a str>) -> bool {
        let mut saw_one = false;
        for item in open_items {
            saw_one = true;
            if !self.is_parked(item) {
                return false;
            }
        }
        saw_one
    }

    /// The parked items, for rendering the report.
    pub fn parked_items(&self) -> &[WorkItemRef] {
        &self.parked_items
    }
}

/// The session's record of autonomous decisions.
///
/// Shared behind an `Arc` and written from tool threads, so every method takes
/// `&self` and the interior `Mutex` is the only mutable state.
#[derive(Debug, Default)]
pub struct AssumptionLedger {
    entries: Mutex<Vec<LedgerEntry>>,
}

impl AssumptionLedger {
    pub fn new() -> Self {
        Self::default()
    }

    /// Record a question answered on the operator's behalf.
    pub fn record_assumption(
        &self,
        question: impl AsRef<str>,
        options: &[String],
        chosen: impl AsRef<str>,
        rationale: impl AsRef<str>,
        confidence: Confidence,
    ) {
        self.push(LedgerEntry::Assumption {
            question: redact(question.as_ref()),
            options: redact_all(options),
            chosen: redact(chosen.as_ref()),
            rationale: redact(rationale.as_ref()),
            confidence,
        });
    }

    /// Park a branch of work behind a genuine blocker.
    ///
    /// Returns [`LedgerError::NothingTried`] when `tried` is empty or contains
    /// only blank strings. This is the exhaustion rule, and it is a hard error
    /// rather than a warning: a caller that has not tried anything has not
    /// found a blocker, it has found an excuse.
    pub fn park_blocker(
        &self,
        kind: BlockerKind,
        tried: &[String],
        needs: impl AsRef<str>,
        blocks: Option<&str>,
    ) -> Result<(), LedgerError> {
        let tried: Vec<String> = tried
            .iter()
            .filter(|t| !t.trim().is_empty())
            .map(|t| redact(t))
            .collect();
        if tried.is_empty() {
            return Err(LedgerError::NothingTried);
        }
        self.push(LedgerEntry::ParkedBlocker {
            kind,
            tried,
            needs: redact(needs.as_ref()),
            blocks: blocks.map(WorkItemRef::new),
        });
        Ok(())
    }

    /// Record an undo manufactured before an unrecoverable action.
    pub fn record_recovery(
        &self,
        action: impl AsRef<str>,
        undo_ref: Option<&str>,
        error: Option<&str>,
    ) {
        self.push(LedgerEntry::Recovery {
            action: redact(action.as_ref()),
            undo_ref: undo_ref.map(redact),
            error: error.map(redact),
        });
    }

    /// Record an action refused by the permission envelope.
    pub fn record_denial(
        &self,
        tool: impl AsRef<str>,
        mode: impl AsRef<str>,
        needed_mode: impl AsRef<str>,
    ) {
        self.push(LedgerEntry::Denial {
            tool: redact(tool.as_ref()),
            mode: redact(mode.as_ref()),
            needed_mode: redact(needed_mode.as_ref()),
        });
    }

    /// A single consistent view of everything the terminal-state proof needs.
    ///
    /// Taken under one lock. The proof asks several questions at once, and
    /// answering them through separate accessors would let a tool thread write
    /// between two of them and produce a torn view — one that reports a parked
    /// blocker exists while the matching work item is still missing. Deciding
    /// to stop a run on a state that never actually held is not a risk worth
    /// carrying for the sake of a few convenience methods.
    pub fn snapshot(&self) -> LedgerSnapshot {
        let entries = self.lock();
        let mut parked_items = Vec::new();
        let mut has_parked = false;
        let mut goal_level_blockers = 0usize;

        for entry in entries.iter() {
            if let LedgerEntry::ParkedBlocker { blocks, .. } = entry {
                has_parked = true;
                match blocks {
                    Some(item) => parked_items.push(item.clone()),
                    None => goal_level_blockers += 1,
                }
            }
        }

        LedgerSnapshot { parked_items, has_parked, goal_level_blockers, total: entries.len() }
    }

    /// Every entry, in the order recorded.
    pub fn entries(&self) -> Vec<LedgerEntry> {
        self.lock().clone()
    }

    pub fn len(&self) -> usize {
        self.lock().len()
    }

    pub fn is_empty(&self) -> bool {
        self.lock().is_empty()
    }

    /// Whether anything is parked. The terminal-state proof requires at least
    /// one parked blocker before it will report `GenuinelyBlocked`, so that a
    /// run which simply ran out of ideas is never mislabelled as blocked.
    pub fn has_parked_blockers(&self) -> bool {
        self.snapshot().has_parked
    }

    /// The work items that are parked, for the terminal-state proof.
    ///
    /// Only blockers naming a specific item appear; a blocker against the goal
    /// as a whole has no item to report. Prefer [`AssumptionLedger::snapshot`]
    /// when asking more than one question, so the answers cannot disagree.
    pub fn parked_work_items(&self) -> Vec<WorkItemRef> {
        self.snapshot().parked_items
    }

    /// Assumptions the report should lead with.
    pub fn low_confidence_assumptions(&self) -> Vec<LedgerEntry> {
        self.lock()
            .iter()
            .filter(|e| {
                matches!(e, LedgerEntry::Assumption { confidence, .. }
                    if confidence.needs_review())
            })
            .cloned()
            .collect()
    }

    fn push(&self, entry: LedgerEntry) {
        self.lock().push(entry);
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Vec<LedgerEntry>> {
        self.entries.lock().expect("assumption ledger lock poisoned")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn strings(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| (*s).to_owned()).collect()
    }

    // ── Recording ────────────────────────────────────────────────────────────

    #[test]
    fn an_assumption_is_recorded_with_every_field_intact() {
        let ledger = AssumptionLedger::new();
        ledger.record_assumption(
            "Which database?",
            &strings(&["postgres", "sqlite"]),
            "sqlite",
            "No connection string was configured, so the embedded option is the reversible one.",
            Confidence::Medium,
        );

        assert_eq!(
            ledger.entries(),
            vec![LedgerEntry::Assumption {
                question: "Which database?".into(),
                options: strings(&["postgres", "sqlite"]),
                chosen: "sqlite".into(),
                rationale:
                    "No connection string was configured, so the embedded option is the reversible one."
                        .into(),
                confidence: Confidence::Medium,
            }]
        );
    }

    #[test]
    fn entries_are_returned_in_the_order_they_were_recorded() {
        let ledger = AssumptionLedger::new();
        ledger.record_assumption("first", &strings(&["a"]), "a", "r", Confidence::High);
        ledger.record_denial("run_command", "default", "acceptEdits");
        ledger.record_recovery("force-push", Some("refs/backup/x"), None);

        let entries = ledger.entries();
        assert_eq!(entries.len(), 3);
        assert!(matches!(entries[0], LedgerEntry::Assumption { .. }));
        assert!(matches!(entries[1], LedgerEntry::Denial { .. }));
        assert!(matches!(entries[2], LedgerEntry::Recovery { .. }));
    }

    #[test]
    fn a_recovery_records_the_undo_reference() {
        let ledger = AssumptionLedger::new();
        ledger.record_recovery("git push --force", Some("refs/backup/pre-force"), None);

        assert_eq!(
            ledger.entries(),
            vec![LedgerEntry::Recovery {
                action: "git push --force".into(),
                undo_ref: Some("refs/backup/pre-force".into()),
                error: None,
            }]
        );
    }

    /// The undo may fail and the action proceeds anyway; the ledger is then the
    /// only record that the safety net was missing.
    #[test]
    fn a_recovery_records_why_no_undo_could_be_made() {
        let ledger = AssumptionLedger::new();
        ledger.record_recovery("git push --force", None, Some("no network"));

        assert_eq!(
            ledger.entries(),
            vec![LedgerEntry::Recovery {
                action: "git push --force".into(),
                undo_ref: None,
                error: Some("no network".into()),
            }]
        );
    }

    #[test]
    fn a_denial_records_the_mode_that_would_have_allowed_it() {
        let ledger = AssumptionLedger::new();
        ledger.record_denial("run_command", "plan", "bypassPermissions");

        assert_eq!(
            ledger.entries(),
            vec![LedgerEntry::Denial {
                tool: "run_command".into(),
                mode: "plan".into(),
                needed_mode: "bypassPermissions".into(),
            }]
        );
    }

    // ── The exhaustion rule ──────────────────────────────────────────────────

    #[test]
    fn parking_records_what_was_tried() {
        let ledger = AssumptionLedger::new();
        let result = ledger.park_blocker(
            BlockerKind::MissingCredential,
            &strings(&["read STRIPE_KEY from the environment", "checked the credential store"]),
            "STRIPE_KEY in the environment",
            Some("wire up billing"),
        );

        assert_eq!(result, Ok(()));
        match &ledger.entries()[0] {
            LedgerEntry::ParkedBlocker { kind, tried, needs, blocks } => {
                assert_eq!(*kind, BlockerKind::MissingCredential);
                assert_eq!(
                    tried,
                    &strings(&[
                        "read STRIPE_KEY from the environment",
                        "checked the credential store"
                    ])
                );
                assert_eq!(needs, "STRIPE_KEY in the environment");
                let blocks = blocks.as_ref().expect("the blocker names an item");
                assert_eq!(blocks.display(), "wire up billing");
                assert!(blocks.matches("wire up billing"));
            }
            other => panic!("expected a parked blocker, got {other:?}"),
        }
    }

    /// Without this rule, "blocked" becomes the new "asked": an agent could
    /// park the instant something looked hard and still claim to have finished.
    #[test]
    fn parking_with_nothing_tried_is_refused() {
        let ledger = AssumptionLedger::new();
        let result = ledger.park_blocker(
            BlockerKind::Infeasible,
            &[],
            "a human",
            Some("some work"),
        );

        assert_eq!(result, Err(LedgerError::NothingTried));
        assert!(ledger.is_empty(), "a refused park must leave no trace");
    }

    /// Blank strings are not attempts. Without this, the rule is defeated by
    /// passing `[""]`.
    #[test]
    fn parking_with_only_blank_attempts_is_refused() {
        let ledger = AssumptionLedger::new();
        let result = ledger.park_blocker(
            BlockerKind::Infeasible,
            &strings(&["", "   ", "\t"]),
            "a human",
            None,
        );

        assert_eq!(result, Err(LedgerError::NothingTried));
        assert!(ledger.is_empty());
    }

    #[test]
    fn blank_attempts_are_dropped_but_real_ones_still_park() {
        let ledger = AssumptionLedger::new();
        ledger
            .park_blocker(
                BlockerKind::MissingAccess,
                &strings(&["", "cloned over https", "   "]),
                "ssh key",
                None,
            )
            .expect("a real attempt was made");

        match &ledger.entries()[0] {
            LedgerEntry::ParkedBlocker { tried, .. } => {
                assert_eq!(tried, &strings(&["cloned over https"]));
            }
            other => panic!("expected a parked blocker, got {other:?}"),
        }
    }

    // ── Redaction ────────────────────────────────────────────────────────────

    /// The ledger is summarised back to the model and printed in the report, so
    /// an unredacted entry would be a leak path.
    #[test]
    fn secrets_are_redacted_from_every_assumption_field() {
        let ledger = AssumptionLedger::new();
        ledger.record_assumption(
            "Use sk-abcdefgh12345678 for this?",
            &strings(&["sk-abcdefgh12345678", "no"]),
            "sk-abcdefgh12345678",
            "the key sk-abcdefgh12345678 was present",
            Confidence::Low,
        );

        let rendered = format!("{:?}", ledger.entries());
        assert!(
            !rendered.contains("sk-abcdefgh12345678"),
            "a secret survived into the ledger: {rendered}"
        );
        assert!(rendered.contains("sk-***"), "expected the placeholder: {rendered}");
    }

    #[test]
    fn secrets_are_redacted_from_parked_blockers() {
        let ledger = AssumptionLedger::new();
        ledger
            .park_blocker(
                BlockerKind::MissingCredential,
                &strings(&["tried sk-abcdefgh12345678"]),
                "a key like sk-abcdefgh12345678",
                Some("deploy with sk-abcdefgh12345678"),
            )
            .expect("one attempt was made");

        let rendered = format!("{:?}", ledger.entries());
        assert!(!rendered.contains("sk-abcdefgh12345678"), "{rendered}");
    }

    #[test]
    fn secrets_are_redacted_from_recoveries_and_denials() {
        let ledger = AssumptionLedger::new();
        ledger.record_recovery(
            "curl -H 'Bearer aaaaaaaaaaaaaaaaaaaaaaaa'",
            Some("sk-abcdefgh12345678"),
            Some("failed using sk-abcdefgh12345678"),
        );
        ledger.record_denial("sk-abcdefgh12345678", "default", "yolo");

        let rendered = format!("{:?}", ledger.entries());
        assert!(!rendered.contains("sk-abcdefgh12345678"), "{rendered}");
        assert!(!rendered.contains("aaaaaaaaaaaaaaaaaaaaaaaa"), "{rendered}");
    }

    #[test]
    fn ordinary_text_survives_redaction_unchanged() {
        let ledger = AssumptionLedger::new();
        ledger.record_assumption(
            "Which database should the worker use?",
            &strings(&["postgres", "sqlite"]),
            "sqlite",
            "It is the reversible choice.",
            Confidence::High,
        );

        match &ledger.entries()[0] {
            LedgerEntry::Assumption { question, chosen, rationale, .. } => {
                assert_eq!(question, "Which database should the worker use?");
                assert_eq!(chosen, "sqlite");
                assert_eq!(rationale, "It is the reversible choice.");
            }
            other => panic!("expected an assumption, got {other:?}"),
        }
    }

    // ── Queries used by the terminal-state proof ─────────────────────────────

    #[test]
    fn an_empty_ledger_has_no_parked_blockers() {
        let ledger = AssumptionLedger::new();
        assert!(!ledger.has_parked_blockers());
        assert!(ledger.is_empty());
        assert_eq!(ledger.len(), 0);
    }

    /// Assumptions and denials are not blockers. Only a park is.
    #[test]
    fn non_blocker_entries_do_not_count_as_parked() {
        let ledger = AssumptionLedger::new();
        ledger.record_assumption("q", &strings(&["a"]), "a", "r", Confidence::High);
        ledger.record_denial("run_command", "plan", "default");
        ledger.record_recovery("act", None, None);

        assert!(
            !ledger.has_parked_blockers(),
            "only a ParkedBlocker may satisfy the terminal-state proof"
        );
        assert!(ledger.parked_work_items().is_empty());
    }

    #[test]
    fn parked_work_items_lists_only_blockers_naming_an_item() {
        let ledger = AssumptionLedger::new();
        ledger
            .park_blocker(BlockerKind::MissingAccess, &strings(&["a"]), "n", Some("item one"))
            .unwrap();
        ledger
            .park_blocker(BlockerKind::Infeasible, &strings(&["b"]), "n", None)
            .unwrap();
        ledger
            .park_blocker(BlockerKind::MissingAccess, &strings(&["c"]), "n", Some("item two"))
            .unwrap();

        assert!(ledger.has_parked_blockers());
        let displays: Vec<String> =
            ledger.parked_work_items().iter().map(|i| i.display().to_owned()).collect();
        assert_eq!(displays, strings(&["item one", "item two"]));
    }

    // ── The terminal-state proof's join key ──────────────────────────────────

    /// The join must survive redaction. A todo whose text resembles a
    /// credential is stored redacted for display, but must still match its
    /// live, unredacted self — otherwise the proof silently never fires and
    /// the run cannot terminate.
    #[test]
    fn a_work_item_containing_a_secret_still_matches_its_unredacted_self() {
        let todo = "rotate sk-abcdefgh12345678 in prod";
        let ledger = AssumptionLedger::new();
        ledger
            .park_blocker(BlockerKind::MissingCredential, &strings(&["checked env"]), "a key", Some(todo))
            .unwrap();

        let snapshot = ledger.snapshot();
        assert!(
            snapshot.is_parked(todo),
            "the redacted blocker must still join against the live todo text"
        );

        let rendered = format!("{:?}", ledger.entries());
        assert!(
            !rendered.contains("sk-abcdefgh12345678"),
            "the display text must still be redacted: {rendered}"
        );
    }

    #[test]
    fn work_item_matching_ignores_surrounding_whitespace() {
        let item = WorkItemRef::new("  wire up billing  ");
        assert!(item.matches("wire up billing"));
        assert!(item.matches("\twire up billing\n"));
        assert!(!item.matches("wire up shipping"));
    }

    #[test]
    fn all_parked_is_true_only_when_every_open_item_is_parked() {
        let ledger = AssumptionLedger::new();
        ledger
            .park_blocker(BlockerKind::MissingAccess, &strings(&["a"]), "n", Some("one"))
            .unwrap();
        ledger
            .park_blocker(BlockerKind::MissingAccess, &strings(&["b"]), "n", Some("two"))
            .unwrap();

        let snapshot = ledger.snapshot();
        assert!(snapshot.all_parked(["one", "two"]));
        assert!(!snapshot.all_parked(["one", "two", "three"]));
    }

    /// A run with no work items has proven nothing. Returning `true` for an
    /// empty list would let a goal terminate as "genuinely blocked" without a
    /// single blocker.
    #[test]
    fn all_parked_is_false_when_there_are_no_open_items() {
        let ledger = AssumptionLedger::new();
        ledger
            .park_blocker(BlockerKind::MissingAccess, &strings(&["a"]), "n", Some("one"))
            .unwrap();

        assert!(
            !ledger.snapshot().all_parked(std::iter::empty()),
            "an empty work list must never satisfy the proof"
        );
    }

    #[test]
    fn a_goal_level_blocker_is_counted_but_parks_no_item() {
        let ledger = AssumptionLedger::new();
        ledger
            .park_blocker(BlockerKind::Infeasible, &strings(&["tried everything"]), "a human", None)
            .unwrap();

        let snapshot = ledger.snapshot();
        assert!(snapshot.has_parked_blockers());
        assert_eq!(snapshot.goal_level_blockers(), 1);
        assert!(snapshot.parked_items().is_empty());
        assert!(!snapshot.is_parked("anything"));
    }

    /// The proof reads several facts at once; they must describe one moment.
    #[test]
    fn a_snapshot_is_internally_consistent() {
        let ledger = AssumptionLedger::new();
        ledger.record_assumption("q", &strings(&["a"]), "a", "r", Confidence::High);
        ledger
            .park_blocker(BlockerKind::MissingAccess, &strings(&["a"]), "n", Some("one"))
            .unwrap();

        let snapshot = ledger.snapshot();
        assert_eq!(snapshot.total_entries(), 2);
        assert!(snapshot.has_parked_blockers());
        assert_eq!(snapshot.parked_items().len(), 1);
        assert_eq!(snapshot.goal_level_blockers(), 0);
    }

    #[test]
    fn only_low_confidence_assumptions_are_flagged_for_review() {
        let ledger = AssumptionLedger::new();
        ledger.record_assumption("high", &strings(&["a"]), "a", "r", Confidence::High);
        ledger.record_assumption("medium", &strings(&["a"]), "a", "r", Confidence::Medium);
        ledger.record_assumption("low", &strings(&["a"]), "a", "r", Confidence::Low);

        let flagged = ledger.low_confidence_assumptions();
        assert_eq!(flagged.len(), 1);
        match &flagged[0] {
            LedgerEntry::Assumption { question, .. } => assert_eq!(question, "low"),
            other => panic!("expected an assumption, got {other:?}"),
        }
    }

    #[test]
    fn confidence_review_flag_is_low_only() {
        assert!(Confidence::Low.needs_review());
        assert!(!Confidence::Medium.needs_review());
        assert!(!Confidence::High.needs_review());
    }

    // ── Sharing ──────────────────────────────────────────────────────────────

    /// The ledger is written from tool threads while the loop reads it, so it
    /// must be usable through a shared reference from several threads at once.
    #[test]
    fn the_ledger_is_shareable_across_threads() {
        use std::sync::Arc;

        let ledger = Arc::new(AssumptionLedger::new());
        let mut handles = Vec::new();
        for i in 0..8 {
            let ledger = Arc::clone(&ledger);
            handles.push(std::thread::spawn(move || {
                ledger.record_assumption(
                    format!("question {i}"),
                    &strings(&["a", "b"]),
                    "a",
                    "because",
                    Confidence::High,
                );
            }));
        }
        for h in handles {
            h.join().expect("no writer may panic");
        }

        assert_eq!(ledger.len(), 8, "every concurrent write must be recorded");
    }
}
