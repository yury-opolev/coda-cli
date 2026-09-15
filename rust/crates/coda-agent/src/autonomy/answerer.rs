//! The proxy answerer: deciding a question on the operator's behalf.
//!
//! When a goal is active the operator has said, in effect, "keep working until
//! this is done". Stopping to ask them something breaks that promise just as
//! surely as stopping early does, so under a goal the question seam is
//! answered here instead of being sent to a terminal nobody is watching.
//!
//! ## Why a second model rather than a rule
//!
//! The questions an agent asks are open-ended — which library, which schema,
//! which of two readings of an ambiguous requirement — and no rule table
//! answers those. Shipping products already do this: Claude Code's `auto` mode
//! uses "a second model, the classifier" to approve actions in the operator's
//! place, and AutoGen's `UserProxyAgent` with `human_input_mode="NEVER"` is an
//! LLM standing in for the user.
//!
//! ## Guarding against a mis-calibrated stand-in
//!
//! The research on simulated users is unflattering: they systematically
//! mis-estimate what the real person wanted ("Lost in Simulation", ACL 2026),
//! and models asked to fill a missing argument will cheerfully invent one
//! ("Learning to Ask", arXiv:2409.00557). Two things contain that here.
//!
//! First, **every choice is recorded** in the assumption ledger with its
//! reasoning and confidence, so a wrong call is visible and reversible rather
//! than silent.
//!
//! Second, **low confidence breaks toward reversibility**. Asked to choose
//! without being sure, the answerer is told to prefer the option that
//! preserves the most future choice. A mis-calibrated proxy does least damage
//! when its bias is toward decisions that can be undone.
//!
//! ## A fault is never an answer
//!
//! If the answerer itself cannot be reached, it does **not** guess and it does
//! not fall back to the first option. It parks the question as a blocker and
//! returns [`AnswerOutcome::Parked`], which the tool surfaces as an ordinary
//! recoverable error. The run continues on another branch; the operator sees
//! the unanswered question in the report.

use std::sync::Arc;

use async_trait::async_trait;
use tokio_util::sync::CancellationToken;

use coda_tool::{AnswerOutcome, UserQuestion};

use super::ledger::{AssumptionLedger, BlockerKind, Confidence};
use super::retry::GoalRetryPolicy;
use super::ForkedAgent;

/// System prompt for the stand-in.
pub const SYSTEM_PROMPT: &str = "\
You are standing in for an absent operator. An autonomous coding agent is \
working toward a goal they set, and has paused to ask a question. They are not \
at their desk, and the run must not stall, so you answer on their behalf.

You MUST choose one of the offered options. \"I don't know\" is not available: \
refusing to choose stops work they asked to have finished.

How to choose:
- Prefer what the goal implies. It is the clearest statement of intent you have.
- Follow the project's existing conventions and prior decisions in the transcript.
- When genuinely unsure, choose the option that is EASIEST TO UNDO. A wrong \
reversible choice costs minutes; a wrong irreversible one can cost the work.

Respond in EXACTLY this form, three lines, nothing else:
CHOSEN: <one option, copied verbatim>
CONFIDENCE: <high|medium|low>
WHY: <one sentence, for the operator to read later>";

/// What the stand-in decided.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProxyAnswer {
    pub chosen: String,
    pub confidence: Confidence,
    pub rationale: String,
}

/// Parse the stand-in's reply, resolving the choice against the real options.
///
/// Returns `None` when no offered option can be identified, which is treated as
/// a failure to answer rather than as licence to pick one. A reply that cannot
/// be tied to a real option is not a decision.
pub fn parse_answer(response: &str, options: &[String]) -> Option<ProxyAnswer> {
    let mut chosen_line = None;
    let mut confidence = Confidence::Medium;
    let mut rationale = String::new();

    for line in response.lines() {
        let line = line.trim();
        if let Some(rest) = strip_prefix_ci(line, "CHOSEN:") {
            chosen_line = Some(rest.trim().to_owned());
        } else if let Some(rest) = strip_prefix_ci(line, "CONFIDENCE:") {
            confidence = match rest.trim().to_ascii_lowercase().as_str() {
                "high" => Confidence::High,
                "low" => Confidence::Low,
                _ => Confidence::Medium,
            };
        } else if let Some(rest) = strip_prefix_ci(line, "WHY:") {
            rationale = rest.trim().to_owned();
        }
    }

    let chosen = resolve_option(chosen_line.as_deref()?, options)?;
    if rationale.is_empty() {
        rationale = "No reason was given.".to_owned();
    }
    Some(ProxyAnswer { chosen, confidence, rationale })
}

