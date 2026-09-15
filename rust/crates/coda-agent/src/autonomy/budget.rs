//! Goal budget: dual-dimension wall-clock + continuation counter with one extension.
//!
//! The budget governs termination for an autonomous goal run (MaxIterations is
//! ignored while a goal is active). Whichever dimension trips first causes
//! exhaustion. Exactly one bounded extension can be granted by the loop when
//! the operator answers an escalation — it raises both ceilings so even a
//! tiny budget gets at least one more turn.
//!
//! Either ceiling may be `None`, meaning **no limit on that dimension**. A goal
//! is a promise to keep working until a judge says it is done, so an operator
//! must be able to say "no ceiling at all" and have that be literally true
//! rather than approximated by a large sentinel. A `None` dimension never
//! exhausts and is never raised by an extension.
//!
//! Not thread-safe; owned exclusively by the agent loop.

use std::time::Duration;

/// Dual-dimension autonomous-goal budget.
pub struct GoalBudget {
    /// Injectable wall-clock elapsed time — `Fn() -> Duration`.
    elapsed: Box<dyn Fn() -> Duration + Send>,
    /// `None` = no wall-clock limit.
    max_duration: Option<Duration>,
    /// `None` = no continuation limit.
    max_continuations: Option<u32>,
    extension_fraction: f64,
    continuations: u32,
    extension_used: bool,
}

impl GoalBudget {
    /// Create a budget with an injectable elapsed-time function.
    ///
    /// Use [`GoalBudget::start_now`] for production code; inject a closure for
    /// tests that need deterministic time. `None` for either ceiling means that
    /// dimension is unlimited.
    pub fn new(
        max_duration: Option<Duration>,
        max_continuations: Option<u32>,
        extension_fraction: f64,
        elapsed: impl Fn() -> Duration + Send + 'static,
    ) -> Self {
        Self {
            elapsed: Box::new(elapsed),
            max_duration,
            max_continuations,
            extension_fraction,
            continuations: 0,
            extension_used: false,
        }
    }

    /// Production factory — starts a stopwatch right now.
    pub fn start_now(
        max_duration: Option<Duration>,
        max_continuations: Option<u32>,
        extension_fraction: f64,
    ) -> Self {
        let start = std::time::Instant::now();
        Self::new(max_duration, max_continuations, extension_fraction, move || start.elapsed())
    }

    /// True when a **set** ceiling has been reached on either dimension.
    ///
    /// An unlimited (`None`) dimension can never exhaust, so a budget with both
    /// ceilings unset never stops the run — completion is then decided solely
    /// by the judge.
    pub fn is_exhausted(&self) -> bool {
        self.max_duration.is_some_and(|max| (self.elapsed)() >= max)
            || self.max_continuations.is_some_and(|max| self.continuations >= max)
    }

    /// Increment the continuation counter (called each time the supervisor
    /// returns `Continue`).
    ///
    /// Saturating: an unlimited run must not panic on overflow in a debug build.
    pub fn record_continuation(&mut self) {
        self.continuations = self.continuations.saturating_add(1);
    }

    /// Grant the single bounded extension, raising both **set** ceilings by
    /// `extension_fraction`.  Returns `false` if an extension was already used.
    ///
    /// A set ceiling is raised by at least one unit so a small / zero budget
    /// actually unblocks the run after an operator answers the escalation.
    /// An unlimited dimension is left alone — there is nothing to raise, and
    /// scaling a sentinel would overflow.
    pub fn grant_extension(&mut self) -> bool {
        if self.extension_used {
            return false;
        }
        self.extension_used = true;

        if let Some(max) = self.max_duration {
            let bump = max.mul_f64(self.extension_fraction);
            let bump = if bump > Duration::ZERO { bump } else { Duration::from_nanos(1) };
            self.max_duration = Some(max.saturating_add(bump));
        }

        if let Some(max) = self.max_continuations {
            let bump = ((max as f64 * self.extension_fraction).ceil() as u32).max(1);
            self.max_continuations = Some(max.saturating_add(bump));
        }

        true
    }

    pub fn continuations(&self) -> u32 {
        self.continuations
    }

    pub fn elapsed(&self) -> Duration {
        (self.elapsed)()
    }

