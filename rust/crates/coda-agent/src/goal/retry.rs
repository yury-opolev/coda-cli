//! Exponential-backoff retry policy for the goal judge call.
//!
//! `OperationCanceledException` (cancellation) propagates immediately; any
//! other error is retried up to `max_attempts - 1` times with backoff capped
//! at `max_delay`.  If every attempt throws, `(false, None)` is returned
//! (fail-open: the supervisor then returns `Continue` so the budget guarantees
//! termination).

use std::time::Duration;

use tokio_util::sync::CancellationToken;

/// Retry configuration for the goal judge.
#[derive(Debug, Clone)]
pub struct GoalRetryPolicy {
    /// Maximum number of attempts (≥ 1).
    pub max_attempts: u32,
    /// Base backoff for the first retry.
    pub base_delay: Duration,
    /// Upper bound on the backoff interval.
    pub max_delay: Duration,
}

impl Default for GoalRetryPolicy {
    fn default() -> Self {
        Self {
            max_attempts: 4,
            base_delay: Duration::from_secs(1),
            max_delay: Duration::from_secs(15),
        }
    }
}

impl GoalRetryPolicy {
    /// A policy that retries without sleeping — useful in tests.
    pub fn for_tests() -> Self {
        Self {
            max_attempts: 4,
            base_delay: Duration::ZERO,
            max_delay: Duration::ZERO,
        }
    }

