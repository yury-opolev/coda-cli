//! Turn interruption: the user's stop control.
//!
//! Split from the event loop because the correctness of this is entirely about
//! *what is known when*, and that argument is easier to keep straight — and to
//! test — in one place.
//!
//! # Why this is a request and not a notification
//!
//! It used to be `connection.notify(...)`. A JSON-RPC notification has no id,
//! so it gets no reply, so nothing on the sending side can tell whether the
//! engine did anything with it — and the engine's transport dropped incoming
//! notifications on the floor entirely. The stop control therefore did nothing
//! at all, while the status bar said `interrupting…` indefinitely, which is
//! the worst of both: no effect, and no way to find out.
//!
//! A request is acknowledged. `Ok` means the engine received the interrupt and
//! acted on its cancellation token; it does **not** mean the turn has already
//! ended, which is why the pinned indicator is settled by the turn's own
//! completion rather than by this reply.

use serde_json::Value;
use tokio::sync::oneshot;

use coda_client::ClientError;
use coda_proto::messages::method;

use super::App;
use crate::transcript::NoticeLevel;
use crate::state::UiEvent;

/// Asks the engine to cancel the running turn.
///
/// Never blocks the render/input loop: the reply is parked on the app and
/// polled by the main select, exactly like a turn's own response.
pub(super) fn request(app: &mut App) {
    if !app.engine_connected {
        return;
    }
    // Coalesce. Holding the control down, or pressing it again while the first
    // request is unanswered, must not queue a second cancellation that could
    // outlive this turn and land on the next one.
    if app.interrupt_ack.is_some() {
        return;
    }

    app.apply(UiEvent::InterruptRequested);

    match app.connection.send_request(method::INTERRUPT, Some(serde_json::json!({}))) {
        Ok(receiver) => app.interrupt_ack = Some(receiver),
        Err(error) => fail(app, &error.to_string()),
    }
}

/// Applies the engine's answer to an interrupt request.
///
/// `result` is the raw oneshot outcome: the outer error is the connection
/// going away before a reply, the inner one is the engine refusing.
pub(super) fn settle(
    app: &mut App,
    result: Result<Result<Value, coda_proto::ResponseError>, oneshot::error::RecvError>,
) {
    let outcome = result
        .map_err(|_| ClientError::ConnectionClosed)
        .and_then(|r| r.map_err(ClientError::Rpc));

    match outcome {
        // Acknowledged. The turn is not necessarily over — the engine has
        // cancelled its token and the operations holding it now have to notice
        // — so the indicator stays up and is cleared by `TurnFinished`, which
        // is the only authority on a turn having actually ended.
        Ok(_) => {}
        Err(error) => fail(app, &error.to_string()),
    }
}

/// Reports a failed interrupt and clears the pending indicator.
///
/// Clearing matters as much as reporting: leaving `interrupting…` up after the
/// request demonstrably failed tells the user cancellation is still coming
/// when nothing is going to happen.
fn fail(app: &mut App, error: &str) {
    app.state.interrupting = false;
    app.notice(format!("Interrupt failed: {error}"), NoticeLevel::Error);
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The wire contract, asserted on the constant the engine dispatches on.
    /// A notification for this method is honoured by the engine only as a
    /// compatibility path for older front-ends; this one sends a request.
    #[test]
    fn the_interrupt_method_is_the_dispatched_one() {
        assert_eq!(method::INTERRUPT, "session/interrupt");
    }
}