    pub fn extension_used(&self) -> bool {
        self.extension_used
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frozen(d: Duration) -> GoalBudget {
        GoalBudget::new(Some(Duration::from_secs(10)), Some(5), 0.5, move || d)
    }

    // §8 item 17: exhaustion by duration.
    #[test]
    fn exhausted_when_elapsed_exceeds_max_duration() {
        // elapsed = 11s > max = 10s
        let budget = frozen(Duration::from_secs(11));
        assert!(budget.is_exhausted());
    }

    // §8 item 17: exhaustion by continuation count.
    #[test]
    fn exhausted_when_continuations_reach_max() {
        let mut budget = frozen(Duration::ZERO); // elapsed = 0 < max = 10s
        for _ in 0..5 {
            budget.record_continuation();
        }
        assert!(budget.is_exhausted());
    }

    #[test]
    fn not_exhausted_while_within_both_limits() {
        let mut budget = frozen(Duration::ZERO);
        budget.record_continuation();
        assert!(!budget.is_exhausted());
    }

    // §8 item 20: extension raises both ceilings and can be granted once.
    #[test]
    fn extension_raises_ceilings_and_can_only_be_granted_once() {
        let mut budget = GoalBudget::new(
            Some(Duration::from_secs(10)),
            Some(4),
            0.5,
            || Duration::from_secs(0),
        );
        // Exhaust by continuations.
        for _ in 0..4 {
            budget.record_continuation();
        }
        assert!(budget.is_exhausted());

        // First extension granted.
        assert!(budget.grant_extension());
        assert!(budget.extension_used());
        // Should no longer be exhausted (max_continuations raised).
        assert!(!budget.is_exhausted());

        // Second attempt must fail.
        assert!(!budget.grant_extension());
    }

    #[test]
    fn extension_with_zero_budget_raises_by_at_least_one() {
        // A zero-duration budget should still unblock after extension.
        let mut budget = GoalBudget::new(
            Some(Duration::ZERO),
            Some(0),
            0.5,
            || Duration::ZERO,
        );
        assert!(budget.is_exhausted());
        assert!(budget.grant_extension());
        // max_duration and max_continuations were both raised by at least 1 unit.
        assert!(!budget.is_exhausted());
    }

    // ── Unlimited ceilings ───────────────────────────────────────────────────

    /// An operator who asks for "no limit" must get exactly that: neither
    /// dimension may ever exhaust, however long the run goes on.
    #[test]
    fn a_fully_unlimited_budget_never_exhausts() {
        let mut budget = GoalBudget::new(None, None, 0.5, || Duration::from_secs(u32::MAX as u64));
        assert!(!budget.is_exhausted());
        for _ in 0..10_000 {
            budget.record_continuation();
        }
        assert!(!budget.is_exhausted(), "an unlimited budget must never exhaust");
    }

    /// Each dimension is independent: an unlimited clock must not stop a run
    /// that still has a continuation ceiling, and vice versa.
    #[test]
    fn one_unlimited_dimension_does_not_suppress_the_other() {
        // Unlimited clock, limited continuations → exhausts on continuations.
        let mut budget = GoalBudget::new(None, Some(2), 0.5, || Duration::from_secs(u64::MAX / 2));
        assert!(!budget.is_exhausted());
        budget.record_continuation();
        budget.record_continuation();
        assert!(budget.is_exhausted());

        // Limited clock, unlimited continuations → exhausts on the clock.
        let mut budget = GoalBudget::new(Some(Duration::from_secs(10)), None, 0.5, || {
            Duration::from_secs(11)
        });
        assert!(budget.is_exhausted());
        budget.record_continuation();
        assert!(budget.is_exhausted());
    }

    /// Extending a partly-unlimited budget must leave the unlimited dimension
    /// unlimited rather than converting it into a finite ceiling.
    #[test]
    fn extension_leaves_an_unlimited_dimension_unlimited() {
        let mut budget =
            GoalBudget::new(None, Some(1), 0.5, || Duration::from_secs(u64::MAX / 2));
        budget.record_continuation();
        assert!(budget.is_exhausted());

        assert!(budget.grant_extension());
        assert!(!budget.is_exhausted());
        // The clock is still unlimited: an enormous elapsed time cannot exhaust it.
        assert!(budget.max_duration.is_none(), "an unlimited clock must stay unlimited");
        assert_eq!(budget.max_continuations, Some(2));
    }

    /// Regression: scaling a near-`MAX` ceiling used to overflow and panic in a
    /// debug build. Saturation must keep it finite-but-huge instead.
    #[test]
    fn extension_near_the_ceiling_saturates_instead_of_panicking() {
        let mut budget =
            GoalBudget::new(Some(Duration::MAX), Some(u32::MAX), 0.5, || Duration::ZERO);
        assert!(budget.grant_extension());
        assert_eq!(budget.max_continuations, Some(u32::MAX));
        assert_eq!(budget.max_duration, Some(Duration::MAX));
    }

    /// Regression: the continuation counter itself must not overflow on a very
    /// long unlimited run.
    #[test]
    fn record_continuation_saturates_at_the_counter_ceiling() {
        let mut budget = GoalBudget::new(None, None, 0.5, || Duration::ZERO);
        budget.continuations = u32::MAX;
        budget.record_continuation();
        assert_eq!(budget.continuations(), u32::MAX);
    }
}
