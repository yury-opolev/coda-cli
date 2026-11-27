# Image steering: text and images in the running-turn queue

Date: 2026-11-27. Status: implemented in `feat/image-steering`.

## Problem

`session/steer` is text-only. A busy TUI refuses an image-bearing draft
("Images cannot be steered into a running turn"), so a screenshot and its
explanation cannot be queued while the agent works.

## Decision

Extend the existing steering pipeline (engine-owned FIFO, stable message ids,
safe-boundary delivery, turn-end sealing) to carry images. No second queue, no
new keyboard gesture.

## Protocol (additive, optional fields)

- `SteerParams.images?: WireImage[]` — same representation as
  `PromptParams.images`. Absent or empty = today's text-only behaviour.
- `SteerResult.rejectedReason` gains `invalidImage`, `imagesUnsupported`,
  `attachmentCapacity`; `emptyText` now means "neither text nor images".
  `SteerResult.rejectedDetail?: string` carries a human explanation that never
  contains image bytes.
- `RecalledSteeringMessage.images?: WireImage[]` — recall hands back the full
  payload (the explicit, destructive recovery path).
- `SteeringPendingDto.images?: SteeringImageMeta[]` (`mediaType`,
  `byteLength`) — metadata only; status events never carry base64.
- Capability `steering.images` (supported). A client must not send `images`
  to an engine that does not advertise it: an older engine ignores unknown
  fields and would queue the text alone.

## Engine

- `SteeringEntry.images: Arc<[SteeringImage]>` — an immutable snapshot,
  cheap to clone; `SteeringImage`'s `Debug` prints media type and size only.
  Runtime items (`TaskCompleted`, `Deadline`) always carry none.
- `SteeringInbox::enqueue_message(text, images)` — rejects only when the text
  is blank **and** there are no images. Under the same gate lock it enforces an
  aggregate cap on queued image payload (`MAX_QUEUED_IMAGE_BYTES` = 32 MiB of
  base64 across all pending entries, including the new one) and refuses with
  `AttachmentCapacity`.
- `session/steer` validation order, all before the TURN lock: per-image media
  type + base64 validation (shared with prompts) → provider capability
  (`LlmClient::accepts_image_input(current model)`) → then the existing
  serialized busy-check + enqueue (lock order TURN → INBOX → STATE → BUS
  unchanged). Every failure returns `ok:false` + reason; nothing is enqueued.
- Delivery: at the loop's iteration boundary a run of operator messages with
  no images is joined exactly as today; a run with images becomes one user
  message whose blocks are, per entry in FIFO order, `Text` then that entry's
  `Image` blocks. In the mid-batch pre-empt path the skipped tools' results
  stay first and the steering text/images follow, so tool results remain in
  provider-valid order.
- Capability drift: the agent tells the inbox at run start whether its
  client/model accepts images. If not, image-bearing messages are withdrawn
  instead of delivered (observer `on_undeliverable` → outcome `rejected`), so
  the client keeps them for explicit recovery; text is never delivered alone.
- Planned restart: recalled steering keeps its text in the restart note and
  says how many images were not carried over; the TUI keeps the payloads
  (outcome `recalled` → recovery list). Subagent checkpoints serialize inbox
  images (optional field) so nothing is silently dropped there either.

## TUI

- `App::submit` while busy resolves images from the draft's surviving markers
  (existing `images_for_draft`). Image-bearing draft + engine without
  `steering.images` → draft and staged images kept, warning notice.
- `App::steer(text, images)` sends both. Staged images are cleared only after
  `ok:true`; the queued item owns its own `Vec<WireImage>` copy. Refusal or
  unconfirmed outcome: draft text restored, staged images untouched, no retry.
- Queued panel shows `[pending · N image(s)]`; acceptance notice says
  "Queued · N image(s) · will reach the model at the next safe boundary".
  Delivery is still shown only when the engine reports it.
- Recall (`session/recallSteering`) and recovery-list recall restore text and
  re-stage images, re-binding each image to the marker that referenced it
  (markers parsed from the text in first-appearance order; an image without a
  marker gets a fresh marker appended).
- Snapshot reconciliation keeps the local payload by message id; entries the
  client never held show their image count from metadata.

## Tests

Inbox (FIFO + per-entry image order, image-only, cap, recall vs delivery,
undeliverable withdrawal), agent delivery (content blocks, tool-result order),
host (`session/steer` validation/provider/capacity/turn-ending, recall returns
images, metadata-only snapshot), proto (schema fields), TUI (image-only
steer, marker removal, immutable snapshot, refusal/unconfirmed keep draft,
older engine notice, recall restages), text-only regressions via existing
suites.

## Out of scope

Mid-request injection, tool interruption, non-image attachments, a
next-turn queue, background-task messaging. An engine-side stash for
turn-ended image payloads is not added: the submitting client holds the
immutable snapshot, and recall is the engine-side recovery path while pending.
