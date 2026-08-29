//! Retrying transient provider failures.
//!
//! Providers fail constantly for uninteresting reasons: overloaded servers,
//! rate limits, dropped connections. The loop should not surface those to the
//! user, but it must also not retry forever, and it must never retry something
//! that cannot succeed.

use std::time::Duration;

use crate::error::{FailureKind, LlmError};

/// How many attempts to make and how long to wait between them.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RetryPolicy {
    /// Total attempts, including the first. One means no retries.
    pub max_attempts: u32,
    pub initial_backoff: Duration,
    /// Backoff never grows past this, so a long outage does not turn into a
    /// multi-minute stall.
    pub max_backoff: Duration,
}

impl Default for RetryPolicy {
    fn default() -> Self {
        Self {
            max_attempts: 4,
            initial_backoff: Duration::from_millis(500),
            max_backoff: Duration::from_secs(30),
        }
    }
}

impl RetryPolicy {
    /// A policy that never retries, for tests and one-shot calls.
    pub fn none() -> Self {
        Self {
            max_attempts: 1,
            ..Self::default()
        }
    }

    /// How long to wait before `attempt`, or `None` to stop.
    ///
    /// `attempt` is one-based: the delay before the second attempt is
    /// `delay_before(2)`.
    pub fn delay_before(&self, attempt: u32, error: &LlmError) -> Option<Duration> {
        if attempt > self.max_attempts || !error.is_retryable() {
            return None;
        }

        // A provider that told us how long to wait knows better than we do.
        if let Some(requested) = error.retry_after() {
            return Some(requested.min(self.max_backoff));
        }

        // Exponential backoff on the number of retries already made.
        let exponent = attempt.saturating_sub(2);
        let scale = 2u32.saturating_pow(exponent.min(16));
        let delay = self
            .initial_backoff
            .saturating_mul(scale)
            .min(self.max_backoff);

        // Rate limits without a Retry-After deserve a longer initial pause.
        if error.kind() == FailureKind::RateLimited {
            return Some((delay * 2).min(self.max_backoff));
        }
        Some(delay)
    }

    /// Whether another attempt should be made after `error` on `attempt`.
    pub fn should_retry(&self, attempt: u32, error: &LlmError) -> bool {
        self.delay_before(attempt + 1, error).is_some()
    }
}

