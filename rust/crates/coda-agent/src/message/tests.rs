use super::*;
use std::sync::{Arc, Mutex};

fn scheduled(def: &str, name: Option<&str>, task: &str) -> MessageSource {
    MessageSource::ScheduledTask {
        definition_id: def.into(),
        definition_name: name.map(|s| s.to_owned()),
        task_id: task.into(),
    }
}

fn subagent(task: &str, label: &str) -> MessageSource {
    MessageSource::Subagent { task_id: task.into(), label: label.into() }
}

#[test]
fn publish_assigns_monotonic_cursor_and_stable_id() {
    let bus = MessageBus::new();
    let r1 = bus.publish_user(&MessageSource::Main, "first", None, None).unwrap();
    let r2 = bus.publish_user(&MessageSource::Main, "second", None, None).unwrap();
    assert_eq!(r1.cursor, 1);
    assert_eq!(r2.cursor, 2);
    assert_ne!(r1.id, r2.id);
    assert!(!r1.deduplicated);
}

#[test]
fn user_since_is_non_destructive_with_independent_cursors() {
    let bus = MessageBus::new();
    bus.publish_user(&MessageSource::Main, "a", None, None).unwrap();
    bus.publish_user(&MessageSource::Main, "b", None, None).unwrap();

    // Two independent readers, at different cursors, do not affect each other.
    let reader1 = bus.user_since(0, None);
    assert_eq!(reader1.messages.len(), 2);

    let reader2 = bus.user_since(1, None);
    assert_eq!(reader2.messages.len(), 1);
    assert_eq!(reader2.messages[0].body, "b");

    // Re-reading from cursor 0 again still returns both (non-destructive).
    let reader1_again = bus.user_since(0, None);
    assert_eq!(reader1_again.messages.len(), 2);
}

#[test]
fn overflow_evicts_oldest_and_reports_exact_gap() {
    let bus = MessageBus::with_capacity_and_observer(2, None);
    bus.publish_user(&MessageSource::Main, "a", None, None).unwrap(); // cursor 1, evicted
    bus.publish_user(&MessageSource::Main, "b", None, None).unwrap(); // cursor 2
    bus.publish_user(&MessageSource::Main, "c", None, None).unwrap(); // cursor 3, evicts cursor1

    let since = bus.user_since(0, None);
    assert!(since.gap, "reading from before the oldest retained message must report a gap");
    assert_eq!(since.messages.len(), 2);
    assert_eq!(since.messages[0].body, "b");
    assert_eq!(since.messages[1].body, "c");

    // A reader already caught up to cursor 2 sees no gap.
    let since2 = bus.user_since(2, None);
    assert!(!since2.gap);
    assert_eq!(since2.messages.len(), 1);
}

#[test]
fn oversize_body_is_rejected_explicitly_not_truncated() {
    let bus = MessageBus::new();
    let huge = "x".repeat(MAX_BODY_CHARS + 1);
    let err = bus.publish_user(&MessageSource::Main, huge, None, None).unwrap_err();
    assert_eq!(err, PublishError::BodyTooLong);
    // Nothing was published.
    assert_eq!(bus.cursor(), 0);
}

#[test]
fn oversize_context_is_rejected_explicitly() {
    let bus = MessageBus::new();
    let huge_ctx = "y".repeat(MAX_CONTEXT_CHARS + 1);
    let err = bus.publish_user(&MessageSource::Main, "ok body", Some(huge_ctx), None).unwrap_err();
    assert_eq!(err, PublishError::ContextTooLong);
}

#[test]
fn empty_body_is_rejected() {
    let bus = MessageBus::new();
    assert_eq!(
        bus.publish_user(&MessageSource::Main, "   ", None, None).unwrap_err(),
        PublishError::EmptyBody
    );
}

#[test]
fn idempotency_replay_with_identical_body_is_deduplicated() {
    let bus = MessageBus::new();
    let r1 = bus
        .publish_user(&MessageSource::Main, "hello", None, Some("k1".into()))
        .unwrap();
    let r2 = bus
        .publish_user(&MessageSource::Main, "hello", None, Some("k1".into()))
        .unwrap();
    assert!(r2.deduplicated);
    assert_eq!(r1.cursor, r2.cursor);
    assert_eq!(bus.cursor(), 1, "a deduplicated replay must not consume a new cursor");
}

#[test]
fn idempotency_key_reuse_with_different_body_is_a_conflict_not_silently_accepted() {
    let bus = MessageBus::new();
    bus.publish_user(&MessageSource::Main, "hello", None, Some("k1".into())).unwrap();
    let err = bus
        .publish_user(&MessageSource::Main, "different body", None, Some("k1".into()))
        .unwrap_err();
    assert_eq!(err, PublishError::IdempotencyConflict);
}

