//! `TurnProgress`: one independent, truthful model of what a running turn is
//! doing right now, for the pinned activity row above the composer.
//!
//! Deliberately separate from [`crate::state::Activity`], which the status
//! bar already uses: that enum is coarse (`Working`/`Thinking`/`Waiting`) and
//! existed before any turn-level clock did. This model exists to answer three
//! questions truthfully, and only when it actually knows the answer:
//!
//! - How long has this turn been running, in real (monotonic, wall-adjacent)
//!   time, from the moment it was submitted locally — before any engine or
//!   network event has arrived at all?
//! - How much of that has specifically been model reasoning, as opposed to
//!   running tools or waiting on the user? Reasoning time is only ever
//!   credited from an explicit [`Event::Thinking`](coda_proto::Event), never
//!   guessed from silence elsewhere.
//! - What did the last response actually cost, once a real `Usage` event
//!   says so? Never a per-turn total nobody sent, never a reasoning token
//!   count invented to fill a blank.
//!
//! Every transition is driven by an explicit call from the reducer, so this
//! stays exactly as testable as the rest of the state machine: feed it
//! instants and events, assert on what it reports.

use std::time::Instant;

/// What the turn is doing right now.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Phase {
    /// Submitted, but nothing more specific has been reported yet.
    ///
    /// The starting phase: generic on purpose, because at the instant a turn
    /// is submitted nothing is yet known about what the model will do with
    /// it, and claiming otherwise would be a guess dressed as an observation.
    Working,
    /// The model is reasoning (an explicit `Event::Thinking` has arrived,
    /// even one whose first delta was empty).
    Thinking,
    /// One or more tool calls are in flight.
    RunningTools,
    /// Blocked on the user answering a permission or question prompt.
    AwaitingApproval,
    /// Assistant text is streaming.
    Responding,
}

impl Phase {
    /// A short label for the pinned row.
    pub fn label(self) -> &'static str {
        match self {
            Phase::Working => "Working",
            Phase::Thinking => "Thinking",
            Phase::RunningTools => "Running tools",
            Phase::AwaitingApproval => "Waiting for you",
            Phase::Responding => "Responding",
        }
    }
}

/// The turn's own clock and phase, independent of the transcript.
#[derive(Debug, Clone)]
pub struct TurnProgress {
    started_at: Instant,
    phase: Phase,
    /// Sum of reasoning segments that have already ended.
    reasoning_committed_ms: i64,
    /// Start of the reasoning segment in progress, if the model is
    /// currently between `Thinking` and its `ThinkingComplete`.
    reasoning_started_at: Option<Instant>,
    /// Whether any `Thinking` event has ever been observed this turn — an
    /// empty first delta still counts, because the phase genuinely began.
    has_reasoned: bool,
    /// `(input, output)` from the most recent `Usage` event, if any has
    /// arrived. Explicitly the *last response*'s tokens, per the existing
    /// reducer: `Event::Usage` overwrites rather than accumulates, so this
    /// must never be presented as a running total.
    last_response_tokens: Option<(i64, i64)>,
    /// Set once by [`TurnProgress::finish`]; every later call is a no-op, so
    /// a duplicate or late end (`TurnComplete` racing `TurnFinished`, a
    /// cancel following an error) can never move an already-frozen clock.
    frozen_elapsed_ms: Option<i64>,
}

impl TurnProgress {
    /// Starts the clock at `now` — called on a successful local `Submitted`,
    /// before any engine or network event, so the pinned row has something
    /// truthful to show at the very first frame: zero seconds, `Working`.
    pub fn start(now: Instant) -> Self {
        Self {
            started_at: now,
            phase: Phase::Working,
            reasoning_committed_ms: 0,
            reasoning_started_at: None,
            has_reasoned: false,
            last_response_tokens: None,
            frozen_elapsed_ms: None,
        }
    }

    pub fn phase(&self) -> Phase {
        self.phase
    }

    pub fn is_finished(&self) -> bool {
        self.frozen_elapsed_ms.is_some()
    }

    /// Total elapsed time since submission, monotonic, frozen once finished.
    pub fn elapsed_ms(&self, now: Instant) -> i64 {
        match self.frozen_elapsed_ms {
            Some(ms) => ms,
            None => elapsed_since(self.started_at, now),
        }
    }

    /// Time actually spent reasoning, or `None` if no `Thinking` event has
    /// ever arrived this turn — never a fabricated zero.
    pub fn reasoning_ms(&self, now: Instant) -> Option<i64> {
        if !self.has_reasoned {
            return None;
        }
        let live = if self.frozen_elapsed_ms.is_some() {
            0
        } else {
            self.reasoning_started_at
                .map(|started| elapsed_since(started, now))
                .unwrap_or(0)
        };
        Some(self.reasoning_committed_ms + live)
    }

