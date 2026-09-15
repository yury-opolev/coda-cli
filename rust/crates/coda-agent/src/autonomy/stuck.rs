//! Stuck detection: noticing that the agent has stopped making progress.
//!
//! An autonomous run cannot lean on a budget to stop it — the operator may have
//! set no ceiling at all — so something has to recognise a loop from the
//! inside. That is this module's job, and it is what guarantees termination.
//!
//! The heuristics and thresholds are taken from OpenHands' `StuckDetector`,
//! which is the most battle-tested published implementation of this idea. They
//! are deliberately *syntactic*: asking a model "are you stuck?" is unreliable
//! precisely when it matters, because a model confidently looping is not
//! introspecting. Counting repeats is dumb and works.
//!
//! ## What counts as the same action
//!
//! Equality is **semantic**, not identity. Two calls are the same when the tool
//! name, the arguments and the accompanying reasoning match. Call ids, result
//! ids and timestamps differ on every attempt and would make every repeat look
//! novel, which is the one thing that would render the whole module useless.
//!
//! ## Nudge before stop
//!
//! Tripping a threshold does not end the run. The first trip injects a
//! corrective nudge — telling the agent plainly that it has repeated itself and
//! that repeating again will not help — and only a streak that survives the
//! nudge marks the branch stuck. Self-correction is cheap; stopping is not.

use std::collections::VecDeque;

/// How many recent events are considered. Mirrors OpenHands' window.
const WINDOW: usize = 20;

/// Identical action *and* identical observation this many times.
const REPEAT_THRESHOLD: usize = 4;

/// Identical action erroring identically **more** than this many times.
///
/// Strictly greater, matching OpenHands: three identical failures is a model
/// trying a reasonable thing and being wrong, which is ordinary. The fourth is
/// a loop.
const ERROR_THRESHOLD: usize = 3;

/// Consecutive assistant turns with no tool call at all.
const MONOLOGUE_THRESHOLD: usize = 3;

/// Longest repeating cycle recognised, in events.
///
/// The spec's heuristic was `[A,B,A,B,A,B]` — a period-2 cycle. That leaves a
/// real hang uncovered: an agent rotating through three or four tools forever
/// trips nothing, and with no budget ceiling nothing else would ever stop it.
/// Generalising to period-k costs one loop and closes the gap.
const MAX_CYCLE_PERIOD: usize = 4;

/// How many full repetitions of a cycle before it counts as stuck.
///
/// Three, so a period-2 cycle still trips at six events exactly as the original
/// `[A,B,A,B,A,B]` rule did.
const CYCLE_REPETITIONS: usize = 3;

/// One thing the agent did, and what came back.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StuckObservation {
    /// The tool invoked.
    pub tool_name: String,
    /// Arguments, verbatim as the model produced them.
    pub input_json: String,
    /// Reasoning that accompanied the call, when the model supplied any.
    pub thought: String,
    /// What the tool returned.
    pub result_content: String,
    /// Whether the tool reported failure.
    pub is_error: bool,
}

impl StuckObservation {
    pub fn new(
        tool_name: impl Into<String>,
        input_json: impl Into<String>,
        result_content: impl Into<String>,
        is_error: bool,
    ) -> Self {
        Self {
            tool_name: tool_name.into(),
            input_json: input_json.into(),
            thought: String::new(),
            result_content: result_content.into(),
            is_error,
        }
    }

    pub fn with_thought(mut self, thought: impl Into<String>) -> Self {
        self.thought = thought.into();
        self
    }

    /// Whether two calls are the same *action*, ignoring what came back.
    fn same_action(&self, other: &Self) -> bool {
        self.tool_name == other.tool_name
            && self.input_json == other.input_json
            && self.thought == other.thought
    }

    /// Whether two calls are the same action **and** produced the same result.
    fn same_action_and_observation(&self, other: &Self) -> bool {
        self.same_action(other)
            && self.result_content == other.result_content
            && self.is_error == other.is_error
    }
}

/// What kind of loop was recognised.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum StuckPattern {
    /// The same call returning the same answer, over and over.
    RepeatedActionObservation,
    /// The same call failing the same way, over and over.
    RepeatedActionError,
    /// Talking without acting.
    Monologue,
    /// A repeating cycle of two to four distinct events, going nowhere.
    ///
    /// Generalises the original `[A,B,A,B,A,B]` ping-pong. Monologues count as
    /// events here, so `[A, think, A, think, ...]` — which evades every
    /// action-only heuristic — is recognised too.
    Cyclic,
}

