//! Timeline chronology, end to end: the real engine state accepts records and
//! projects `session/getHistory`; this client reads it through its real
//! rebuild path. A count of cards alone would miss the original bug, so
//! every test checks the order.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Instant;

use coda_llm::Message;
use coda_proto::presentation::{PresentationPayload, PresentationRecord};
use coda_proto::requests::GetHistoryParams;
use coda_proto::state::{ActiveConfig, ActivityPhase};
use coda_serve::bus::EventBus;
use coda_serve::history::{build_live_result, CommittedPage};
use coda_serve::state::{EngineState, TurnEnd};
use serde_json::{json, Value};

use super::serve::tests::{app_with, Harness};
use crate::local::AccessMode;
use crate::state::{UiEvent, UiState};
use crate::transcript::{Block, HISTORICAL_MARKER};

fn config() -> ActiveConfig {
    ActiveConfig {
        provider_id: None,
        model: "fixture-model".into(),
        effort: None,
        effort_is_auto: true,
        permission_mode: "default".into(),
        system_prompt_source: "default".into(),
    }
}

/// The engine side: its real state, a committed conversation, and the read
/// it would serve.
struct Engine {
    state: Arc<EngineState>,
    history: Vec<Message>,
    turns: u32,
    instance: String,
    _wire: tokio::sync::mpsc::UnboundedReceiver<Vec<u8>>,
}

impl Engine {
    fn new(instance: &str) -> Self {
        let (tx, rx) = tokio::sync::mpsc::unbounded_channel();
        let bus = Arc::new(EventBus::new(tx, instance));
        let state = Arc::new(EngineState::new(bus, "s1", ".", HashMap::new(), config()));
        Self { state, history: Vec::new(), turns: 0, instance: instance.into(), _wire: rx }
    }

    fn begin(&mut self, prompt: &str) -> String {
        self.turns += 1;
        let turn = format!("turn-{}", self.turns);
        assert!(self.state.begin_turn(turn.clone(), prompt, config(), ActivityPhase::Preparing));
        turn
    }

    fn commit(&mut self, turn: String, prompt: &str, answer: &str) {
        self.history.push(Message::user(prompt));
        self.history.push(Message::assistant(answer));
        self.state.end_turn(TurnEnd {
            turn_id: turn,
            history_length: Some(self.history.len() as i64),
            ..Default::default()
        });
    }

    fn exchange(&mut self, prompt: &str, answer: &str) {
        let turn = self.begin(prompt);
        self.commit(turn, prompt, answer);
    }

    fn complete(&self, task: &str, title: &str) -> PresentationRecord {
        self.state
            .accept_presentation(
                format!("task:{}:{task}", self.instance),
                PresentationPayload::TaskCompletion {
                    task_id: task.into(),
                    status: "completed".into(),
                    task_kind: Some("agent".into()),
                    title: Some(title.into()),
                    instructions: format!("instructions for {title}"),
                },
            )
            .expect("a new occurrence is accepted")
    }

    /// Exactly what `session/getHistory` would answer.
    fn read(&self) -> Value {
        let page = CommittedPage { total: self.history.len() as i64, start: 0, messages: self.history.clone() };
        let result = build_live_result(&GetHistoryParams::default(), &page, &self.state.history_view())
            .expect("the read is served");
        serde_json::to_value(result).expect("the read serializes")
    }
}

/// The client, wired to answer `session/getHistory` with whatever `read`
/// currently holds, and to record every request it makes.
fn client(read: &Arc<Mutex<Value>>) -> Harness {
    let read = Arc::clone(read);
    app_with(AccessMode::TrustedLocal, move |method, _| match method {
        "session/getHistory" => read.lock().expect("read").clone(),
        _ => json!({}),
    })
}

