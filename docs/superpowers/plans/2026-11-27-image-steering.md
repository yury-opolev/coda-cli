# Image Steering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let `session/steer` carry images end to end (protocol → serve → agent inbox/delivery → TUI) per `docs/superpowers/specs/2026-11-27-image-steering-design.md`.

**Architecture:** Additive optional fields on existing steering types; images ride on `SteeringEntry` as an immutable `Arc<[SteeringImage]>`; delivery builds multimodal user content; the TUI keeps an immutable per-item payload copy and re-stages images on recall.

**Tech Stack:** Rust workspace under `rust/` (crates `coda-proto`, `coda-agent`, `coda-serve`, `coda-tui`), serde, schemars (feature `schema`), tokio.

All commands run from `rust/`. Every commit ends with
`Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.

---

### Task 1: Protocol types and capability

**Files:** `crates/coda-proto/src/messages.rs`, `crates/coda-proto/src/state.rs`, `crates/coda-proto/src/schema.rs` (test), `docs/protocol/schemas/*.json` (regenerated), `docs/protocol/catalog.md`, `docs/serve-protocol.md`.

- [ ] `SteerParams { text: String, #[serde(skip_serializing_if = "Vec::is_empty", default)] images: Vec<WireImage> }` (+ `Default`, `Deserialize` derive).
- [ ] `SteerResult` gains `#[serde(default)] rejected_detail: Option<String>`; doc lists new reasons `invalidImage`, `imagesUnsupported`, `attachmentCapacity`.
- [ ] `RecalledSteeringMessage` gains `#[serde(default)] images: Vec<WireImage>`.
- [ ] `WireImage`: replace derived `Debug` with a manual impl printing `media_type` and `base64.len()` only.
- [ ] `pub const STEERING_IMAGES_CAPABILITY: &str = "steering.images";` in messages.rs.
- [ ] state.rs: `SteeringImageMeta { media_type: String, byte_length: i64 }` and `SteeringPendingDto.images: Vec<SteeringImageMeta>` with `#[serde(default, skip_serializing_if = "Vec::is_empty")]`.
- [ ] schema.rs test asserting `SteerParams.json` has `images`, `RecallSteeringResult.json`'s message has `images`, `StateSnapshot.json` has `SteeringImageMeta`.
- [ ] Regenerate schemas: copy `..\..\coda-cli\rust\crates\coda-proto\src\bin\emit_schemas.rs` into the worktree (gitignored, never committed) and run `cargo run -p coda-proto --features schema --bin emit_schemas`.
- [ ] Run `cargo test -p coda-proto --features schema --lib`; commit.

### Task 2: Inbox carries images

**Files:** `crates/coda-agent/src/steering.rs`, `crates/coda-agent/src/subagents/checkpoint.rs`, `crates/coda-agent/src/subagents/host.rs`.

- [ ] `pub struct SteeringImage { pub media_type: String, pub base64: String }` with manual `Debug` (media type + length) and `fn byte_len()`.
- [ ] `SteeringEntry.images: Arc<[SteeringImage]>`; `push_item` and `enqueue` always use an empty slice.
- [ ] `SteerRejectReason::AttachmentCapacity`; `pub const MAX_QUEUED_IMAGE_BYTES: usize = 32 * 1024 * 1024`.
- [ ] `enqueue_message(text, images: Vec<SteeringImage>) -> Result<SteeringEntry, SteerRejectReason>`: EmptyText only if blank text **and** no images; under the gate, sum pending image bytes + new > cap → `AttachmentCapacity` (observer `on_rejected`). `enqueue_with_reason(text)` delegates with no images.
- [ ] `accepts_images: AtomicBool` (default true) + `set_accepts_images(bool)`; `take_all_for_delivery` when false moves image-bearing `Message` entries out, reports them via new observer hook `on_undeliverable(&[SteeringEntry])`, and delivers the rest.
- [ ] `snapshot_items` returns `Vec<(InboxItemKind, String, Arc<[SteeringImage]>)>`; `CheckpointInboxItem` gets optional `images: Vec<CheckpointImage{media_type, base64}>` (`serde(default, skip_serializing_if)`); restore uses a new `push_item_with_images`.
- [ ] Unit tests: image-only accepted; FIFO + per-entry image order; cap refusal and that removal (recall/delivery) frees capacity; undeliverable withdrawal never delivers text alone; recall vs delivery exclusivity with images; Debug has no base64.

### Task 3: Agent delivery

**Files:** `crates/coda-agent/src/agent/mod.rs` (step 4b + `run`), `crates/coda-agent/src/agent/tools.rs` (pre-empt).

- [ ] In `run`, after `open_for_turn`, call `steering.set_accepts_images(self.client.accepts_image_input(&self.model))`.
- [ ] Step 4b: a run with no images keeps the existing joined-text `Message::user`; a run with images builds `Message::new(Role::User, blocks)` where, per entry, `Content::Text(text)` (brief prefixed to the first; skipped if empty) then its `Content::Image`s.
- [ ] Pre-empt path: after `Content::Text(steering_text)` push each entry's images as `Content::Image` (tool results stay first).
- [ ] Tests: steered images reach the next request as image blocks with text before them; pre-empt keeps tool results before the steering blocks.

### Task 4: Server

**Files:** `crates/coda-serve/src/dispatch.rs`, `crates/coda-serve/src/host.rs`, `crates/coda-serve/src/images.rs`, `crates/coda-serve/src/state/{mod.rs,steering_observer.rs,live.rs}`, `crates/coda-serve/src/capabilities.rs`, `crates/coda-serve/src/restart.rs`.

- [ ] dispatch `SteerParams.images: Option<Vec<Value>>`.
- [ ] images.rs: `validate_wire_image(&Value) -> Result<(String,String), String>` shared by prompt and steer (media type list + base64), messages without payload bytes.
- [ ] `session_steer`: validate → `imagesUnsupported` if images and `!client.accepts_image_input(current_model)` → existing serialized enqueue via `enqueue_message`; map `AttachmentCapacity` → `attachmentCapacity`, `rejectedDetail` human text.
- [ ] Recall returns `images` (`[{mediaType, base64}]`).
- [ ] State: `steering_enqueued(id, text, images_meta)`; observer passes metadata; `on_undeliverable` → `Rejected` outcome; live projection adds `HistoryBlock::Image` metadata after text.
- [ ] Capability `steering.images` supported.
- [ ] Restart: `steering_images: Vec<usize>` (parallel counts, optional) and note "(with N image(s) not carried over)".
- [ ] Host tests: text+image accepted and pending DTO has metadata (no base64 in the serialized snapshot); image-only accepted; invalid image / unsupported provider / capacity refused with nothing queued; recall returns images in order; no turn → `noActiveTurn`.

### Task 5: TUI

**Files:** `crates/coda-tui/src/app/{mod.rs,queue.rs,image.rs,clipboard.rs}`, `crates/coda-tui/src/api/view.rs`, `crates/coda-tui/src/state.rs`, `crates/coda-tui/src/render/draw.rs`.

- [ ] `QueuedMessage.images: Vec<WireImage>` and `image_count: usize`; `UiEvent::Queued { text, id, images }`; reconcile keeps local images by id and takes count from metadata otherwise; `recall_unsent` returns `(String, Vec<WireImage>)`.
- [ ] `ServeView` records `steering.images` capability like `presentation_capable`.
- [ ] `submit` busy path: images + no capability → keep draft, warning; else `steer(text, images)`.
- [ ] `steer`: send images; on ok clear staged images, `Queued` with images, hint "Queued · N image(s) · will reach the model at the next safe boundary"; refusal/unconfirmed keep text + staged images; refusal texts for new reasons.
- [ ] `restage_recalled(text, images) -> String` in image.rs; used by engine recall and recovery-list recall.
- [ ] draw_pending label `pending · N image(s)`.
- [ ] Tests for each bullet (state, image.rs, queue.rs, draw).

### Task 6: Docs, version, verification

- [ ] `docs/serve-protocol.md` + `docs/protocol/catalog.md` steering sections; `version.json` build 188.
- [ ] `cargo test --workspace`, `cargo test -p coda-proto --features schema --lib`, release build + `engine_contract`, zero warnings.