/// Match the model's text against the offered options.
///
/// Exact match, then case-insensitive. Nothing else.
///
/// There is deliberately no fuzzy or substring fallback. Substring matching
/// reads `"Do not delete"` as `Delete` and `"I would rather not deploy"` as
/// `Deploy` — it inverts the stand-in's meaning and returns the result as a
/// confident answer, which is the single most dangerous thing this module
/// could do. It also silently resolves plain hallucinations (`"apple"` against
/// `["a", "b"]`) to whichever option shares a few letters, usually the first —
/// exactly the "never the first option" rule the question seam was built to
/// enforce.
///
/// The system prompt requires the choice to be copied verbatim. A reply that
/// cannot be matched exactly has not chosen, and the honest response to that is
/// to park the question rather than to guess what was meant.
fn resolve_option(text: &str, options: &[String]) -> Option<String> {
    let text = text.trim().trim_matches(['"', '`', '\'']).trim();
    if text.is_empty() {
        return None;
    }
    if let Some(exact) = options.iter().find(|o| o.as_str() == text) {
        return Some(exact.clone());
    }
    options.iter().find(|o| o.eq_ignore_ascii_case(text)).cloned()
}

fn strip_prefix_ci<'a>(line: &'a str, prefix: &str) -> Option<&'a str> {
    let bytes = line.as_bytes();
    let pb = prefix.as_bytes();
    if bytes.len() >= pb.len() && bytes[..pb.len()].eq_ignore_ascii_case(pb) {
        Some(&line[pb.len()..])
    } else {
        None
    }
}

/// Build the message describing the question to the stand-in.
pub fn build_user_message(
    goal: &str,
    question: &str,
    options: &[String],
    transcript: &str,
    conventions: &str,
) -> String {
    let rendered: Vec<String> =
        options.iter().enumerate().map(|(i, o)| format!("{}. {o}", i + 1)).collect();

    let mut msg = format!(
        "The operator's goal:\n{goal}\n\n\
         The agent is asking:\n{question}\n\n\
         The options:\n{}\n",
        rendered.join("\n")
    );
    if !conventions.trim().is_empty() {
        msg.push_str(&format!("\nProject conventions:\n{conventions}\n"));
    }
    if !transcript.trim().is_empty() {
        msg.push_str(&format!("\nWhat has happened so far:\n{transcript}\n"));
    }
    msg.push_str("\nChoose one option.");
    msg
}

/// Answers questions in the operator's place while a goal is active.
pub struct ProxyAnswerer {
    judge: Arc<dyn ForkedAgent>,
    ledger: Arc<AssumptionLedger>,
    goal: String,
    conventions: String,
    retry: GoalRetryPolicy,
    /// Most recent transcript text, refreshed by the loop each turn.
    transcript: std::sync::Mutex<String>,
}

impl ProxyAnswerer {
    /// Build a stand-in.
    ///
    /// `ledger` must be the supervisor's own handle — [`AutonomySupervisor::ledger`]
    /// — and never a freshly constructed one. A separate ledger would accept
    /// every write and surface none of them: the end-of-run report would show no
    /// assumptions, and the termination proof would never see the parks it needs
    /// to conclude anything.
    ///
    /// [`AutonomySupervisor::ledger`]: super::AutonomySupervisor::ledger
    pub fn new(
        judge: Arc<dyn ForkedAgent>,
        ledger: Arc<AssumptionLedger>,
        goal: impl Into<String>,
    ) -> Self {
        Self {
            judge,
            ledger,
            goal: goal.into(),
            conventions: String::new(),
            retry: GoalRetryPolicy::default(),
            transcript: std::sync::Mutex::new(String::new()),
        }
    }