    /// The last response's token counts, when a `Usage` event has reported
    /// them. Labelled by the caller as the last response's, never a total.
    pub fn last_response_tokens(&self) -> Option<(i64, i64)> {
        self.last_response_tokens
    }

    /// An explicit `Event::Thinking` arrived. Counts even with an empty
    /// first delta — the phase genuinely began, whether or not any text has
    /// been streamed yet.
    pub fn on_thinking_start(&mut self, now: Instant) {
        if self.is_finished() {
            return;
        }
        self.phase = Phase::Thinking;
        self.has_reasoned = true;
        if self.reasoning_started_at.is_none() {
            self.reasoning_started_at = Some(now);
        }
    }

    /// `ThinkingComplete` arrived: commit the reasoning segment and return
    /// to the generic working phase until something more specific happens.
    pub fn on_thinking_end(&mut self, now: Instant) {
        self.commit_reasoning(now);
        if !self.is_finished() {
            self.phase = Phase::Working;
        }
    }

    /// A tool call started.
    pub fn on_tool_call_started(&mut self, now: Instant) {
        if self.is_finished() {
            return;
        }
        // A tool call arriving mid-reasoning still ends that reasoning
        // segment: running a tool is not thinking, however it was reached.
        self.commit_reasoning(now);
        self.phase = Phase::RunningTools;
    }

    /// Assistant text started streaming.
    pub fn on_responding(&mut self, now: Instant) {
        if self.is_finished() {
            return;
        }
        self.commit_reasoning(now);
        self.phase = Phase::Responding;
    }

    /// The engine is waiting on a permission or question prompt.
    pub fn on_awaiting_approval(&mut self, now: Instant) {
        if self.is_finished() {
            return;
        }
        self.commit_reasoning(now);
        self.phase = Phase::AwaitingApproval;
    }

    /// The user answered; the turn resumes without a more specific signal
    /// yet, so the generic working phase is truthful until one arrives.
    pub fn on_resumed(&mut self) {
        if self.is_finished() {
            return;
        }
        self.phase = Phase::Working;
    }

    /// A real `Usage` event arrived, naming the last response's tokens.
    pub fn on_usage(&mut self, input_tokens: i64, output_tokens: i64) {
        self.last_response_tokens = Some((input_tokens, output_tokens));
    }

    /// Freezes the clock. Idempotent: once finished, later calls (a
    /// duplicate `TurnComplete`, a `TurnFinished` that follows it, a cancel
    /// racing an error) change nothing, so the displayed duration can never
    /// jump after the fact.
    pub fn finish(&mut self, now: Instant) {
        if self.is_finished() {
            return;
        }
        self.commit_reasoning(now);
        self.frozen_elapsed_ms = Some(elapsed_since(self.started_at, now));
    }

    fn commit_reasoning(&mut self, now: Instant) {
        if let Some(started) = self.reasoning_started_at.take() {
            self.reasoning_committed_ms += elapsed_since(started, now);
        }
    }
}

