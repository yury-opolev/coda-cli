//! Paste integrity, end to end: console records through the real decoder,
//! into the application, out to a fake engine whose requests are counted.
//!
//! A synthetic `PasteText` alone would only exercise the layer that already
//! worked; these tests start from the records the Windows console delivers.

use std::time::Instant;

use serde_json::{json, Value};

use super::serve::tests::{app_with, notices, Harness};
use crate::input::decoder::{Decoder, KeyRecord, Record};
use crate::input::{InputAction, PasteSource};
use crate::local::AccessMode;
use crate::state::{PendingPrompt, UiEvent};
use crate::surface::prompt::PromptSurface;

/// Each UTF-16 unit as a VT-input key-down record, exactly as the console
/// host delivers characters to a client with virtual terminal input on.
fn raw(text: &str) -> Vec<Record> {
    text.encode_utf16()
        .map(|unit| {
            Record::Key(KeyRecord { vk: 0, unit, down: true, repeat: 1, ..KeyRecord::default() })
        })
        .collect()
}

fn decode(records: Vec<Record>) -> Vec<InputAction> {
    let mut decoder = Decoder::new();
    let now = Instant::now();
    for record in records {
        decoder.feed(record, now);
    }
    std::iter::from_fn(|| decoder.next_action()).collect()
}

fn bracketed(text: &str) -> Vec<InputAction> {
    decode(raw(&format!("\x1b[200~{text}\x1b[201~")))
}

fn engine() -> Harness {
    app_with(AccessMode::TrustedLocal, |method, _| match method {
        "session/steer" => json!({ "ok": true, "messageId": "m1" }),
        _ => json!({}),
    })
}

async fn drive(harness: &mut Harness, actions: Vec<InputAction>) {
    for action in actions {
        harness.app.apply_input(action).await;
    }
}

/// Every request the client wrote with this method, in order.
async fn requests(harness: &Harness, method: &str) -> Vec<Value> {
    harness.settle_wire().await;
    harness
        .frames()
        .into_iter()
        .filter(|frame| frame["method"] == method)
        .collect()
}

async fn submissions(harness: &Harness) -> usize {
    requests(harness, "session/prompt").await.len() + requests(harness, "session/steer").await.len()
}

/// An accented letter and an emoji outside the BMP (a surrogate pair).
fn non_ascii() -> String {
    [0xE9, 0x1F600].into_iter().filter_map(char::from_u32).collect()
}

const PASTE: &str = "First line\r\nSecond line\r\n\r\nFourth line\r\n";
const DRAFT: &str = "First line\nSecond line\n\nFourth line\n";

#[tokio::test]
async fn a_multiline_paste_is_one_draft_and_sends_nothing() {
    let mut harness = engine();
    drive(&mut harness, bracketed(PASTE)).await;
    assert_eq!(harness.app.composer.text(), DRAFT);
    assert_eq!(submissions(&harness).await, 0, "pasting must never submit");
}

#[tokio::test]
async fn one_send_after_a_paste_while_idle_is_one_prompt_with_the_whole_draft() {
    let mut harness = engine();
    let mut actions = bracketed(PASTE);
    actions.extend(decode(raw("\r")));
    drive(&mut harness, actions).await;

    let prompts = requests(&harness, "session/prompt").await;
    assert_eq!(prompts.len(), 1, "exactly one prompt");
    assert_eq!(prompts[0]["params"]["text"], DRAFT);
    assert!(requests(&harness, "session/steer").await.is_empty());
}

#[tokio::test]
async fn one_send_after_a_paste_while_a_turn_runs_is_one_steering_message() {
    let mut harness = engine();
    harness.app.state.apply(UiEvent::Submitted { text: "work".into() });
    assert!(harness.app.state.is_busy(), "the fixture must be busy");
    let mut actions = bracketed(PASTE);
    actions.extend(decode(raw("\r")));
    drive(&mut harness, actions).await;

    let steers = requests(&harness, "session/steer").await;
    assert_eq!(steers.len(), 1, "exactly one steering submission");
    assert_eq!(steers[0]["params"]["text"], DRAFT);
    assert!(requests(&harness, "session/prompt").await.is_empty());
}

#[tokio::test]
async fn a_paste_split_across_reads_and_line_ending_styles_gives_the_same_draft() {
    let mut harness = engine();
    let wide = non_ascii();
    let records = raw(&format!("\x1b[200~a\r\nb\rc\n\n  d {wide}\r\n\x1b[201~"));
    let mut decoder = Decoder::new();
    let start = Instant::now();
    let mut actions = Vec::new();
    // One record per read: every boundary, including inside the markers,
    // between CR and LF, and between the halves of the surrogate pair.
    for (index, record) in records.into_iter().enumerate() {
        let now = start + std::time::Duration::from_micros(index as u64);
        decoder.feed(record, now);
        decoder.tick(now);
        actions.extend(std::iter::from_fn(|| decoder.next_action()));
    }
    drive(&mut harness, actions).await;
    assert_eq!(harness.app.composer.text(), format!("a\nb\nc\n\n  d {wide}\n"));
    assert_eq!(submissions(&harness).await, 0);
}

