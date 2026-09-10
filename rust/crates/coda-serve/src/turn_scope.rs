//! Which API turn a unit of execution belongs to.
//!
//! # Why this exists
//!
//! `ServeHost` hands **one** `PermissionPrompt`/`PromptChannel` to the agent
//! loop, to `SubagentHost`, and to the schedule runtime. Background subagents
//! and scheduled runs are `tokio::spawn`ed (`SubagentHost::spawn`), so they
//! outlive the turn that started them and can raise a `request/permission`
//! at any moment — including in the middle of an unrelated *later* turn.
//!
//! Attributing such a request to "whatever `EngineState.turn` happens to hold
//! right now" invents provenance twice over:
//!
//! - `PendingRequestDto.turnId` names a turn the request has nothing to do
//!   with, so a client correlating requests to turns correlates them wrongly;
//! - the phase machine parks that turn in `awaitingUserInput`, telling every
//!   client the foreground turn is blocked on a human when it is streaming
//!   perfectly happily — and, symmetrically, one long-lived background
//!   approval used to pin a turn there for the rest of its life.
//!
//! # Why a task-local, and why a *separate* one
//!
//! The scope must follow the execution that actually issued the request, and
//! must **not** be inherited by detached work. A `tokio::task_local!` is
//! exactly that: `Agent::run` awaits tool execution (and therefore the
//! permission prompt) inline on the same task, while `tokio::spawn` starts a
//! fresh task that sees nothing. Foreground subagents, which are awaited
//! inline, correctly inherit the turn they belong to; background ones
//! correctly do not.
//!
//! `coda_diagnostics` has a task-local of the same shape, but it is
//! deliberately **not** reused: diagnostics are optional (`--log-file` may be
//! absent, and `ServeHost::diagnostics` is an `Option`), and an identity model
//! that silently degrades when logging is switched off is not an identity
//! model. This one is always installed around a turn.
//!
//! # The rule
//!
//! Unknown origin is `None`. It is never back-filled from ambient state, and
//! a `None` request never moves a turn's public phase.

use std::future::Future;

tokio::task_local! {
    /// The API `turnId` the current execution is part of.
    static EXECUTION_TURN: String;
}

/// Runs `fut` attributed to `turn_id`.
///
/// Applied around the agent run, so everything that run awaits inline — tool
/// permission checks, `ask_user_question`, plan approval, goal escalation,
/// foreground subagents — is attributed to it, and everything it detaches is
/// not.
pub async fn in_turn<F: Future>(turn_id: impl Into<String>, fut: F) -> F::Output {
    EXECUTION_TURN.scope(turn_id.into(), fut).await
}

/// The turn the calling execution belongs to, or `None` when it belongs to
/// none — a background subagent, a scheduled run, or anything else detached
/// from a turn.
pub fn current() -> Option<String> {
    EXECUTION_TURN.try_with(|t| t.clone()).ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn outside_any_turn_the_origin_is_unknown() {
        assert_eq!(current(), None);
    }

    #[tokio::test]
    async fn inside_a_turn_the_origin_is_that_turn() {
        let seen = in_turn("t1", async { current() }).await;
        assert_eq!(seen.as_deref(), Some("t1"));
    }

    #[tokio::test]
    async fn work_awaited_inline_inherits_the_turn() {
        // A foreground subagent is awaited by the turn that spawned it, so it
        // really is part of that turn.
        async fn nested() -> Option<String> {
            tokio::task::yield_now().await;
            current()
        }
        let seen = in_turn("t1", async { nested().await }).await;
        assert_eq!(seen.as_deref(), Some("t1"), "an inline await is the same execution");
    }

    /// The property the whole module exists for: detached work must **not**
    /// inherit the turn. `SubagentHost::spawn` uses exactly this shape for a
    /// background subagent.
    #[tokio::test]
    async fn detached_work_does_not_inherit_the_turn() {
        let seen = in_turn("t1", async {
            tokio::spawn(async { current() }).await.expect("the detached task runs")
        })
        .await;
        assert_eq!(
            seen, None,
            "a background subagent must not borrow the identity of the turn that started it"
        );
    }

    #[tokio::test]
    async fn the_scope_does_not_leak_past_the_turn() {
        in_turn("t1", async {}).await;
        assert_eq!(current(), None, "the scope ends with the run it wrapped");
    }

    #[tokio::test]
    async fn concurrent_turns_do_not_see_each_other() {
        let (a, b) = tokio::join!(
            in_turn("t-a", async {
                tokio::task::yield_now().await;
                current()
            }),
            in_turn("t-b", async {
                tokio::task::yield_now().await;
                current()
            })
        );
        assert_eq!(a.as_deref(), Some("t-a"));
        assert_eq!(b.as_deref(), Some("t-b"));
    }
}