impl StuckPattern {
    pub fn as_str(self) -> &'static str {
        match self {
            StuckPattern::RepeatedActionObservation => "repeatedActionObservation",
            StuckPattern::RepeatedActionError => "repeatedActionError",
            StuckPattern::Monologue => "monologue",
            StuckPattern::Cyclic => "cyclic",
        }
    }

    /// The corrective nudge injected the first time this pattern trips.
    fn nudge(self, detail: &str) -> String {
        match self {
            StuckPattern::RepeatedActionObservation => format!(
                "You have called {detail} with the same arguments several times and received \
                 the same result each time. Repeating it will not produce anything new — treat \
                 that result as final and move on to a different part of the task."
            ),
            StuckPattern::RepeatedActionError => format!(
                "You have called {detail} with the same arguments several times in a row and it \
                 failed every time. Repeating the exact same call again will not work — review \
                 the error and either correct the arguments or try a different approach."
            ),
            StuckPattern::Monologue => {
                "You have produced several turns in a row without using a tool. If you are \
                 working, take the next concrete action; if you are blocked, record what you \
                 tried and move to a different part of the task."
                    .to_owned()
            }
            StuckPattern::Cyclic => format!(
                "You are cycling through {detail} without making progress. Break the cycle: \
                 either accept the current state and move on, or try an approach you have not \
                 attempted yet."
            ),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum Event {
    Action(StuckObservation),
    /// An assistant turn that called no tool.
    Monologue,
}

impl Event {
    /// Whether two events are the same *step*, for cycle detection. Two
    /// monologues are interchangeable; two actions match on identity.
    fn same_step(&self, other: &Event) -> bool {
        match (self, other) {
            (Event::Monologue, Event::Monologue) => true,
            (Event::Action(a), Event::Action(b)) => a.same_action(b),
            _ => false,
        }
    }
}

/// A nudge already delivered, and for which streak.
///
/// Keyed by the offending action as well as the pattern. Recording only the
/// pattern would mean the *second* error loop in a long run — a different tool,
/// a different cause — silently received no nudge and was declared stuck
/// immediately, because its variant was already on the list. Over hours that
/// erodes the "correct once before stopping" guarantee to nothing.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Nudged {
    pattern: StuckPattern,
    fingerprint: u64,
}

/// Recognises loops in the recent history of a run.
///
/// Owned by the autonomy supervisor. Uses interior mutability because the
/// supervisor is shared behind an `Arc` while the loop drives it.
#[derive(Debug, Default)]
pub struct StuckDetector {
    inner: std::sync::Mutex<Inner>,
}

#[derive(Debug, Default)]
struct Inner {
    window: VecDeque<Event>,
    /// Streaks already nudged for, so the nudge fires once per streak and
    /// escalation is reserved for one that survived it.
    nudged: Vec<Nudged>,
}

impl StuckDetector {
    pub fn new() -> Self {
        Self::default()
    }

    /// Record a tool call and its result.
    pub fn observe(&self, observation: StuckObservation) {
        self.push(Event::Action(observation));
    }

    /// Record an assistant turn that called no tool.
    pub fn observe_monologue(&self) {
        self.push(Event::Monologue);
    }

    /// Forget everything. Called when the operator speaks: a new instruction
    /// means the history before it says nothing about whether the agent is
    /// making progress now.
    pub fn reset(&self) {
        let mut inner = self.lock();
        inner.window.clear();
        inner.nudged.clear();
    }

    /// The pattern currently visible, if any.
    pub fn detect(&self) -> Option<StuckPattern> {
        self.lock().detect()
    }

    /// Whether the run should be treated as stuck.
    ///
    /// True only once *this* streak has survived its own corrective nudge, so
    /// an agent that self-corrects is never penalised for the attempt that
    /// prompted it, and a genuinely new loop always gets its own warning first.
    pub fn is_stuck(&self) -> bool {
        let inner = self.lock();
        match inner.detect() {
            Some(pattern) => inner.nudged.contains(&inner.nudged_key(pattern)),
            None => false,
        }
    }

    /// Take the one-shot corrective nudge for the current streak.
    ///
    /// Returns `Some` the first time a given streak is seen and `None` on every
    /// subsequent call for that same streak, so the agent is told once and then
    /// judged on whether it listened.
    pub fn take_nudge(&self) -> Option<String> {
        let mut inner = self.lock();
        let pattern = inner.detect()?;
        let key = inner.nudged_key(pattern);
        if inner.nudged.contains(&key) {
            return None;
        }
        inner.nudged.push(key);
        let detail = inner.detail_for(pattern);
        Some(pattern.nudge(&detail))
    }

    fn push(&self, event: Event) {
        let mut inner = self.lock();
        inner.window.push_back(event);
        while inner.window.len() > WINDOW {
            inner.window.pop_front();
        }
        // Breaking out of every loop restores grace: the next loop, whenever it
        // comes, deserves its own warning rather than immediate escalation.
        if inner.detect().is_none() {
            inner.nudged.clear();
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Inner> {
        self.inner.lock().expect("stuck detector lock poisoned")
    }
}

impl Inner {
    /// Check every heuristic. Order matters only for which nudge is shown
    /// first; any hit means the same thing.
    fn detect(&self) -> Option<StuckPattern> {
        if self.repeated_action_error() {
            return Some(StuckPattern::RepeatedActionError);
        }
        if self.repeated_action_observation() {
            return Some(StuckPattern::RepeatedActionObservation);
        }
        if self.monologue() {
            return Some(StuckPattern::Monologue);
        }
        if self.cycle_period().is_some() {
            return Some(StuckPattern::Cyclic);
        }
        None
    }

    /// Identify the current streak, so a nudge is remembered against the
    /// specific loop it was issued for rather than against the whole category.
    fn nudged_key(&self, pattern: StuckPattern) -> Nudged {
        use std::hash::{Hash, Hasher};
        let mut hasher = std::collections::hash_map::DefaultHasher::new();
        match pattern {
            // Keyed by the offending call, so a different tool looping later
            // is a different streak deserving its own warning.
            StuckPattern::RepeatedActionError | StuckPattern::RepeatedActionObservation => {
                if let Some(action) = self.trailing_actions().last() {
                    action.tool_name.hash(&mut hasher);
                    action.input_json.hash(&mut hasher);
                    action.thought.hash(&mut hasher);
                }
            }
            // Keyed by the shape of the cycle.
            StuckPattern::Cyclic => {
                if let Some(period) = self.cycle_period() {
                    period.hash(&mut hasher);
                    for event in self.window.iter().rev().take(period) {
                        match event {
                            Event::Action(a) => {
                                a.tool_name.hash(&mut hasher);
                                a.input_json.hash(&mut hasher);
                            }
                            Event::Monologue => "monologue".hash(&mut hasher),
                        }
                    }
                }
            }
            // There is only ever one way to be silent.
            StuckPattern::Monologue => "monologue".hash(&mut hasher),
        }
        Nudged { pattern, fingerprint: hasher.finish() }
    }

    /// The trailing run of actions, most recent last. Stops at a monologue:
    /// a turn without a tool call breaks any repeat streak.
    fn trailing_actions(&self) -> Vec<&StuckObservation> {
        let mut actions = Vec::new();
        for event in self.window.iter().rev() {
            match event {
                Event::Action(a) => actions.push(a),
                Event::Monologue => break,
            }
        }
        actions.reverse();
        actions
    }

    fn repeated_action_observation(&self) -> bool {
        let actions = self.trailing_actions();
        let Some(reference) = actions.last() else { return false };
        let streak = actions
            .iter()
            .rev()
            .take_while(|a| a.same_action_and_observation(reference))
            .count();
        streak >= REPEAT_THRESHOLD
    }

    /// Strictly greater than the threshold, matching OpenHands: three identical
    /// failures is a reasonable thing tried and found wrong; the fourth is a loop.
    fn repeated_action_error(&self) -> bool {
        let actions = self.trailing_actions();
        let Some(reference) = actions.last() else { return false };
        if !reference.is_error {
            return false;
        }
        let streak = actions
            .iter()
            .rev()
            .take_while(|a| a.is_error && a.same_action(reference))
            .count();
        streak > ERROR_THRESHOLD
    }

    fn monologue(&self) -> bool {
        self.window
            .iter()
            .rev()
            .take_while(|e| matches!(e, Event::Monologue))
            .count()
            >= MONOLOGUE_THRESHOLD
    }

    /// The period of the repeating cycle at the tail, if there is one.
    ///
    /// Runs over the *event* window rather than the action window, so a cycle
    /// that launders itself through a thinking turn — `[A, think, A, think]` —
    /// is caught. That shape evades every action-only heuristic, because a
    /// monologue breaks each action streak, and it is a genuine hang.
    ///
    /// The shortest period wins, so a period-2 cycle is never reported as a
    /// period-4 one.
    fn cycle_period(&self) -> Option<usize> {
        let events: Vec<&Event> = self.window.iter().collect();
        for period in 2..=MAX_CYCLE_PERIOD {
            let needed = period * CYCLE_REPETITIONS;
            if events.len() < needed {
                continue;
            }
            let tail = &events[events.len() - needed..];

            let repeats = (0..needed).all(|i| tail[i].same_step(tail[i % period]));
            if !repeats {
                continue;
            }
            // A "cycle" whose every step is identical is just a repeat, and the
            // repeat heuristics describe it better.
            let has_variety = (1..period).any(|i| !tail[0].same_step(tail[i]));
            if has_variety {
                return Some(period);
            }
        }
        None
    }

    /// A short description of what is looping, for the nudge text.
    fn detail_for(&self, pattern: StuckPattern) -> String {
        let actions = self.trailing_actions();
        match pattern {
            StuckPattern::RepeatedActionObservation | StuckPattern::RepeatedActionError => actions
                .last()
                .map(|a| format!("`{}`", a.tool_name))
                .unwrap_or_else(|| "the same tool".to_owned()),
            StuckPattern::Cyclic => {
                let Some(period) = self.cycle_period() else {
                    return "the same few steps".to_owned();
                };
                let mut names: Vec<String> = self
                    .window
                    .iter()
                    .rev()
                    .take(period)
                    .map(|e| match e {
                        Event::Action(a) => format!("`{}`", a.tool_name),
                        Event::Monologue => "thinking".to_owned(),
                    })
                    .collect();
                names.reverse();
                names.dedup();
                names.join(" and ")
            }
            StuckPattern::Monologue => String::new(),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn obs(tool: &str, input: &str, result: &str, is_error: bool) -> StuckObservation {
        StuckObservation::new(tool, input, result, is_error)
    }

    fn ok(tool: &str, input: &str, result: &str) -> StuckObservation {
        obs(tool, input, result, false)
    }

    fn err(tool: &str, input: &str, result: &str) -> StuckObservation {
        obs(tool, input, result, true)
    }

    // ── Repeated action + observation ────────────────────────────────────────

    #[test]
    fn an_identical_call_returning_an_identical_result_trips_at_four() {
        let d = StuckDetector::new();
        for _ in 0..3 {
            d.observe(ok("read_file", r#"{"p":"a"}"#, "contents"));
        }
        assert_eq!(d.detect(), None, "three repeats is not yet a loop");

        d.observe(ok("read_file", r#"{"p":"a"}"#, "contents"));
        assert_eq!(d.detect(), Some(StuckPattern::RepeatedActionObservation));
    }

    #[test]
    fn a_differing_result_breaks_the_repeat_streak() {
        let d = StuckDetector::new();
        for _ in 0..3 {
            d.observe(ok("read_file", r#"{"p":"a"}"#, "contents"));
        }
        d.observe(ok("read_file", r#"{"p":"a"}"#, "something else"));
        assert_eq!(d.detect(), None, "the result changed, so progress was made");
    }

    #[test]
    fn differing_arguments_are_a_different_action() {
        let d = StuckDetector::new();
        for i in 0..6 {
            d.observe(ok("read_file", &format!(r#"{{"p":"{i}"}}"#), "contents"));
        }
        assert_eq!(d.detect(), None, "reading six different files is progress");
    }

    /// Ids and timestamps differ on every attempt; if they counted, no repeat
    /// would ever be recognised and the whole module would be inert.
    #[test]
    fn equality_ignores_everything_except_tool_arguments_and_thought() {
        let a = ok("read_file", r#"{"p":"a"}"#, "same");
        let b = ok("read_file", r#"{"p":"a"}"#, "same");
        assert!(a.same_action(&b));
        assert!(a.same_action_and_observation(&b));

        let with_thought = ok("read_file", r#"{"p":"a"}"#, "same").with_thought("different plan");
        assert!(
            !a.same_action(&with_thought),
            "a genuinely different plan is a different action"
        );
    }

    // ── Repeated action + error ──────────────────────────────────────────────

    /// Three identical failures is a reasonable thing tried and found wrong.
    /// The fourth is a loop. This boundary is deliberately strict-greater.
    #[test]
    fn an_identical_failing_call_trips_only_after_more_than_three() {
        let d = StuckDetector::new();
        for _ in 0..3 {
            d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        }
        assert_eq!(d.detect(), None, "three identical failures is not yet a loop");

        d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        assert_eq!(d.detect(), Some(StuckPattern::RepeatedActionError));
    }

    /// The error path is checked first, so an erroring repeat reports as an
    /// error loop rather than an observation loop even though both would match.
    #[test]
    fn an_erroring_repeat_reports_as_an_error_loop() {
        let d = StuckDetector::new();
        for _ in 0..5 {
            d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        }
        assert_eq!(d.detect(), Some(StuckPattern::RepeatedActionError));
    }

    #[test]
    fn a_success_after_failures_clears_the_error_streak() {
        let d = StuckDetector::new();
        for _ in 0..4 {
            d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        }
        d.observe(ok("run_command", r#"{"c":"build"}"#, "built"));
        assert_eq!(d.detect(), None, "it finally worked");
    }

    /// Same tool, same failure, but different arguments each time is someone
    /// genuinely searching for the right invocation.
    #[test]
    fn the_same_error_from_different_arguments_is_not_a_loop() {
        let d = StuckDetector::new();
        for i in 0..6 {
            d.observe(err("run_command", &format!(r#"{{"c":"try{i}"}}"#), "E123"));
        }
        assert_eq!(d.detect(), None);
    }

    // ── Monologue ────────────────────────────────────────────────────────────

    #[test]
    fn three_turns_without_a_tool_call_is_a_monologue() {
        let d = StuckDetector::new();
        d.observe_monologue();
        d.observe_monologue();
        assert_eq!(d.detect(), None, "two turns of thinking is allowed");

        d.observe_monologue();
        assert_eq!(d.detect(), Some(StuckPattern::Monologue));
    }

    #[test]
    fn a_tool_call_breaks_the_monologue_streak() {
        let d = StuckDetector::new();
        d.observe_monologue();
        d.observe_monologue();
        d.observe(ok("read_file", r#"{"p":"a"}"#, "contents"));
        d.observe_monologue();
        assert_eq!(d.detect(), None);
    }

    /// A monologue in the middle must not let two separate repeat runs join up
    /// and look like one long streak.
    #[test]
    fn a_monologue_breaks_a_repeat_streak() {
        let d = StuckDetector::new();
        for _ in 0..2 {
            d.observe(ok("read_file", r#"{"p":"a"}"#, "contents"));
        }
        d.observe_monologue();
        for _ in 0..2 {
            d.observe(ok("read_file", r#"{"p":"a"}"#, "contents"));
        }
        assert_eq!(
            d.detect(),
            None,
            "only the two calls after the monologue count toward the streak"
        );
    }

    // ── Cycles ───────────────────────────────────────────────────────────────

    #[test]
    fn six_alternating_actions_are_a_cycle() {
        let d = StuckDetector::new();
        for _ in 0..3 {
            d.observe(ok("edit_file", r#"{"p":"a"}"#, "edited"));
            d.observe(ok("run_command", r#"{"c":"test"}"#, "failed"));
        }
        assert_eq!(d.detect(), Some(StuckPattern::Cyclic));
    }

    #[test]
    fn four_alternating_actions_are_not_yet_a_cycle() {
        let d = StuckDetector::new();
        for _ in 0..2 {
            d.observe(ok("edit_file", r#"{"p":"a"}"#, "edited"));
            d.observe(ok("run_command", r#"{"c":"test"}"#, "failed"));
        }
        assert_eq!(d.detect(), None);
    }

    /// A three-tool rotation is just as much a hang as a two-tool one, and with
    /// no budget ceiling nothing else would ever stop it.
    #[test]
    fn a_three_cycle_is_detected_once_it_has_repeated_enough() {
        let d = StuckDetector::new();
        for _ in 0..2 {
            d.observe(ok("a", "{}", "r"));
            d.observe(ok("b", "{}", "r"));
            d.observe(ok("c", "{}", "r"));
        }
        assert_eq!(d.detect(), None, "two rotations is not yet a cycle");

        d.observe(ok("a", "{}", "r"));
        d.observe(ok("b", "{}", "r"));
        d.observe(ok("c", "{}", "r"));
        assert_eq!(d.detect(), Some(StuckPattern::Cyclic));
    }

    #[test]
    fn a_four_cycle_is_detected() {
        let d = StuckDetector::new();
        for _ in 0..3 {
            for tool in ["a", "b", "c", "d"] {
                d.observe(ok(tool, "{}", "r"));
            }
        }
        assert_eq!(d.detect(), Some(StuckPattern::Cyclic));
    }

    /// A monologue breaks every action streak, so a loop that launders itself
    /// through a thinking turn evades all of them. Cycle detection runs over
    /// events, not actions, precisely to close that hole.
    #[test]
    fn a_loop_laundered_through_thinking_turns_is_still_a_cycle() {
        let d = StuckDetector::new();
        for _ in 0..3 {
            d.observe(ok("run_command", r#"{"c":"test"}"#, "failed"));
            d.observe_monologue();
        }
        assert_eq!(
            d.detect(),
            Some(StuckPattern::Cyclic),
            "alternating action and thought is a hang like any other"
        );
    }

    /// An identical repeat is a repeat, not a cycle — the repeat heuristics
    /// describe it better and fire first.
    #[test]
    fn an_identical_repeat_is_not_reported_as_a_cycle() {
        let d = StuckDetector::new();
        for _ in 0..6 {
            d.observe(ok("read_file", r#"{"p":"a"}"#, "contents"));
        }
        assert_eq!(d.detect(), Some(StuckPattern::RepeatedActionObservation));
    }

    #[test]
    fn genuine_progress_through_many_distinct_tools_is_not_a_cycle() {
        let d = StuckDetector::new();
        for i in 0..12 {
            d.observe(ok(&format!("tool{i}"), "{}", "r"));
        }
        assert_eq!(d.detect(), None);
    }

    // ── Nudge, then escalate ─────────────────────────────────────────────────

    /// Tripping a threshold must not end the run outright: the agent gets told
    /// once and is judged on whether it listened.
    #[test]
    fn the_first_trip_nudges_rather_than_declaring_the_run_stuck() {
        let d = StuckDetector::new();
        for _ in 0..4 {
            d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        }

        assert!(!d.is_stuck(), "the agent has not been told yet");
        let nudge = d.take_nudge().expect("a first trip must produce a nudge");
        assert!(nudge.contains("run_command"), "{nudge}");
        assert!(nudge.contains("will not work"), "{nudge}");
    }

    #[test]
    fn a_pattern_that_survives_its_nudge_marks_the_run_stuck() {
        let d = StuckDetector::new();
        for _ in 0..4 {
            d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        }
        d.take_nudge().expect("nudged once");

        d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        assert!(d.is_stuck(), "it was told, and did it again");
    }

    #[test]
    fn the_nudge_for_a_pattern_is_issued_only_once() {
        let d = StuckDetector::new();
        for _ in 0..4 {
            d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        }
        assert!(d.take_nudge().is_some());
        assert!(d.take_nudge().is_none(), "the agent is told once, not repeatedly");
    }

    #[test]
    fn no_pattern_produces_no_nudge() {
        let d = StuckDetector::new();
        d.observe(ok("read_file", r#"{"p":"a"}"#, "contents"));
        assert!(d.take_nudge().is_none());
        assert!(!d.is_stuck());
    }

    /// Recovering from one loop must not leave the agent pre-condemned for a
    /// different one later.
    #[test]
    fn nudging_one_pattern_does_not_mark_a_different_pattern_stuck() {
        let d = StuckDetector::new();
        for _ in 0..4 {
            d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        }
        d.take_nudge().expect("error loop nudged");

        d.reset();
        d.observe_monologue();
        d.observe_monologue();
        d.observe_monologue();
        assert_eq!(d.detect(), Some(StuckPattern::Monologue));
        assert!(!d.is_stuck(), "a fresh pattern deserves its own nudge first");
    }

    /// The case that matters in a long unattended run: a second error loop,
    /// with no operator input in between. Keying the nudge by pattern alone
    /// would silently skip the warning and declare this stuck on the spot.
    #[test]
    fn a_second_unrelated_error_loop_gets_its_own_nudge_without_any_reset() {
        let d = StuckDetector::new();
        for _ in 0..4 {
            d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        }
        let first = d.take_nudge().expect("the first loop is nudged");
        assert!(first.contains("run_command"), "{first}");

        // Genuine progress breaks the streak — no reset(), as would happen in
        // a headless run where the operator never speaks.
        for i in 0..4 {
            d.observe(ok("read_file", &format!(r#"{{"p":"{i}"}}"#), "contents"));
        }
        assert_eq!(d.detect(), None, "the first loop is over");

        // A different tool now starts failing.
        for _ in 0..4 {
            d.observe(err("web_fetch", r#"{"u":"x"}"#, "timeout"));
        }
        assert!(
            !d.is_stuck(),
            "a genuinely new loop must not be declared stuck before it is warned"
        );
        let second = d.take_nudge().expect("the second loop deserves its own nudge");
        assert!(second.contains("web_fetch"), "{second}");
    }

    /// The same streak must still be nudged only once, or the agent would be
    /// told the same thing forever and never escalate.
    #[test]
    fn breaking_a_loop_and_resuming_the_very_same_one_does_not_reset_grace() {
        let d = StuckDetector::new();
        for _ in 0..4 {
            d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        }
        d.take_nudge().expect("nudged once");

        d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        assert!(d.is_stuck(), "it was told, and did the same thing again");
        assert!(d.take_nudge().is_none(), "the same streak is never nudged twice");
    }

    // ── Window and reset ─────────────────────────────────────────────────────

    #[test]
    fn a_new_instruction_clears_the_history() {
        let d = StuckDetector::new();
        for _ in 0..5 {
            d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        }
        assert!(d.detect().is_some());

        d.reset();
        assert_eq!(d.detect(), None, "the operator said something new");
        assert!(!d.is_stuck());
    }

    /// The window bound is about memory, not detection: `result_content` can be
    /// large and an unbudgeted run observes without limit. Detection reads only
    /// the tail, so asserting on `detect()` would pass even with the trim
    /// removed — the bound has to be asserted directly.
    #[test]
    fn the_window_is_bounded_however_long_the_run_goes_on() {
        let d = StuckDetector::new();
        for i in 0..(WINDOW * 5) {
            d.observe(ok("read_file", &format!(r#"{{"p":"{i}"}}"#), "contents"));
        }
        assert_eq!(
            d.lock().window.len(),
            WINDOW,
            "the window must never grow beyond its bound"
        );
    }

    /// A loop that has since been buried under genuine progress must not keep
    /// haunting the run.
    #[test]
    fn an_old_loop_stops_being_reported_once_progress_resumes() {
        let d = StuckDetector::new();
        for _ in 0..4 {
            d.observe(err("run_command", r#"{"c":"build"}"#, "E123"));
        }
        assert!(d.detect().is_some());

        for i in 0..WINDOW {
            d.observe(ok("read_file", &format!(r#"{{"p":"{i}"}}"#), "contents"));
        }
        assert_eq!(d.detect(), None, "the run has moved on");
    }

    #[test]
    fn an_empty_detector_reports_nothing() {
        let d = StuckDetector::new();
        assert_eq!(d.detect(), None);
        assert!(!d.is_stuck());
        assert!(d.take_nudge().is_none());
    }

    // ── Sharing ──────────────────────────────────────────────────────────────

    #[test]
    fn the_detector_is_shareable_across_threads() {
        use std::sync::Arc;

        let d = Arc::new(StuckDetector::new());
        let mut handles = Vec::new();
        for i in 0..8 {
            let d = Arc::clone(&d);
            handles.push(std::thread::spawn(move || {
                d.observe(ok("read_file", &format!(r#"{{"p":"{i}"}}"#), "contents"));
            }));
        }
        for h in handles {
            h.join().expect("no writer may panic");
        }
        assert_eq!(d.detect(), None, "eight distinct reads are progress");
    }
}