fn shape(blocks: &[Block]) -> Vec<String> {
    blocks
        .iter()
        .filter_map(|block| match block {
            Block::User { text, .. } => Some(format!("user: {text}")),
            Block::Assistant { text, .. } => Some(format!("assistant: {text}")),
            Block::TaskCompletion { title, task_id, .. } => {
                Some(format!("card: {}", title.as_deref().unwrap_or(task_id)))
            }
            Block::AgentMessage { text, .. } => Some(format!("message: {text}")),
            Block::TaskCompletionReport { task_id, .. } => {
                Some(format!("report: {}", task_id.as_deref().unwrap_or("?")))
            }
            Block::HistoryNotice { text, .. } if text == HISTORICAL_MARKER => Some("earlier".into()),
            _ => None,
        })
        .collect()
}

async fn refresh(harness: &mut Harness, engine: &Engine, read: &Arc<Mutex<Value>>) -> Vec<String> {
    *read.lock().expect("read") = engine.read();
    harness.app.rehydrate_at(Instant::now()).await;
    shape(harness.app.state.transcript.blocks())
}

#[tokio::test]
async fn an_old_completion_stays_where_it_happened_across_refreshes() {
    let mut engine = Engine::new("engine-1");
    let read = Arc::new(Mutex::new(json!({})));
    let mut harness = client(&read);

    engine.exchange("Run a background check", "Started it.");
    engine.complete("t1", "background check");
    engine.exchange("Unrelated work", "Unrelated answer");

    let expected = [
        "user: Run a background check",
        "assistant: Started it.",
        "card: background check",
        "user: Unrelated work",
        "assistant: Unrelated answer",
    ];
    for _ in 0..3 {
        assert_eq!(refresh(&mut harness, &engine, &read).await, expected);
    }
    engine.exchange("More work", "More answer");
    for _ in 0..2 {
        let shape = refresh(&mut harness, &engine, &read).await;
        assert_eq!(shape[..5], expected, "the card did not move: {shape:?}");
        assert_eq!(shape.iter().filter(|s| s.starts_with("card")).count(), 1);
    }

    // Recovery is display-only: no hint, and nothing but the reads went to
    // the engine — no prompt, no delivery, no schedule, no task.
    assert!(harness.app.state.hints.current(Instant::now()).is_none(), "a refresh raised a hint");
    let calls = harness.calls.lock().expect("calls").clone();
    assert!(calls.iter().all(|method| method == "session/getHistory"), "{calls:?}");
}

#[tokio::test]
async fn a_live_record_and_its_snapshot_converge_in_either_order() {
    let mut engine = Engine::new("engine-1");
    engine.exchange("Run a background check", "Started it.");

    // Live first, then the snapshot.
    let read = Arc::new(Mutex::new(json!({})));
    let mut live_first = client(&read);
    refresh(&mut live_first, &engine, &read).await;
    let record = engine.complete("t1", "background check");
    live_first.app.sync_timeline();
    live_first.app.state.apply(UiEvent::Engine(coda_proto::Event::PresentationRecord(Box::new(record.clone()))));
    assert!(live_first.app.state.hints.current(Instant::now()).is_some(), "a new live record says so");
    engine.exchange("Unrelated work", "Unrelated answer");
    let a = refresh(&mut live_first, &engine, &read).await;

    // Snapshot first, then a duplicate live delivery.
    let read2 = Arc::new(Mutex::new(json!({})));
    let mut snapshot_first = client(&read2);
    let b = refresh(&mut snapshot_first, &engine, &read2).await;
    snapshot_first.app.state.apply(UiEvent::Engine(coda_proto::Event::PresentationRecord(Box::new(record))));
    assert!(snapshot_first.app.state.hints.current(Instant::now()).is_none(), "a known record is not news");
    let b_after = shape(snapshot_first.app.state.transcript.blocks());

    assert_eq!(a, b);
    assert_eq!(b, b_after, "a duplicate live delivery changed nothing");
    assert_eq!(a.iter().filter(|s| s.starts_with("card")).count(), 1);
}