fn elapsed_since(from: Instant, now: Instant) -> i64 {
    now.saturating_duration_since(from)
        .as_millis()
        .min(i64::MAX as u128) as i64
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    #[test]
    fn starts_generic_and_at_zero() {
        let now = Instant::now();
        let progress = TurnProgress::start(now);
        assert_eq!(progress.phase(), Phase::Working);
        assert_eq!(progress.elapsed_ms(now), 0);
        assert_eq!(progress.reasoning_ms(now), None, "no fake reasoning duration");
        assert_eq!(progress.last_response_tokens(), None, "no fake token count");
        assert!(!progress.is_finished());
    }

    #[test]
    fn elapsed_time_advances_from_the_local_submit_instant_alone() {
        let start = Instant::now();
        let progress = TurnProgress::start(start);
        let later = start + Duration::from_secs(7);
        // No engine event ever arrived; the clock still moves.
        assert_eq!(progress.elapsed_ms(later), 7_000);
    }

    #[test]
    fn an_explicit_thinking_event_and_only_that_enters_the_thinking_phase() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);
        assert_eq!(progress.phase(), Phase::Working);

        let thinking_at = start + Duration::from_secs(2);
        progress.on_thinking_start(thinking_at);
        assert_eq!(progress.phase(), Phase::Thinking);
    }

    #[test]
    fn an_empty_first_thinking_delta_still_counts_as_having_reasoned() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);
        // The event arriving at all is what counts, not the delta's length.
        progress.on_thinking_start(start);
        assert_eq!(progress.reasoning_ms(start), Some(0));
    }

    #[test]
    fn reasoning_duration_accumulates_across_more_than_one_segment() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);

        progress.on_thinking_start(start);
        progress.on_thinking_end(start + Duration::from_secs(3));
        progress.on_tool_call_started(start + Duration::from_secs(3));
        // Running a tool is not reasoning time, however long it takes.
        progress.on_thinking_start(start + Duration::from_secs(10));
        progress.on_thinking_end(start + Duration::from_secs(12));

        assert_eq!(progress.reasoning_ms(start + Duration::from_secs(12)), Some(5_000));
    }

    #[test]
    fn tool_time_is_never_credited_as_reasoning() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);
        progress.on_tool_call_started(start);
        let later = start + Duration::from_secs(30);
        assert_eq!(progress.phase(), Phase::RunningTools);
        assert_eq!(progress.reasoning_ms(later), None, "no Thinking event ever arrived");
    }

    #[test]
    fn waiting_time_is_never_credited_as_reasoning_either() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);
        progress.on_awaiting_approval(start);
        let later = start + Duration::from_secs(60);
        assert_eq!(progress.phase(), Phase::AwaitingApproval);
        assert_eq!(progress.reasoning_ms(later), None);
    }

    #[test]
    fn a_long_silence_is_reflected_before_any_further_event_arrives() {
        let start = Instant::now();
        let progress = TurnProgress::start(start);
        let later = start + Duration::from_secs(120);
        assert_eq!(progress.elapsed_ms(later), 120_000);
        assert_eq!(progress.phase(), Phase::Working);
    }

    #[test]
    fn late_thinking_after_a_long_silence_changes_the_truthful_phase() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);
        let later = start + Duration::from_secs(120);
        assert_eq!(progress.phase(), Phase::Working);
        progress.on_thinking_start(later);
        assert_eq!(progress.phase(), Phase::Thinking);
    }

    #[test]
    fn resuming_after_a_prompt_returns_to_the_generic_working_phase() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);
        progress.on_awaiting_approval(start);
        progress.on_resumed();
        assert_eq!(progress.phase(), Phase::Working);
    }

    #[test]
    fn usage_reports_the_last_responses_tokens_only_once_known() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);
        assert_eq!(progress.last_response_tokens(), None);
        progress.on_usage(120, 45);
        assert_eq!(progress.last_response_tokens(), Some((120, 45)));
        // A later response overwrites — it is the *last* response, not a sum.
        progress.on_usage(10, 5);
        assert_eq!(progress.last_response_tokens(), Some((10, 5)));
    }

    #[test]
    fn finishing_freezes_the_elapsed_clock() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);
        let end = start + Duration::from_secs(9);
        progress.finish(end);
        assert!(progress.is_finished());
        assert_eq!(progress.elapsed_ms(end), 9_000);

        let much_later = end + Duration::from_secs(999);
        assert_eq!(progress.elapsed_ms(much_later), 9_000, "a frozen clock must not keep ticking");
    }

    #[test]
    fn finishing_is_idempotent_under_a_duplicate_or_late_end() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);
        progress.finish(start + Duration::from_secs(5));
        assert_eq!(progress.elapsed_ms(start + Duration::from_secs(5)), 5_000);

        // TurnComplete then TurnFinished, or a cancel racing an error: a
        // second finalization at a different instant changes nothing.
        progress.finish(start + Duration::from_secs(50));
        assert_eq!(progress.elapsed_ms(start + Duration::from_secs(999)), 5_000);
    }

    #[test]
    fn finishing_commits_an_in_progress_reasoning_segment_exactly_once() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);
        progress.on_thinking_start(start);
        progress.finish(start + Duration::from_secs(4));
        assert_eq!(progress.reasoning_ms(start + Duration::from_secs(4)), Some(4_000));

        // A duplicate finish at a much later instant must not add more.
        progress.finish(start + Duration::from_secs(400));
        assert_eq!(progress.reasoning_ms(start + Duration::from_secs(999)), Some(4_000));
    }

    #[test]
    fn mutating_calls_after_finish_are_all_no_ops() {
        let start = Instant::now();
        let mut progress = TurnProgress::start(start);
        progress.finish(start + Duration::from_secs(1));

        progress.on_thinking_start(start + Duration::from_secs(2));
        progress.on_tool_call_started(start + Duration::from_secs(3));
        progress.on_responding(start + Duration::from_secs(4));
        progress.on_awaiting_approval(start + Duration::from_secs(5));
        progress.on_resumed();

        assert_eq!(progress.elapsed_ms(start + Duration::from_secs(999)), 1_000);
        assert_eq!(progress.reasoning_ms(start + Duration::from_secs(999)), None);
    }
}
