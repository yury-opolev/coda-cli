//! Goal budget: dual-dimension wall-clock + continuation counter with one extension.
//!
//! The budget governs termination for an autonomous goal run (MaxIterations is
//! ignored while a goal is active). Whichever dimension trips first causes
//! exhaustion. Exactly one bounded extension can be granted by the loop when
//! the operator answers an escalation — it raises both ceilings so even a
//! tiny budget gets at least one more turn.
//!
//! Not thread-safe; owned exclusively by the agent loop.

use std::time::Duration;

/// Dual-dimension autonomous-goal budget.
pub struct GoalBudget {
    /// Injectable wall-clock elapsed time — `Fn() -> Duration`.
    elapsed: Box<dyn Fn() -> Duration + Send>,
    max_duration: Duration,
    max_continuations: u32,
    extension_fraction: f64,
    continuations: u32,
    extension_used: bool,
}

impl GoalBudget {
    /// Create a budget with an injectable elapsed-time function.
    ///
    /// Use [`GoalBudget::start_now`] for production code; inject a closure for
    /// tests that need deterministic time.
    pub fn new(
        max_duration: Duration,
        max_continuations: u32,
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
        max_duration: Duration,
        max_continuations: u32,
        extension_fraction: f64,
    ) -> Self {
        let start = std::time::Instant::now();
        Self::new(max_duration, max_continuations, extension_fraction, move || start.elapsed())
    }

    /// True when either the wall-clock or the continuation ceiling has been hit.
    pub fn is_exhausted(&self) -> bool {
        (self.elapsed)() >= self.max_duration || self.continuations >= self.max_continuations
    }

    /// Increment the continuation counter (called each time the supervisor
    /// returns `Continue`).
    pub fn record_continuation(&mut self) {
        self.continuations += 1;
    }

    /// Grant the single bounded extension, raising both ceilings by
    /// `extension_fraction`.  Returns `false` if an extension was already used.
    ///
    /// Both ceilings are raised by at least one unit so a small / zero budget
    /// actually unblocks the run after an operator answers the escalation.
    pub fn grant_extension(&mut self) -> bool {
        if self.extension_used {
            return false;
        }
        self.extension_used = true;

        let duration_bump = self.max_duration.mul_f64(self.extension_fraction);
        // Use saturating_add so Duration::MAX (used as "no limit") doesn't overflow.
        self.max_duration = self.max_duration.saturating_add(
            if duration_bump > Duration::ZERO { duration_bump } else { Duration::from_nanos(1) }
        );

        let cont_bump = (self.max_continuations as f64 * self.extension_fraction).ceil() as u32;
        self.max_continuations += cont_bump.max(1);

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
        GoalBudget::new(Duration::from_secs(10), 5, 0.5, move || d)
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
            Duration::from_secs(10),
            4,
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
            Duration::ZERO,
            0,
            0.5,
            || Duration::ZERO,
        );
        assert!(budget.is_exhausted());
        assert!(budget.grant_extension());
        // max_duration and max_continuations were both raised by at least 1 unit.
        assert!(!budget.is_exhausted());
    }
}
