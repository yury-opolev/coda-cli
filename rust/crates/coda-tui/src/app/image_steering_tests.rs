//! Image steering, end to end through `App::submit` to a fake engine: what
//! is sent, what is kept, and what the operator is told.

use std::collections::HashMap;

use serde_json::{json, Value};

use super::image::images_for_draft;
use super::serve::tests::{app_with, warnings, Harness};
use crate::local::AccessMode;
use crate::state::UiEvent;
use coda_proto::messages::{CapabilityEntry, InitializeResult, WireImage, STEERING_IMAGES_CAPABILITY};

/// A fake engine answering `session/steer` with `steer`, and a busy turn.
fn busy_engine(image_capable: bool, steer: impl Fn(&Value) -> Value + Send + 'static) -> Harness {
    let mut harness = app_with(AccessMode::TrustedLocal, move |method, params| match method {
        "session/steer" => steer(params),
        _ => json!({}),
    });
    let mut capabilities = HashMap::new();
    if image_capable {
        capabilities.insert(STEERING_IMAGES_CAPABILITY.to_string(), CapabilityEntry::supported());
    }
    harness.app.view.on_initialize(&InitializeResult {
        protocol_version: "1".into(),
        session_id: "s1".into(),
        server_info: "in-process".into(),
        capabilities: Some(capabilities),
        ..Default::default()
    });
    harness.app.state.apply(UiEvent::Submitted { text: "work".into() });
    assert!(harness.app.state.is_busy());
    harness
}

fn accepting() -> impl Fn(&Value) -> Value + Send + 'static {
    |_| json!({ "ok": true, "messageId": "m1" })
}

async fn steers(harness: &Harness) -> Vec<Value> {
    harness.settle_wire().await;
    harness.frames().into_iter().filter(|frame| frame["method"] == "session/steer").collect()
}

fn type_text(harness: &mut Harness, text: &str) {
    harness.app.composer.insert(text);
}

#[tokio::test]
async fn text_and_an_image_are_queued_as_one_message() {
    let mut harness = busy_engine(true, accepting());
    type_text(&mut harness, "Here is the failing screen");
    let marker = harness.app.stage_image_bytes("image/png", b"png-bytes");
    harness.app.submit().await;

    let sent = steers(&harness).await;
    assert_eq!(sent.len(), 1, "exactly one steering request");
    assert_eq!(sent[0]["params"]["text"], format!("Here is the failing screen {marker}"));
    assert_eq!(sent[0]["params"]["images"].as_array().unwrap().len(), 1);
    assert_eq!(sent[0]["params"]["images"][0]["mediaType"], "image/png");

    let queued = &harness.app.state.queued;
    assert_eq!(queued.len(), 1);
    assert_eq!(queued[0].id.as_deref(), Some("m1"));
    assert_eq!(queued[0].image_count, 1);
    assert!(harness.app.composer.is_empty(), "the composer is free for another draft");
    assert!(harness.app.staged_images.is_empty(), "the queued item owns its own copy now");
}

#[tokio::test]
async fn an_image_only_draft_is_queued_without_filler_text() {
    let mut harness = busy_engine(true, accepting());
    harness.app.stage_image_bytes("image/png", b"png-bytes");
    harness.app.submit().await;
    let sent = steers(&harness).await;
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0]["params"]["images"].as_array().unwrap().len(), 1);
    assert_eq!(harness.app.state.queued.len(), 1);
}

#[tokio::test]
async fn a_removed_marker_excludes_its_image() {
    let mut harness = busy_engine(true, accepting());
    harness.app.stage_image_bytes("image/png", b"dropped");
    harness.app.composer.set_text("just text now");
    harness.app.submit().await;
    let sent = steers(&harness).await;
    assert_eq!(sent.len(), 1);
    assert!(sent[0]["params"].get("images").is_none(), "{}", sent[0]);
}