    pub fn with_conventions(mut self, conventions: impl Into<String>) -> Self {
        self.conventions = conventions.into();
        self
    }

    pub fn with_retry(mut self, retry: GoalRetryPolicy) -> Self {
        self.retry = retry;
        self
    }

    /// Update the transcript the stand-in sees. Called by the loop as the run
    /// progresses, so a question late in a run is answered with the context of
    /// everything decided before it.
    pub fn set_transcript(&self, transcript: impl Into<String>) {
        *self.transcript.lock().expect("transcript lock poisoned") = transcript.into();
    }

    fn transcript(&self) -> String {
        self.transcript.lock().expect("transcript lock poisoned").clone()
    }

    /// Park an unanswerable question and produce the outcome the tool turns
    /// into a recoverable error.
    ///
    /// The ledger write can itself be refused — the exhaustion rule requires a
    /// real attempt — so the attempt is stated explicitly rather than assumed.
    fn park(&self, question: &str, detail: &str) -> AnswerOutcome {
        let tried = vec![format!(
            "asked the stand-in decision-maker to choose on the operator's behalf ({detail})"
        )];
        let _ = self.ledger.park_blocker(
            BlockerKind::Infeasible,
            &tried,
            "an operator to answer this question",
            Some(question),
        );
        AnswerOutcome::Parked { reason: detail.to_owned() }
    }
}