#[tokio::test]
async fn a_reused_task_number_from_a_restarted_engine_is_a_second_card() {
    let first = Engine::new("engine-1");
    let second = Engine::new("engine-2");
    let a = first.complete("t1", "nightly");
    let b = second.complete("t1", "nightly");
    assert_ne!(a.item_id, b.item_id);

    let mut state = UiState::new();
    for record in [a, b] {
        state.apply(UiEvent::Engine(coda_proto::Event::PresentationRecord(Box::new(record))));
    }
    assert_eq!(shape(state.transcript.blocks()), ["card: nightly", "card: nightly"]);
    // Folding one does not fold the other.
    assert!(state.transcript.toggle_task_card(0));
    let rows: Vec<String> = state
        .transcript
        .render(60, coda_render::tool::ToolDisplayMode::Summary)
        .iter()
        .map(|row| row.text.clone())
        .collect();
    assert_eq!(rows.iter().filter(|row| row.contains("instructions for nightly")).count(), 1);
}

#[tokio::test]
async fn a_record_during_a_stream_waits_for_the_turn_boundary() {
    let mut engine = Engine::new("engine-1");
    let mut state = UiState::new();
    state.apply(UiEvent::Submitted { text: "Long task".into() });
    let turn = engine.begin("Long task");
    state.apply(UiEvent::Engine(coda_proto::Event::AssistantText { delta: "Working on".into() }));

    let record = engine.complete("t9", "side job");
    assert!(record.anchor.turn_id.is_some(), "a record of a running turn waits for its end");
    state.apply(UiEvent::Engine(coda_proto::Event::PresentationRecord(Box::new(record))));
    state.apply(UiEvent::Engine(coda_proto::Event::AssistantText { delta: " it".into() }));
    assert_eq!(
        shape(state.transcript.blocks()),
        ["user: Long task", "assistant: Working on it"],
        "the reply was not split and the card did not take over the stream"
    );
    assert!(state.hints.current(Instant::now()).is_some(), "the completion is still announced");

    engine.commit(turn, "Long task", "Working on it");
    state.apply(UiEvent::TurnFinished { interrupted: false, error: None });
    assert_eq!(
        shape(state.transcript.blocks()),
        ["user: Long task", "assistant: Working on it", "card: side job"]
    );
}

/// Deliveries duplicated and snapshots taken at arbitrary points must end
/// at the same projection as one clean read.
#[tokio::test]
async fn any_interleaving_of_live_records_and_snapshots_converges() {
    let mut engine = Engine::new("engine-1");
    let mut script = Vec::new();
    for round in 0..4 {
        engine.exchange(&format!("prompt {round}"), &format!("answer {round}"));
        let record = engine.complete(&format!("t{round}"), &format!("task {round}"));
        script.push((engine.read(), record));
    }
    let reference = {
        let read = Arc::new(Mutex::new(json!({})));
        let mut harness = client(&read);
        refresh(&mut harness, &engine, &read).await
    };
    assert_eq!(reference.iter().filter(|s| s.starts_with("card")).count(), 4);

    let mut seed = 0x2545_F491_4F6C_DD1Du64;
    let mut next = move || {
        seed ^= seed << 13;
        seed ^= seed >> 7;
        seed ^= seed << 17;
        seed
    };
    for _ in 0..25 {
        let read = Arc::new(Mutex::new(json!({})));
        let mut harness = client(&read);
        for (snapshot, record) in &script {
            let event = || UiEvent::Engine(coda_proto::Event::PresentationRecord(Box::new(record.clone())));
            if next() % 2 == 0 {
                harness.app.sync_timeline();
                harness.app.state.apply(event());
            }
            if next() % 3 == 0 {
                *read.lock().expect("read") = snapshot.clone();
                harness.app.rehydrate_at(Instant::now()).await;
            }
            if next() % 4 == 0 {
                harness.app.state.apply(event());
            }
        }
        let converged = refresh(&mut harness, &engine, &read).await;
        assert_eq!(converged, reference);
    }
}