#[tokio::test]
async fn pasted_command_text_and_control_bytes_run_nothing() {
    let mut harness = engine();
    drive(&mut harness, bracketed("/clear\r\n/quit\r\x03\x1b[A")).await;
    assert_eq!(harness.app.composer.text(), "/clear\n/quit\n\x03\x1b[A");
    assert!(!harness.app.state.should_quit, "a pasted /quit must not run");
    assert_eq!(submissions(&harness).await, 0);
    assert!(harness.calls.lock().expect("calls").is_empty(), "no request of any kind");
}

#[tokio::test]
async fn a_paste_never_answers_a_permission_prompt() {
    let mut harness = engine();
    harness.app.surfaces.push(Box::new(PromptSurface::new(PendingPrompt::Permission {
        tool: "write_file".into(),
        preview: "src/main.rs".into(),
    })));
    // "y", Enter and a trailing newline are what an approval would look
    // like typed.
    drive(&mut harness, bracketed("y\r\ny\r\n")).await;
    harness.settle_wire().await;
    assert!(harness.frames().is_empty(), "the paste wrote a frame: {:?}", harness.frames());
    assert_eq!(harness.app.surfaces.len(), 1, "the prompt is still open");
    assert!(harness.app.composer.text().is_empty(), "nothing leaked into the hidden composer");
}

#[tokio::test]
async fn a_dialog_opening_mid_paste_receives_nothing_and_the_composer_stays_clean() {
    let mut harness = engine();
    let actions = bracketed("text meant for the composer\r\n");
    let (start, rest) = actions.split_at(1);
    drive(&mut harness, start.to_vec()).await;
    harness.app.surfaces.push(Box::new(PromptSurface::new(PendingPrompt::Question {
        question: "Which?".into(),
        options: vec!["a".into()],
        multi_select: false,
        allow_free_text: true,
        accepts_images: false,
    })));
    drive(&mut harness, rest.to_vec()).await;

    assert!(harness.app.composer.text().is_empty());
    assert!(notices(&harness.app).iter().any(|notice| notice.contains("different dialog")));
    assert_eq!(submissions(&harness).await, 0);
}

#[tokio::test]
async fn an_aborted_paste_inserts_nothing_and_says_so() {
    let mut harness = engine();
    let mut decoder = Decoder::new();
    let now = Instant::now();
    for record in raw("\x1b[200~half a paste\r\n") {
        decoder.feed(record, now);
    }
    decoder.tick(now + crate::input::decoder::PASTE_STALL_TIMEOUT);
    let actions: Vec<_> = std::iter::from_fn(|| decoder.next_action()).collect();
    assert!(matches!(actions.last(), Some(InputAction::PasteAborted { .. })), "{actions:?}");
    drive(&mut harness, actions).await;

    assert!(harness.app.composer.text().is_empty());
    assert!(notices(&harness.app).iter().any(|notice| notice.contains("discarded")));
    assert_eq!(submissions(&harness).await, 0);
}

#[tokio::test]
async fn an_oversized_clipboard_or_terminal_text_is_refused_whole() {
    let mut harness = engine();
    let huge = "x".repeat(crate::input::decoder::MAX_PASTE_BYTES + 1);
    harness.app.apply_paste_text(huge, PasteSource::Clipboard, 0);
    assert!(harness.app.composer.text().is_empty());
    assert!(notices(&harness.app).iter().any(|notice| notice.contains("paste limit")));
}

#[tokio::test]
async fn two_identical_pastes_are_both_kept() {
    // One gesture, one owner — but two gestures are two pastes, and nothing
    // deduplicates by text or timing.
    let mut harness = engine();
    let mut actions = bracketed("same\r\n");
    actions.extend(bracketed("same\r\n"));
    drive(&mut harness, actions).await;
    assert_eq!(harness.app.composer.text(), "same\nsame\n");
}

#[tokio::test]
async fn ordinary_typing_and_enter_still_send_one_prompt() {
    let mut harness = engine();
    drive(&mut harness, decode(raw("hi\r"))).await;
    let prompts = requests(&harness, "session/prompt").await;
    assert_eq!(prompts.len(), 1);
    assert_eq!(prompts[0]["params"]["text"], "hi");
}

#[tokio::test]
async fn shift_enter_in_win32_input_mode_still_inserts_a_newline() {
    let mut harness = engine();
    let shift_enter = "\x1b[13;28;13;1;16;1_\x1b[13;28;13;0;16;1_";
    drive(&mut harness, decode(raw(&format!("a{shift_enter}b")))).await;
    assert_eq!(harness.app.composer.text(), "a\nb");
    assert_eq!(submissions(&harness).await, 0);
}

#[tokio::test]
async fn an_unframed_console_is_announced_once() {
    let mut harness = engine();
    harness.app.warn_if_paste_is_unframed(Some(crate::input::BackendStatus {
        backend: "crossterm-native",
        paste_framing: false,
        key_encoding: "native-records",
    }));
    assert!(notices(&harness.app).iter().any(|notice| notice.contains("Alt+V")));

    let mut framed = engine();
    framed.app.warn_if_paste_is_unframed(Some(crate::input::BackendStatus {
        backend: "windows-vt",
        paste_framing: true,
        key_encoding: "win32-input-mode",
    }));
    assert!(notices(&framed.app).is_empty());
}