#[async_trait]
impl UserQuestion for ProxyAnswerer {
    async fn ask(
        &self,
        question: &str,
        options: &[String],
        _multi_select: bool,
        cancel: CancellationToken,
    ) -> AnswerOutcome {
        if options.is_empty() {
            return self.park(question, "no options were offered");
        }

        let message = build_user_message(
            &self.goal,
            question,
            options,
            &self.transcript(),
            &self.conventions,
        );
        let messages = vec![coda_llm::Message::user(message)];

        let judge = Arc::clone(&self.judge);
        let result = self
            .retry
            .run(
                |ct| {
                    let msgs = messages.clone();
                    let judge = Arc::clone(&judge);
                    async move { judge.run(SYSTEM_PROMPT, msgs, ct).await }
                },
                cancel,
            )
            .await;

        let response = match result {
            Ok((true, Some(response))) => response,
            // Cancelled, exhausted, or an empty success. None of these is a
            // decision, and guessing here is exactly the fabrication the whole
            // design forbids.
            _ => return self.park(question, "the stand-in decision-maker was unreachable"),
        };

        let Some(answer) = parse_answer(&response, options) else {
            return self.park(question, "the stand-in did not choose one of the offered options");
        };

        self.ledger.record_assumption(
            question,
            options,
            &answer.chosen,
            &answer.rationale,
            answer.confidence,
        );
        AnswerOutcome::Answered(answer.chosen)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_llm::Message;

    fn opts(items: &[&str]) -> Vec<String> {
        items.iter().map(|s| (*s).to_owned()).collect()
    }

    struct Scripted(std::sync::Mutex<Vec<String>>);

    impl Scripted {
        fn new(responses: &[&str]) -> Arc<Self> {
            Arc::new(Self(std::sync::Mutex::new(
                responses.iter().rev().map(|s| (*s).to_owned()).collect(),
            )))
        }
    }

    #[async_trait]
    impl ForkedAgent for Scripted {
        async fn run(
            &self,
            _: &str,
            _: Vec<Message>,
            _: CancellationToken,
        ) -> anyhow::Result<String> {
            Ok(self.0.lock().unwrap().pop().unwrap_or_default())
        }
    }

    struct AlwaysFails;

    #[async_trait]
    impl ForkedAgent for AlwaysFails {
        async fn run(
            &self,
            _: &str,
            _: Vec<Message>,
            _: CancellationToken,
        ) -> anyhow::Result<String> {
            Err(anyhow::anyhow!("unreachable"))
        }
    }

    fn answerer(judge: Arc<dyn ForkedAgent>) -> (ProxyAnswerer, Arc<AssumptionLedger>) {
        let ledger = Arc::new(AssumptionLedger::new());
        let a = ProxyAnswerer::new(judge, Arc::clone(&ledger), "ship the feature")
            .with_retry(GoalRetryPolicy::for_tests());
        (a, ledger)
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    #[test]
    fn a_well_formed_reply_is_parsed_in_full() {
        let parsed = parse_answer(
            "CHOSEN: sqlite\nCONFIDENCE: high\nWHY: it needs no server",
            &opts(&["postgres", "sqlite"]),
        )
        .expect("a well-formed reply parses");

        assert_eq!(parsed.chosen, "sqlite");
        assert_eq!(parsed.confidence, Confidence::High);
        assert_eq!(parsed.rationale, "it needs no server");
    }

    #[test]
    fn parsing_is_insensitive_to_label_case_and_spacing() {
        let parsed = parse_answer(
            "  chosen:   sqlite  \n  Confidence: LOW \n  why:  simpler  ",
            &opts(&["postgres", "sqlite"]),
        )
        .expect("parses");
        assert_eq!(parsed.chosen, "sqlite");
        assert_eq!(parsed.confidence, Confidence::Low);
    }

    #[test]
    fn quotes_and_backticks_around_the_choice_are_ignored() {
        for raw in ["\"sqlite\"", "`sqlite`", "'sqlite'"] {
            let parsed = parse_answer(
                &format!("CHOSEN: {raw}\nCONFIDENCE: high\nWHY: x"),
                &opts(&["postgres", "sqlite"]),
            )
            .unwrap_or_else(|| panic!("{raw} should parse"));
            assert_eq!(parsed.chosen, "sqlite");
        }
    }

    /// The stored choice must be the option as offered, not as the model
    /// happened to spell it, or nothing downstream can compare against it.
    #[test]
    fn a_case_mismatched_choice_resolves_to_the_offered_spelling() {
        let parsed = parse_answer(
            "CHOSEN: SQLite\nCONFIDENCE: high\nWHY: x",
            &opts(&["Postgres", "sqlite"]),
        )
        .expect("parses");
        assert_eq!(parsed.chosen, "sqlite");
    }

    #[test]
    fn an_unrecognised_confidence_is_treated_as_medium() {
        let parsed =
            parse_answer("CHOSEN: a\nCONFIDENCE: banana\nWHY: x", &opts(&["a"])).expect("parses");
        assert_eq!(parsed.confidence, Confidence::Medium);
    }

    #[test]
    fn a_missing_reason_is_recorded_as_absent_rather_than_blank() {
        let parsed = parse_answer("CHOSEN: a\nCONFIDENCE: high", &opts(&["a"])).expect("parses");
        assert_eq!(parsed.rationale, "No reason was given.");
    }

    /// A reply naming something that was never offered is not a decision.
    #[test]
    fn a_choice_outside_the_offered_options_does_not_parse() {
        assert!(parse_answer(
            "CHOSEN: mysql\nCONFIDENCE: high\nWHY: x",
            &opts(&["postgres", "sqlite"])
        )
        .is_none());
    }

    #[test]
    fn a_reply_with_no_choice_line_does_not_parse() {
        assert!(parse_answer("I think sqlite is best", &opts(&["postgres", "sqlite"])).is_none());
        assert!(parse_answer("", &opts(&["a"])).is_none());
        assert!(parse_answer("CHOSEN:   \nWHY: x", &opts(&["a"])).is_none());
    }

    /// Text matching two options has not chosen between them; picking either
    /// would be invention.
    #[test]
    fn an_ambiguous_choice_matching_two_options_does_not_parse() {
        assert!(parse_answer(
            "CHOSEN: use\nCONFIDENCE: high\nWHY: x",
            &opts(&["use postgres", "use sqlite"])
        )
        .is_none());
    }

    /// SECURITY: the most dangerous thing this module could do is read a
    /// refusal as consent. Substring matching did exactly that — "Do not
    /// delete" contains "delete" — and returned it as a confident answer.
    #[test]
    fn a_reply_that_negates_an_option_never_resolves_to_that_option() {
        for (reply, options) in [
            ("Do not delete", opts(&["Delete", "Keep"])),
            ("I would rather not deploy", opts(&["Deploy", "Hold"])),
            ("neither, please stop", opts(&["Delete", "Keep"])),
        ] {
            let parsed =
                parse_answer(&format!("CHOSEN: {reply}\nCONFIDENCE: high\nWHY: x"), &options);
            assert!(
                parsed.is_none(),
                "{reply:?} must park, not resolve to an option: {parsed:?}"
            );
        }
    }

    /// SECURITY: prose that merely shares letters with an option is not a
    /// choice. Each of these used to resolve — usually to the first option,
    /// the one outcome the question seam must never produce by accident.
    #[test]
    fn incidental_letter_overlap_never_resolves_to_an_option() {
        for (reply, options) in [
            ("yesterday's approach", opts(&["Yes", "No"])),
            ("another option entirely", opts(&["Yes", "No"])),
            ("apple", opts(&["a", "b"])),
        ] {
            let parsed =
                parse_answer(&format!("CHOSEN: {reply}\nCONFIDENCE: high\nWHY: x"), &options);
            assert!(
                parsed.is_none(),
                "{reply:?} is not a choice: {parsed:?}"
            );
        }
    }

    // ── Prompt construction ──────────────────────────────────────────────────

    #[test]
    fn the_message_carries_the_goal_question_and_numbered_options() {
        let msg = build_user_message(
            "ship the feature",
            "Which database?",
            &opts(&["postgres", "sqlite"]),
            "",
            "",
        );
        assert!(msg.contains("ship the feature"), "{msg}");
        assert!(msg.contains("Which database?"), "{msg}");
        assert!(msg.contains("1. postgres"), "{msg}");
        assert!(msg.contains("2. sqlite"), "{msg}");
    }

    #[test]
    fn conventions_and_transcript_are_included_only_when_present() {
        let bare = build_user_message("g", "q", &opts(&["a"]), "", "");
        assert!(!bare.contains("Project conventions"), "{bare}");
        assert!(!bare.contains("What has happened so far"), "{bare}");

        let full = build_user_message("g", "q", &opts(&["a"]), "did a thing", "use tabs");
        assert!(full.contains("use tabs"), "{full}");
        assert!(full.contains("did a thing"), "{full}");
    }

    /// The instruction to break ties toward reversibility is the main guard
    /// against a mis-calibrated stand-in, so it must actually be in the prompt.
    #[test]
    fn the_system_prompt_demands_a_choice_and_biases_toward_reversibility() {
        assert!(SYSTEM_PROMPT.contains("MUST choose"), "{SYSTEM_PROMPT}");
        assert!(SYSTEM_PROMPT.contains("EASIEST TO UNDO"), "{SYSTEM_PROMPT}");
        assert!(SYSTEM_PROMPT.contains("not available"), "{SYSTEM_PROMPT}");
    }

    // ── Answering ────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn a_question_is_answered_and_recorded() {
        let (a, ledger) = answerer(Scripted::new(&[
            "CHOSEN: sqlite\nCONFIDENCE: high\nWHY: no server needed",
        ]));

        let outcome = a
            .ask("Which database?", &opts(&["postgres", "sqlite"]), false, CancellationToken::new())
            .await;

        assert_eq!(outcome, AnswerOutcome::Answered("sqlite".into()));
        match &ledger.entries()[0] {
            crate::autonomy::LedgerEntry::Assumption { chosen, rationale, confidence, .. } => {
                assert_eq!(chosen, "sqlite");
                assert_eq!(rationale, "no server needed");
                assert_eq!(*confidence, Confidence::High);
            }
            other => panic!("expected an assumption, got {other:?}"),
        }
    }

    /// The whole point: under a goal, asking never suspends the run.
    #[tokio::test]
    async fn answering_never_reports_a_no_answer_fault() {
        let cases: Vec<Arc<dyn ForkedAgent>> = vec![
            Arc::new(AlwaysFails),
            Scripted::new(&[""]),
            Scripted::new(&["total nonsense"]),
            Scripted::new(&["CHOSEN: something never offered\nCONFIDENCE: high\nWHY: x"]),
            // Prose that shares letters with an option but chooses nothing.
            Scripted::new(&["CHOSEN: do not pick a\nCONFIDENCE: high\nWHY: x"]),
            // A reply in the wrong shape entirely.
            Scripted::new(&["{\"chosen\": \"a\"}"]),
        ];
        for judge in cases {
            let (a, _) = answerer(judge);
            let outcome = a
                .ask("Which?", &opts(&["a", "b"]), false, CancellationToken::new())
                .await;
            assert!(
                outcome.no_answer_reason().is_none(),
                "a goal run must never surface a NoAnswer fault: {outcome:?}"
            );
            assert!(matches!(outcome, AnswerOutcome::Parked { .. }), "{outcome:?}");
            assert_ne!(
                outcome.answered(),
                Some("a"),
                "SECURITY: no failure path may select the first option"
            );
        }
    }

    /// Cancellation must reach the stand-in and park, not hang and not guess.
    #[tokio::test]
    async fn a_cancelled_question_parks_rather_than_hanging_or_guessing() {
        let (a, _) = answerer(Arc::new(AlwaysFails));
        let cancel = CancellationToken::new();
        cancel.cancel();

        let outcome = a.ask("Which?", &opts(&["a", "b"]), false, cancel).await;
        assert!(matches!(outcome, AnswerOutcome::Parked { .. }), "{outcome:?}");
        assert_eq!(outcome.answered(), None);
    }

    /// The failure that would be most tempting to paper over: if the stand-in
    /// is unreachable, picking the first option would look like an answer and
    /// silently commit the operator to something they never chose.
    #[tokio::test]
    async fn an_unreachable_stand_in_parks_rather_than_guessing() {
        let (a, ledger) = answerer(Arc::new(AlwaysFails));

        let outcome = a
            .ask("Delete production?", &opts(&["Delete", "Keep"]), false, CancellationToken::new())
            .await;

        assert!(matches!(outcome, AnswerOutcome::Parked { .. }), "{outcome:?}");
        assert_eq!(outcome.answered(), None, "a park must never read as an answer");
        assert!(ledger.has_parked_blockers(), "the question must be recorded as a blocker");
        assert!(
            ledger.snapshot().is_parked("Delete production?"),
            "the park must name the question it could not settle"
        );
    }

    #[tokio::test]
    async fn no_options_at_all_parks_immediately() {
        let (a, ledger) = answerer(Scripted::new(&["CHOSEN: a\nCONFIDENCE: high\nWHY: x"]));
        let outcome = a.ask("Which?", &[], false, CancellationToken::new()).await;
        assert!(matches!(outcome, AnswerOutcome::Parked { .. }), "{outcome:?}");
        assert!(ledger.has_parked_blockers());
    }

    /// A parked question must leave no assumption behind: nothing was decided.
    #[tokio::test]
    async fn a_parked_question_records_no_assumption() {
        let (a, ledger) = answerer(Arc::new(AlwaysFails));
        a.ask("Which?", &opts(&["a", "b"]), false, CancellationToken::new()).await;

        let assumed = ledger
            .entries()
            .into_iter()
            .any(|e| matches!(e, crate::autonomy::LedgerEntry::Assumption { .. }));
        assert!(!assumed, "nothing was decided, so nothing may be recorded as decided");
    }

    #[tokio::test]
    async fn a_low_confidence_choice_is_flagged_for_the_operator() {
        let (a, ledger) =
            answerer(Scripted::new(&["CHOSEN: b\nCONFIDENCE: low\nWHY: little to go on"]));
        a.ask("Which?", &opts(&["a", "b"]), false, CancellationToken::new()).await;

        assert_eq!(
            ledger.low_confidence_assumptions().len(),
            1,
            "an unsure choice must surface in the report"
        );
    }

    #[tokio::test]
    async fn the_transcript_is_passed_to_the_stand_in() {
        let (a, _) = answerer(Scripted::new(&["CHOSEN: a\nCONFIDENCE: high\nWHY: x"]));
        a.set_transcript("we already chose tabs over spaces");
        let msg = build_user_message("g", "q", &opts(&["a"]), &a.transcript(), "");
        assert!(msg.contains("tabs over spaces"), "{msg}");
    }
}