#[tokio::test]
async fn later_drafts_and_clipboard_changes_never_alter_an_accepted_item() {
    let mut harness = busy_engine(true, accepting());
    harness.app.stage_image_bytes("image/png", b"original");
    harness.app.submit().await;
    let accepted = harness.app.state.queued[0].clone();

    harness.app.stage_image_bytes("image/png", b"something else");
    type_text(&mut harness, " a new draft");
    assert_eq!(harness.app.state.queued[0], accepted);
}

#[tokio::test]
async fn a_refusal_keeps_the_whole_draft_with_its_images() {
    let mut harness = busy_engine(true, |_| {
        json!({ "ok": false, "rejectedReason": "imagesUnsupported" })
    });
    type_text(&mut harness, "explain");
    harness.app.stage_image_bytes("image/png", b"bytes");
    harness.app.submit().await;

    let draft = harness.app.composer.text();
    assert!(draft.starts_with("explain"), "{draft}");
    let still = images_for_draft(&harness.app.staged_images, &draft);
    assert_eq!(still.len(), 1, "the image is still attached to the draft");
    assert!(harness.app.state.queued.is_empty());
    assert!(warnings(&harness.app).iter().any(|w| w.contains("cannot receive images")));
}

#[tokio::test]
async fn an_unconfirmed_outcome_keeps_the_draft_and_is_never_retried() {
    let mut harness = busy_engine(true, |_| json!("not a steer result"));
    harness.app.stage_image_bytes("image/png", b"bytes");
    harness.app.submit().await;

    assert_eq!(steers(&harness).await.len(), 1, "no automatic resend");
    let draft = harness.app.composer.text();
    assert_eq!(images_for_draft(&harness.app.staged_images, &draft).len(), 1);
    assert!(harness.app.state.queued.is_empty(), "no success is claimed");
    assert!(warnings(&harness.app).iter().any(|w| w.contains("did not confirm")));
}

#[tokio::test]
async fn an_older_engine_keeps_an_image_draft_and_says_why() {
    let mut harness = busy_engine(false, accepting());
    type_text(&mut harness, "with a picture");
    harness.app.stage_image_bytes("image/png", b"bytes");
    harness.app.submit().await;

    assert!(steers(&harness).await.is_empty(), "the text is never sent without its image");
    let draft = harness.app.composer.text();
    assert_eq!(images_for_draft(&harness.app.staged_images, &draft).len(), 1);
    assert!(warnings(&harness.app).iter().any(|w| w.contains("predates image steering")));
}

#[tokio::test]
async fn text_only_steering_is_unchanged_on_an_older_engine() {
    let mut harness = busy_engine(false, accepting());
    type_text(&mut harness, "plain follow-up");
    harness.app.submit().await;
    let sent = steers(&harness).await;
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0]["params"], json!({ "text": "plain follow-up" }));
}

#[tokio::test]
async fn recall_restores_the_text_and_restages_its_images() {
    let marker = format!("[{} coda-image-a1b2c3d4e5f6.png]", crate::render::glyphs::CAMERA);
    let image = WireImage { media_type: "image/png".into(), base64: "UkVDQUxM".into() };
    let recalled = json!({ "messages": [{ "id": "m1", "text": format!("see {marker}"), "images": [image] }] });
    let mut harness = app_with(AccessMode::TrustedLocal, move |method, _| match method {
        "session/recallSteering" => recalled.clone(),
        _ => json!({}),
    });
    harness.app.state.apply(UiEvent::Submitted { text: "work".into() });
    harness.app.state.apply(UiEvent::Queued {
        text: format!("see {marker}"),
        id: Some("m1".into()),
        images: vec![image.clone()],
    });
    assert!(harness.app.recall_pending_into_composer().await);

    let draft = harness.app.composer.text();
    assert_eq!(draft, format!("see {marker}"), "text and marker come back as they were");
    assert_eq!(images_for_draft(&harness.app.staged_images, &draft), vec![image]);
    assert!(harness.app.state.queued.is_empty(), "the recalled item left the queue");
}