/// The shared HTTP retry loop, parameterised by the request builder.
///
/// Both provider clients (Anthropic and Copilot) use the same pattern: build a
/// request, send it, classify the response, and retry on transient errors.
/// The only difference is how the request is built (which headers, URL, body),
/// so the policy + error handling lives here and the builder is a closure.
///
/// `make_builder` is called fresh on every attempt because `reqwest::RequestBuilder`
/// is not `Clone`; the URL, body, and auth headers must all be captured by the
/// closure.
///
/// The `provider` label is used only in tracing — pass a short identifier such
/// as `"anthropic"` or `"copilot"`.
pub(crate) async fn send_with_retry<F>(
    policy: &RetryPolicy,
    provider: &str,
    mut make_builder: F,
) -> Result<reqwest::Response, LlmError>
where
    F: FnMut() -> reqwest::RequestBuilder,
{
    let mut attempt = 1u32;
    loop {
        let error = match make_builder().send().await {
            Ok(response) if response.status().is_success() => return Ok(response),
            Ok(response) => {
                let status = response.status().as_u16();
                let retry_after = response
                    .headers()
                    .get("retry-after")
                    .and_then(|v| v.to_str().ok())
                    .and_then(crate::error::parse_retry_after);
                let body = response.text().await.unwrap_or_default();
                LlmError::from_status(status, &body, retry_after)
            }
            Err(e) if e.is_timeout() => LlmError::Transport(format!("request timed out: {e}")),
            Err(e) => LlmError::Transport(e.to_string()),
        };

        match policy.delay_before(attempt + 1, &error) {
            Some(delay) => {
                tracing::warn!(
                    attempt,
                    ?delay,
                    %error,
                    "{provider} request failed; retrying"
                );
                tokio::time::sleep(delay).await;
                attempt += 1;
            }
            None => return Err(error),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn transient() -> LlmError {
        LlmError::Api {
            status: 503,
            message: "overloaded".into(),
            kind: FailureKind::Transient,
            retry_after: None,
            body: None,
        }
    }

    fn permanent() -> LlmError {
        LlmError::Api {
            status: 400,
            message: "bad request".into(),
            kind: FailureKind::Permanent,
            retry_after: None,
            body: None,
        }
    }

    fn rate_limited(retry_after: Option<Duration>) -> LlmError {
        LlmError::Api {
            status: 429,
            message: "slow down".into(),
            kind: FailureKind::RateLimited,
            retry_after,
            body: None,
        }
    }

    #[test]
    fn a_permanent_failure_is_never_retried() {
        let policy = RetryPolicy::default();
        assert!(policy.delay_before(2, &permanent()).is_none());
        assert!(!policy.should_retry(1, &permanent()));
    }

    #[test]
    fn a_transient_failure_is_retried() {
        let policy = RetryPolicy::default();
        assert!(policy.delay_before(2, &transient()).is_some());
        assert!(policy.should_retry(1, &transient()));
    }

    #[test]
    fn retries_stop_at_the_attempt_limit() {
        let policy = RetryPolicy {
            max_attempts: 3,
            ..RetryPolicy::default()
        };
        assert!(policy.delay_before(2, &transient()).is_some());
        assert!(policy.delay_before(3, &transient()).is_some());
        assert!(
            policy.delay_before(4, &transient()).is_none(),
            "a fourth attempt exceeds the limit of three"
        );
    }

    #[test]
    fn a_no_retry_policy_makes_a_single_attempt() {
        let policy = RetryPolicy::none();
        assert!(policy.delay_before(2, &transient()).is_none());
        assert!(!policy.should_retry(1, &transient()));
    }

    #[test]
    fn backoff_grows_exponentially() {
        let policy = RetryPolicy {
            initial_backoff: Duration::from_millis(100),
            max_attempts: 10,
            max_backoff: Duration::from_secs(60),
        };

        assert_eq!(
            policy.delay_before(2, &transient()),
            Some(Duration::from_millis(100))
        );
        assert_eq!(
            policy.delay_before(3, &transient()),
            Some(Duration::from_millis(200))
        );
        assert_eq!(
            policy.delay_before(4, &transient()),
            Some(Duration::from_millis(400))
        );
    }

    #[test]
    fn backoff_is_capped() {
        let policy = RetryPolicy {
            initial_backoff: Duration::from_secs(1),
            max_attempts: 30,
            max_backoff: Duration::from_secs(5),
        };

        for attempt in 2..25 {
            let delay = policy.delay_before(attempt, &transient()).expect("a delay");
            assert!(
                delay <= Duration::from_secs(5),
                "attempt {attempt} waited {delay:?}, past the cap"
            );
        }
    }

    #[test]
    fn a_rate_limit_waits_longer_than_a_plain_transient_failure() {
        let policy = RetryPolicy::default();
        let transient_delay = policy.delay_before(2, &transient()).expect("a delay");
        let limited_delay = policy
            .delay_before(2, &rate_limited(None))
            .expect("a delay");

        assert!(
            limited_delay > transient_delay,
            "backing off harder on a rate limit avoids compounding it"
        );
    }

    #[test]
    fn an_explicit_retry_after_is_honoured() {
        let policy = RetryPolicy::default();
        let delay = policy
            .delay_before(2, &rate_limited(Some(Duration::from_secs(12))))
            .expect("a delay");

        assert_eq!(
            delay,
            Duration::from_secs(12),
            "the provider's own guidance should win"
        );
    }

    #[test]
    fn an_absurd_retry_after_is_still_capped() {
        let policy = RetryPolicy {
            max_backoff: Duration::from_secs(10),
            ..RetryPolicy::default()
        };
        let delay = policy
            .delay_before(2, &rate_limited(Some(Duration::from_secs(600))))
            .expect("a delay");

        assert_eq!(delay, Duration::from_secs(10));
    }

    #[test]
    fn a_cancelled_request_is_never_retried() {
        let policy = RetryPolicy::default();
        assert!(policy.delay_before(2, &LlmError::Cancelled).is_none());
    }

    #[test]
    fn a_dropped_stream_is_retried() {
        let policy = RetryPolicy::default();
        assert!(policy.delay_before(2, &LlmError::IncompleteStream).is_some());
    }

    #[test]
    fn an_authentication_failure_is_never_retried() {
        let policy = RetryPolicy::default();
        assert!(policy
            .delay_before(2, &LlmError::Unauthorized("no token".into()))
            .is_none());
    }
}