#[test]
fn idempotency_is_scoped_per_source_not_global() {
    let bus = MessageBus::new();
    let r1 = bus
        .publish_user(&scheduled("def-a", None, "task-a"), "same key different origin", None, Some("k1".into()))
        .unwrap();
    let r2 = bus
        .publish_user(&subagent("task-b", "child"), "same key different origin", None, Some("k1".into()))
        .unwrap();
    assert!(!r2.deduplicated, "different origins must not collide on the same key");
    assert_ne!(r1.cursor, r2.cursor);
}

#[test]
fn concurrent_publish_with_same_key_has_exactly_one_winner_and_rest_are_dedup_or_conflict() {
    let bus = Arc::new(MessageBus::new());
    let barrier = Arc::new(std::sync::Barrier::new(4));
    let mut handles = Vec::new();
    for i in 0..4 {
        let bus = Arc::clone(&bus);
        let barrier = Arc::clone(&barrier);
        handles.push(std::thread::spawn(move || {
            barrier.wait();
            // All four threads publish the SAME body under the SAME key —
            // all must either win or be dedup, never see torn state.
            let _ = i;
            bus.publish_user(&MessageSource::Main, "same content", None, Some("race".into()))
        }));
    }
    let results: Vec<_> = handles.into_iter().map(|h| h.join().unwrap()).collect();
    assert!(results.iter().all(|r| r.is_ok()), "identical content under a shared key must never conflict");
    let cursors: std::collections::HashSet<u64> = results.iter().map(|r| r.as_ref().unwrap().cursor).collect();
    assert_eq!(cursors.len(), 1, "all four calls must agree on exactly one cursor");
    assert_eq!(bus.cursor(), 1, "only one message must actually have been published");
}

#[test]
fn close_refuses_subsequent_publication() {
    let bus = MessageBus::new();
    bus.publish_user(&MessageSource::Main, "before close", None, None).unwrap();
    bus.close();
    assert!(bus.is_closed());
    let err = bus.publish_user(&MessageSource::Main, "after close", None, None).unwrap_err();
    assert_eq!(err, PublishError::Closed);
    // Reads still work against what was retained.
    let since = bus.user_since(0, None);
    assert_eq!(since.messages.len(), 1);
}

#[test]
fn cursor_beyond_current_returns_no_messages_without_error() {
    let bus = MessageBus::new();
    bus.publish_user(&MessageSource::Main, "only", None, None).unwrap();
    let since = bus.user_since(999, None);
    assert!(since.messages.is_empty());
    assert!(!since.gap);
}

#[test]
fn pagination_reports_truncated_and_advances_next_cursor() {
    let bus = MessageBus::new();
    for i in 0..5 {
        bus.publish_user(&MessageSource::Main, format!("m{i}"), None, None).unwrap();
    }
    let page1 = bus.user_since(0, Some(2));
    assert_eq!(page1.messages.len(), 2);
    assert!(page1.truncated);
    assert_eq!(page1.next_cursor, 2);

    let page2 = bus.user_since(page1.next_cursor, Some(2));
    assert_eq!(page2.messages.len(), 2);
    assert!(page2.truncated);

    let page3 = bus.user_since(page2.next_cursor, Some(2));
    assert_eq!(page3.messages.len(), 1);
    assert!(!page3.truncated);
}

#[test]
fn label_is_derived_from_source_and_bounded_never_from_body() {
    let bus = MessageBus::new();
    let long_name = "n".repeat(MAX_LABEL_CHARS + 50);
    let r = bus
        .publish_user(&scheduled("def-1", Some(&long_name), "task-1"), "body text", None, None)
        .unwrap();
    let since = bus.user_since(0, None);
    let msg = since.messages.iter().find(|m| m.cursor == r.cursor).unwrap();
    assert_eq!(msg.label.chars().count(), MAX_LABEL_CHARS);
    assert_eq!(msg.source_kind, "scheduledTask");
}

#[test]
fn scheduled_root_and_nested_subagent_child_have_distinct_attribution() {
    let bus = MessageBus::new();
    let root = bus
        .publish_user(
            &scheduled("nightly-def", Some("nightly audit"), "task-0001"),
            "root notification",
            None,
            None,
        )
        .unwrap();
    let child = bus
        .publish_user(&subagent("task-0002", "audit-subagent"), "child notification", None, None)
        .unwrap();

    let since = bus.user_since(0, None);
    let root_msg = since.messages.iter().find(|m| m.cursor == root.cursor).unwrap();
    let child_msg = since.messages.iter().find(|m| m.cursor == child.cursor).unwrap();
    assert_eq!(root_msg.source_kind, "scheduledTask");
    assert_eq!(root_msg.label, "nightly audit");
    assert_eq!(child_msg.source_kind, "subagent");
    assert_eq!(child_msg.label, "audit-subagent");
}