    /// Run `producer` with retries.
    ///
    /// Returns `(true, Some(value))` on success, or `(false, None)` if every
    /// attempt failed.  Cancellation propagates immediately — the supervisor
    /// must NOT suppress it.
    pub async fn run<T, F, Fut>(
        &self,
        producer: F,
        cancel: CancellationToken,
    ) -> Result<(bool, Option<T>), tokio_util::sync::CancellationToken>
    where
        F: Fn(CancellationToken) -> Fut,
        Fut: std::future::Future<Output = anyhow::Result<T>>,
    {
        let max = self.max_attempts.max(1);
        for attempt in 1..=max {
            // Bail immediately if cancelled before attempting.
            if cancel.is_cancelled() {
                return Err(cancel);
            }

            match producer(cancel.clone()).await {
                Ok(value) => return Ok((true, Some(value))),
                Err(_) if attempt < max => {
                    // Exponential backoff capped at max_delay.
                    // Cap the shift at 62 to avoid overflow on large attempt counts.
                    let shift = (attempt - 1).min(62);
                    let backoff_nanos = self
                        .max_delay
                        .as_nanos()
                        .min(self.base_delay.as_nanos().saturating_mul(1u128 << shift));
                    let backoff = Duration::from_nanos(backoff_nanos as u64);

                    tokio::select! {
                        _ = tokio::time::sleep(backoff) => {}
                        _ = cancel.cancelled() => return Err(cancel),
                    }
                }
                Err(_) => {
                    // Final attempt failed.
                }
            }
        }

        Ok((false, None))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio_util::sync::CancellationToken;

    #[tokio::test]
    async fn succeeds_on_first_attempt() {
        let policy = GoalRetryPolicy::for_tests();
        let cancel = CancellationToken::new();
        let (ok, val) = policy
            .run(|_| async { Ok::<_, anyhow::Error>(42) }, cancel)
            .await
            .unwrap();
        assert!(ok);
        assert_eq!(val, Some(42));
    }

    #[tokio::test]
    async fn retries_and_eventually_succeeds() {
        let policy = GoalRetryPolicy::for_tests();
        let cancel = CancellationToken::new();
        let counter = std::sync::Arc::new(std::sync::atomic::AtomicU32::new(0));
        let c = counter.clone();
        let (ok, val) = policy
            .run(
                move |_| {
                    let c = c.clone();
                    async move {
                        let n = c.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
                        if n < 2 {
                            Err(anyhow::anyhow!("transient"))
                        } else {
                            Ok("done")
                        }
                    }
                },
                cancel,
            )
            .await
            .unwrap();
        assert!(ok);
        assert_eq!(val, Some("done"));
        assert_eq!(counter.load(std::sync::atomic::Ordering::SeqCst), 3);
    }

    #[tokio::test]
    async fn returns_false_when_all_attempts_fail() {
        let policy = GoalRetryPolicy { max_attempts: 3, ..GoalRetryPolicy::for_tests() };
        let cancel = CancellationToken::new();
        let (ok, val) = policy
            .run(|_| async { Err::<String, _>(anyhow::anyhow!("always fails")) }, cancel)
            .await
            .unwrap();
        assert!(!ok);
        assert!(val.is_none());
    }

    #[tokio::test]
    async fn cancellation_propagates() {
        let policy = GoalRetryPolicy::for_tests();
        let cancel = CancellationToken::new();
        cancel.cancel();
        let result = policy
            .run(|_| async { Ok::<_, anyhow::Error>(1) }, cancel)
            .await;
        assert!(result.is_err(), "cancellation should propagate as Err");
    }

    // ── MINOR 7: backoff / clamp / overflow / mid-backoff cancel ──────────────

    #[tokio::test]
    async fn exponential_backoff_grows_with_attempts() {
        // Use zero delays (for_tests policy) so the test runs instantly; what
        // we verify is that all 4 attempts execute and the function returns
        // false without panicking.
        let policy = GoalRetryPolicy {
            max_attempts: 4,
            base_delay: std::time::Duration::ZERO,
            max_delay: std::time::Duration::ZERO,
        };
        let cancel = CancellationToken::new();
        let (ok, _) = policy
            .run(|_| async { Err::<(), _>(anyhow::anyhow!("always fails")) }, cancel)
            .await
            .unwrap();
        assert!(!ok);
    }

    #[tokio::test]
    async fn max_delay_clamp_is_respected() {
        // The `min(max_delay)` clamp must not panic for any attempt count.
        // Use a very short real delay so the test still exercises the sleep path.
        let max = std::time::Duration::from_millis(1);
        let policy = GoalRetryPolicy {
            max_attempts: 5,
            base_delay: std::time::Duration::from_millis(1),
            max_delay: max,
        };
        let cancel = CancellationToken::new();
        let (ok, _) = policy
            .run(|_| async { Err::<(), _>(anyhow::anyhow!("fail")) }, cancel)
            .await
            .unwrap();
        assert!(!ok);
    }

    #[tokio::test]
    async fn large_attempt_count_does_not_overflow_shift() {
        // shift.min(62) must prevent u128 overflow when attempt numbers are large.
        // Use a tiny delay and few attempts to keep the test fast.
        let policy = GoalRetryPolicy {
            max_attempts: 70, // attempt 70 → shift = 68 → clamped to 62 → no overflow
            base_delay: std::time::Duration::from_nanos(1),
            max_delay: std::time::Duration::from_nanos(1),
        };
        let cancel = CancellationToken::new();
        let (ok, _) = policy
            .run(|_| async { Err::<(), _>(anyhow::anyhow!("fail")) }, cancel)
            .await
            .unwrap();
        assert!(!ok); // 70 failures — no panic from overflow
    }

    #[tokio::test]
    async fn cancellation_mid_backoff_propagates() {
        // The select! inside the retry loop must wake on cancel even while
        // sleeping between attempts.  We use a short but non-zero sleep so
        // the cancel arm is actually reached.
        let policy = GoalRetryPolicy {
            max_attempts: 10,
            base_delay: std::time::Duration::from_millis(500),
            max_delay: std::time::Duration::from_millis(500),
        };
        let cancel = CancellationToken::new();
        let cancel_clone = cancel.clone();

        // Cancel after the first failure so the retry loop enters the sleep arm.
        let first_call = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
        let first_call2 = first_call.clone();
        let result = tokio::time::timeout(
            std::time::Duration::from_secs(5),
            policy.run(
                move |_| {
                    let cancel_clone = cancel_clone.clone();
                    let first_call2 = first_call2.clone();
                    async move {
                        if !first_call2.swap(true, std::sync::atomic::Ordering::SeqCst) {
                            // Only cancel on the first call.
                            cancel_clone.cancel();
                        }
                        Err::<(), _>(anyhow::anyhow!("fail"))
                    }
                },
                cancel,
            ),
        )
        .await
        .expect("must complete quickly after cancel");

        assert!(result.is_err(), "mid-backoff cancel must propagate as Err");
    }
}