#[tokio::test]
async fn a_rewound_record_is_historical_not_fresh() {
    let mut engine = Engine::new("engine-1");
    let read = Arc::new(Mutex::new(json!({})));
    let mut harness = client(&read);
    engine.exchange("q1", "a1");
    engine.exchange("q2", "a2");
    engine.complete("t1", "late");
    engine.history.truncate(2);
    engine.state.session_changed("rewind", "s1", 2);
    harness.app.view.on_history_reset(engine.state.history_view().history_epoch, 2);
    let shape = refresh(&mut harness, &engine, &read).await;
    assert_eq!(shape, ["earlier", "card: late", "user: q1", "assistant: a1"]);
}

/// The engine-side runtime (task manager, message bus) is wired in only to
/// produce the exact frame and record a real engine would; the client still
/// reads it through `session/getHistory` alone.
#[cfg(test)]
mod engine_chain {
    use super::*;

    /// The real chain for a background completion: a main-owned shell task ends
    /// in the background, `MainInboxCompletionPush` queues its frame, the
    /// running turn drains it into the live history, the engine serves
    /// `session/getHistory`, and this client rebuilds from it — live, after the
    /// turn commits, after a save/load round trip, and after a restart that
    /// reuses the task number. Every time: one collapsed card, never the frame
    /// as an operator prompt, and the model's history still holds the frame.
    #[tokio::test]
    async fn a_background_completion_frame_is_never_shown_as_an_operator_prompt() {
        use coda_agent::tasks::{MainInboxCompletionPush, TaskExecutionMode, TaskKind, TaskManager};
        use coda_agent::MessageBus;

        struct Runtime {
            tasks: Arc<TaskManager>,
            bus: Arc<MessageBus>,
        }
        fn runtime(engine: &Engine) -> Runtime {
            let tasks = TaskManager::with_defaults("s1");
            let bus = Arc::new(MessageBus::new());
            bus.set_presentation_scope(engine.instance.clone());
            tasks.set_completion_listener(MainInboxCompletionPush::new(&bus));
            let publisher = coda_serve::task_events::TaskEventPublisher::new(Arc::clone(engine.state.bus_ref()));
            publisher.set_presentation(&engine.state);
            tasks.set_event_observer(publisher);
            Runtime { tasks, bus }
        }
        /// A shell task the agent stopped waiting for finishes in the
        /// background; the next turn drains its completion. Returns the frame
        /// the model received.
        fn finish_in_background(engine: &mut Engine, rt: &Runtime, line: &str) -> (String, coda_llm::Message) {
            let task = rt
                .tasks
                .register(TaskKind::Shell, "synthetic job", "Start-Sleep 3; emit synthetic lines", None, TaskExecutionMode::Foreground)
                .expect("registered");
            rt.tasks.append_output(&task.id, &format!("{line}\n"));
            task.try_promote_to_background().expect("backgrounded");
            rt.tasks.complete(&task.id, Some("exit code: 0".into()));
            let turn = engine.begin("");
            let drained = rt.bus.take_main_for_delivery();
            assert_eq!(drained.len(), 1, "one completion is queued for the main agent");
            engine.state.main_messages_delivered(&drained);
            let message = coda_llm::Message::user(drained[0].injected_text()).with_origin(drained[0].origin());
            (turn, message)
        }
        fn commit(engine: &mut Engine, turn: String, frame: coda_llm::Message, answer: &str) {
            engine.history.push(frame);
            engine.history.push(Message::assistant(answer));
            engine.state.end_turn(TurnEnd {
                turn_id: turn,
                history_length: Some(engine.history.len() as i64),
                ..Default::default()
            });
        }
        let frames = |shape: &[String]| shape.iter().filter(|s| s.contains("[task-completed]")).count();

        let mut engine = Engine::new("engine-1");
        let rt = runtime(&engine);
        let read = Arc::new(Mutex::new(json!({})));
        let mut harness = client(&read);
        engine.exchange("Start the synthetic job", "Started it in the background.");

        // Live: the completion was delivered into the running turn.
        let (turn, frame) = finish_in_background(&mut engine, &rt, "synthetic output line 1");
        assert!(frame.text().contains("synthetic output line 1"), "the model gets the output tail");
        assert!(frame.text().contains("<task_output>") && frame.text().contains("exit code: 0"));
        let live = refresh(&mut harness, &engine, &read).await;
        assert_eq!(frames(&live), 0, "live: {live:?}");
        assert!(!live.iter().any(|s| s.starts_with("report")), "live: {live:?}");

        // Committed: one card where it happened, and the reply after it.
        commit(&mut engine, turn, frame, "The job finished.");
        let expected = [
            "user: Start the synthetic job",
            "assistant: Started it in the background.",
            "card: synthetic job",
            "assistant: The job finished.",
        ];
        for _ in 0..3 {
            assert_eq!(refresh(&mut harness, &engine, &read).await, expected);
        }
        let card = harness
            .app
            .state
            .transcript
            .blocks()
            .iter()
            .position(|b| matches!(b, Block::TaskCompletion { .. }))
            .expect("the card");
        assert_eq!(harness.app.state.transcript.render(80, coda_render::tool::ToolDisplayMode::Summary)
            .iter().filter(|r| r.text.contains("synthetic job") && !r.text.contains("Start the")).count(), 1, "collapsed: one row");
        assert!(harness.app.state.transcript.toggle_task_card(card));

        // The model's own history still holds the full frame.
        assert!(engine.history[2].text().starts_with("[task-completed]"));
        assert!(engine.history[2].text().contains("synthetic output line 1"));

        // Save and load the transcript: provenance survives, the view is the same,
        // and the card stays expanded where it was.
        let saved = coda_agent::session::message_json::serialize_messages(&engine.history);
        engine.history = coda_agent::session::message_json::deserialize_messages(&saved);
        assert!(engine.history[2].origin.is_some());
        assert_eq!(refresh(&mut harness, &engine, &read).await, expected);
        let rows: Vec<String> = harness
            .app
            .state
            .transcript
            .render(80, coda_render::tool::ToolDisplayMode::Summary)
            .iter()
            .map(|r| r.text.clone())
            .collect();
        assert!(rows.iter().any(|r| r.contains("Start-Sleep 3; emit synthetic lines")), "still expanded");

        // A restarted engine reuses the task number and the title; its timeline
        // does not hold the old card. The old frame becomes one historical
        // report, the new one its own card — nothing is matched by title or by
        // the bare task id.
        let mut restarted = Engine::new("engine-2");
        restarted.history = engine.history.clone();
        restarted.state.session_changed("resume", "s1", restarted.history.len() as i64);
        let rt2 = runtime(&restarted);
        let (turn, frame) = finish_in_background(&mut restarted, &rt2, "synthetic output line 2");
        commit(&mut restarted, turn, frame, "Second run finished.");
        harness.app.view.on_history_reset(restarted.state.history_view().history_epoch, restarted.history.len() as i64);
        let shape = refresh(&mut harness, &restarted, &read).await;
        assert_eq!(frames(&shape), 0, "{shape:?}");
        assert_eq!(shape.iter().filter(|s| s.starts_with("card")).count(), 1, "{shape:?}");
        assert_eq!(shape.iter().filter(|s| s.starts_with("report")).count(), 1, "{shape:?}");
        let report = shape.iter().position(|s| s.starts_with("report")).unwrap();
        assert_eq!(shape[report - 1], "assistant: Started it in the background.", "{shape:?}");

        // The operator pasting a whole frame is still the operator speaking.
        let pasted = engine.history[2].text();
        restarted.exchange(&pasted, "That is a pasted report.");
        let shape = refresh(&mut harness, &restarted, &read).await;
        assert_eq!(frames(&shape), 1, "{shape:?}");
        assert!(shape.contains(&format!("user: {pasted}")));
    }
}