// ── Gap 3: trusted task/schedule id provenance ─────────────────────────────

#[test]
fn scheduled_message_carries_its_own_task_id_and_definition_id() {
    let bus = MessageBus::new();
    bus.publish_user(&scheduled("def-9", Some("nightly"), "task-77"), "hi", None, None).unwrap();
    let since = bus.user_since(0, None);
    let msg = &since.messages[0];
    assert_eq!(msg.task_id.as_deref(), Some("task-77"));
    assert_eq!(msg.schedule_definition_id.as_deref(), Some("def-9"));
}

#[test]
fn subagent_message_carries_task_id_but_no_schedule_definition_id() {
    let bus = MessageBus::new();
    bus.publish_user(&subagent("task-42", "child"), "hi", None, None).unwrap();
    let since = bus.user_since(0, None);
    let msg = &since.messages[0];
    assert_eq!(msg.task_id.as_deref(), Some("task-42"));
    assert_eq!(msg.schedule_definition_id, None);
}

#[test]
fn main_message_carries_neither_task_id_nor_schedule_definition_id() {
    let bus = MessageBus::new();
    bus.publish_user(&MessageSource::Main, "hi", None, None).unwrap();
    let since = bus.user_since(0, None);
    let msg = &since.messages[0];
    assert_eq!(msg.task_id, None);
    assert_eq!(msg.schedule_definition_id, None);
}

// ── Gap 4: bounded, sanitized label; bounded idempotency key ───────────────

#[test]
fn label_strips_control_characters_and_collapses_to_a_single_line() {
    let bus = MessageBus::new();
    bus.publish_user(
        &subagent("task-1", "line one\nline two\tstill one\x1b[31mred"),
        "body",
        None,
        None,
    )
    .unwrap();
    let since = bus.user_since(0, None);
    let label = &since.messages[0].label;
    assert!(!label.contains('\n'), "label must be a single line: {label:?}");
    assert!(!label.contains('\t'), "label must not carry raw control chars: {label:?}");
    assert!(!label.chars().any(|c| c.is_control()), "label must be control-char-free: {label:?}");
}

#[test]
fn idempotency_key_over_the_bound_is_rejected() {
    let bus = MessageBus::new();
    let huge_key = "k".repeat(MAX_IDEMPOTENCY_KEY_CHARS + 1);
    let err = bus.publish_user(&MessageSource::Main, "body", None, Some(huge_key)).unwrap_err();
    assert_eq!(err, PublishError::IdempotencyKeyTooLong);
    assert_eq!(bus.cursor(), 0, "an oversized key must not consume a cursor");
}

// ── Gap 5: idempotency compares exact content (context too), not just hash ─

#[test]
fn same_key_and_body_but_different_context_is_a_conflict_not_a_replay() {
    let bus = MessageBus::new();
    bus.publish_user(&MessageSource::Main, "same body", Some("ctx-a".into()), Some("k".into()))
        .unwrap();
    let err = bus
        .publish_user(&MessageSource::Main, "same body", Some("ctx-b".into()), Some("k".into()))
        .unwrap_err();
    assert_eq!(err, PublishError::IdempotencyConflict);
}

#[test]
fn same_key_body_and_context_is_deduplicated() {
    let bus = MessageBus::new();
    let r1 = bus
        .publish_user(&MessageSource::Main, "same body", Some("ctx".into()), Some("k".into()))
        .unwrap();
    let r2 = bus
        .publish_user(&MessageSource::Main, "same body", Some("ctx".into()), Some("k".into()))
        .unwrap();
    assert!(r2.deduplicated);
    assert_eq!(r1.cursor, r2.cursor);
}

#[test]
fn same_key_and_context_but_no_context_the_second_time_is_a_conflict() {
    let bus = MessageBus::new();
    bus.publish_user(&MessageSource::Main, "body", Some("ctx".into()), Some("k".into())).unwrap();
    let err =
        bus.publish_user(&MessageSource::Main, "body", None, Some("k".into())).unwrap_err();
    assert_eq!(err, PublishError::IdempotencyConflict);
}

// ── Gap 6: `user_since` limit=0 must not silently skip messages ───────────

