//! Session forking and history rewinding.
//!
//! Mirrors C# `Coda.Sdk.SessionForking` (fork) and
//! `Coda.Tui.Commands.RewindCommand` (rewind semantics).

use coda_llm::Message;

use super::audit::SessionAuditStore;
use super::ids;
use super::store::SessionTranscriptStore;

// ─────────────────────────────────────────────────────────────────────────────
// Fork
// ─────────────────────────────────────────────────────────────────────────────

/// Fork `source_id` into a fresh session id under `working_dir`.
///
/// Persists `messages` as the new session's transcript, and copies the
/// source's audit sidecar so the fork is fully auditable.  The source is
/// never modified.  Both the transcript write and the audit copy are
/// best-effort — a disk fault on either is swallowed, and the live session
/// lazily re-persists its transcript on its next turn if the initial write
/// failed.  Returns the new session id.
pub async fn fork(
    working_dir: &str,
    source_id: Option<&str>,
    messages: &[Message],
    system_prompt_override: Option<&str>,
) -> String {
    let new_id = ids::new_id();

    // Best-effort transcript write.
    let transcript_store = SessionTranscriptStore::new(working_dir);
    let _ = transcript_store.save(&new_id, messages, system_prompt_override).await;

    // Best-effort audit copy.
    if let Some(src) = source_id {
        let audit_store = SessionAuditStore::new(working_dir);
        audit_store.copy(src, &new_id);
    }

    new_id
}

// ─────────────────────────────────────────────────────────────────────────────
// Rewind
// ─────────────────────────────────────────────────────────────────────────────

/// Remove the last `n` user exchanges from `history`.
///
/// Each "exchange" is defined as a user turn and all subsequent messages up to
/// (but not including) the previous user turn — matching C# `RewindCommand`.
///
/// Returns the number of exchanges actually removed (may be less than `n` if
/// history is exhausted before `n` exchanges are found).  A `n` of zero is a
/// no-op.  An empty history is a no-op and returns 0.
pub fn rewind(history: &mut Vec<Message>, n: usize) -> usize {
    let mut removed = 0;
    for _ in 0..n {
        let Some(user_idx) = find_last_user_index(history) else { break };
        let count = history.len() - user_idx;
        history.drain(user_idx..);
        let _ = count; // drain already removes them
        removed += 1;
    }
    removed
}

fn find_last_user_index(history: &[Message]) -> Option<usize> {
    history
        .iter()
        .enumerate()
        .rev()
        .find(|(_, m)| m.role == coda_llm::Role::User)
        .map(|(i, _)| i)
}

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use coda_llm::Message;

    fn user(text: &str) -> Message {
        Message::user(text)
    }
    fn assistant(text: &str) -> Message {
        Message::assistant(text)
    }

    // ── rewind ────────────────────────────────────────────────────────────────

    #[test]
    fn rewind_removes_last_exchange() {
        let mut h = vec![user("q1"), assistant("a1"), user("q2"), assistant("a2")];
        let removed = rewind(&mut h, 1);
        assert_eq!(removed, 1);
        assert_eq!(h.len(), 2);
        assert_eq!(h[0].text(), "q1");
        assert_eq!(h[1].text(), "a1");
    }

    #[test]
    fn rewind_removes_two_exchanges() {
        let mut h =
            vec![user("q1"), assistant("a1"), user("q2"), assistant("a2"), user("q3"), assistant("a3")];
        let removed = rewind(&mut h, 2);
        assert_eq!(removed, 2);
        assert_eq!(h.len(), 2);
        assert_eq!(h[0].text(), "q1");
        assert_eq!(h[1].text(), "a1");
    }

    #[test]
    fn rewind_on_empty_is_noop() {
        let mut h: Vec<Message> = Vec::new();
        let removed = rewind(&mut h, 1);
        assert_eq!(removed, 0);
        assert!(h.is_empty());
    }

    #[test]
    fn rewind_more_than_available_drains_to_empty() {
        let mut h = vec![user("q1"), assistant("a1"), user("q2")];
        let removed = rewind(&mut h, 10);
        assert_eq!(removed, 2, "only 2 user turns existed");
        assert!(h.is_empty());
    }

    #[test]
    fn rewind_zero_is_noop() {
        let mut h = vec![user("q1"), assistant("a1")];
        let removed = rewind(&mut h, 0);
        assert_eq!(removed, 0);
        assert_eq!(h.len(), 2);
    }

    #[test]
    fn rewind_removes_only_from_last_user_turn() {
        // [user, assistant, user, assistant, assistant] — last user is at index 2
        let mut h = vec![user("q1"), assistant("a1"), user("q2"), assistant("a2"), assistant("follow")];
        let removed = rewind(&mut h, 1);
        assert_eq!(removed, 1);
        // q1, a1 remain
        assert_eq!(h.len(), 2);
    }

    // ── fork ──────────────────────────────────────────────────────────────────

    #[tokio::test]
    async fn fork_creates_new_session() {
        let dir = tempfile::tempdir().unwrap();
        let msgs = vec![user("hello"), assistant("world")];
        let new_id = fork(dir.path().to_str().unwrap(), None, &msgs, None).await;

        let store = SessionTranscriptStore::new(dir.path());
        let loaded = store.load(&new_id).await.unwrap();
        assert_eq!(loaded.len(), 2);
        assert_eq!(loaded[0].text(), "hello");
    }

    #[tokio::test]
    async fn fork_leaves_source_untouched() {
        let dir = tempfile::tempdir().unwrap();
        let src = "source123456";
        let store = SessionTranscriptStore::new(dir.path());
        store.save(src, &[user("original")], None).await.unwrap();

        let msgs = vec![user("original"), assistant("added")];
        let _new_id = fork(dir.path().to_str().unwrap(), Some(src), &msgs, None).await;

        let src_msgs = store.load(src).await.unwrap();
        assert_eq!(src_msgs.len(), 1, "source must be untouched");
        assert_eq!(src_msgs[0].text(), "original");
    }

    #[tokio::test]
    async fn fork_copies_audit_sidecar() {
        let dir = tempfile::tempdir().unwrap();
        use super::super::audit::{AuditTurn, SessionAuditStore};
        use chrono::Utc;

        let src = "source123456";
        let audit = SessionAuditStore::new(dir.path());
        let turn = AuditTurn {
            turn_index: 0,
            ts_utc: Utc::now(),
            provider: "p".into(),
            model: "m".into(),
            input_tokens: 1,
            output_tokens: 1,
            stop_reason: None,
            tool_calls: vec![],
            system_prompt: Some("sys".into()),
            tool_defs: vec![],
        };
        audit.append_turn(src, &turn).await;
        assert!(audit.exists(src));

        let new_id = fork(dir.path().to_str().unwrap(), Some(src), &[user("hi")], None).await;

        // Audit sidecar must have been copied.
        assert!(audit.exists(&new_id), "fork must copy audit sidecar");
    }

    #[tokio::test]
    async fn fork_returns_unique_ids() {
        let dir = tempfile::tempdir().unwrap();
        let msgs = vec![user("hi")];
        let id1 = fork(dir.path().to_str().unwrap(), None, &msgs, None).await;
        let id2 = fork(dir.path().to_str().unwrap(), None, &msgs, None).await;
        assert_ne!(id1, id2);
    }
}