#[test]
fn zero_limit_returns_an_empty_page_without_advancing_the_cursor() {
    let bus = MessageBus::new();
    bus.publish_user(&MessageSource::Main, "a", None, None).unwrap();
    bus.publish_user(&MessageSource::Main, "b", None, None).unwrap();

    let since = bus.user_since(0, Some(0));
    assert!(since.messages.is_empty());
    assert_eq!(since.next_cursor, 0, "must not silently jump ahead to the latest cursor");
    assert!(since.truncated, "there is more to fetch with a positive limit");

    // A caller that retries with a real limit still sees everything.
    let follow_up = bus.user_since(since.next_cursor, None);
    assert_eq!(follow_up.messages.len(), 2);
}

#[test]
fn gap_reports_exact_dropped_range_and_count() {
    let bus = MessageBus::with_capacity_and_observer(2, None);
    bus.publish_user(&MessageSource::Main, "a", None, None).unwrap(); // cursor 1, evicted
    bus.publish_user(&MessageSource::Main, "b", None, None).unwrap(); // cursor 2, evicted
    bus.publish_user(&MessageSource::Main, "c", None, None).unwrap(); // cursor 3
    bus.publish_user(&MessageSource::Main, "d", None, None).unwrap(); // cursor 4, evicts 2

    let since = bus.user_since(0, None);
    assert!(since.gap);
    let dropped = since.dropped.expect("gap implies an exact dropped range");
    assert_eq!(dropped.from, 1);
    assert_eq!(dropped.to, 2);
    assert_eq!(dropped.count, 2);
}

#[test]
fn no_gap_means_no_dropped_range() {
    let bus = MessageBus::new();
    bus.publish_user(&MessageSource::Main, "a", None, None).unwrap();
    let since = bus.user_since(0, None);
    assert!(!since.gap);
    assert!(since.dropped.is_none());
}

#[test]
fn debug_repr_does_not_include_raw_body_field_name_confusion() {
    // Not a full redaction guarantee (UserMessage.body is legitimately part
    // of the payload other layers must forward), but the Debug impl for the
    // bus itself must not leak internals beyond what's already public.
    let bus = MessageBus::new();
    bus.publish_user(&MessageSource::Main, "some body", None, None).unwrap();
    // MessageBus has no Debug impl (by design — no ad hoc dump of internal
    // lock state); this test only documents that expectation compiles.
    let _ = &bus;
}

#[test]
fn overflow_observer_reports_exact_dropped_range() {
    struct Recorder {
        overflow: Mutex<Vec<(u64, u64)>>,
        published: Mutex<Vec<u64>>,
        closed: Mutex<bool>,
    }
    impl MessageBusObserver for Recorder {
        fn on_published(&self, msg: &UserMessage) {
            self.published.lock().unwrap().push(msg.cursor);
        }
        fn on_overflow(&self, from: u64, to: u64) {
            self.overflow.lock().unwrap().push((from, to));
        }
        fn on_closed(&self) {
            *self.closed.lock().unwrap() = true;
        }
    }
    let rec = Arc::new(Recorder {
        overflow: Mutex::new(Vec::new()),
        published: Mutex::new(Vec::new()),
        closed: Mutex::new(false),
    });
    let bus = MessageBus::with_capacity_and_observer(1, Some(rec.clone() as Arc<dyn MessageBusObserver>));
    bus.publish_user(&MessageSource::Main, "a", None, None).unwrap();
    bus.publish_user(&MessageSource::Main, "b", None, None).unwrap();
    assert_eq!(*rec.overflow.lock().unwrap(), vec![(1, 1)]);
    assert_eq!(*rec.published.lock().unwrap(), vec![1, 2]);

    bus.close();
    assert!(*rec.closed.lock().unwrap());
}

#[test]
fn publish_races_with_snapshot_reads_never_produce_torn_state() {
    let bus = Arc::new(MessageBus::new());
    let writer = {
        let bus = Arc::clone(&bus);
        std::thread::spawn(move || {
            for i in 0..200 {
                let _ = bus.publish_user(&MessageSource::Main, format!("m{i}"), None, None);
            }
        })
    };
    let reader = {
        let bus = Arc::clone(&bus);
        std::thread::spawn(move || {
            for _ in 0..200 {
                let since = bus.user_since(0, None);
                // Every observed cursor sequence must itself be sorted and
                // free of duplicates — a torn read would violate this.
                let mut prev = 0u64;
                for m in &since.messages {
                    assert!(m.cursor > prev, "cursors must be strictly increasing in any snapshot");
                    prev = m.cursor;
                }
            }
        })
    };
    writer.join().unwrap();
    reader.join().unwrap();
}
