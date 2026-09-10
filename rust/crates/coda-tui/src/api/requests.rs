//! Outstanding server-initiated requests, from *both* directions.
//!
//! The engine can tell this client about the same permission/question/plan in
//! two different ways at the same time:
//!
//! - the raw `request/*` round-trip, which arrives with a [`Responder`] that
//!   answers it, and
//! - `session/getPendingRequests` / `event/requestPending`, which describe it
//!   as a [`PendingRequestDto`] with an opaque `requestId` handle.
//!
//! Treating those as two requests produces two modals for one decision.
//! Treating the discovery description as authoritative and discarding the
//! responder is worse: **dropping a `Responder` answers it** — with
//! `REQUEST_CANCELLED`, i.e. a decline — so the user's eventual "allow" would
//! arrive after the tool had already been denied.
//!
//! So there is one registry, keyed on the engine's own opaque handle (carried
//! on the raw frame since this contract stage, additive and optional). The
//! handle is compared, never parsed: its shape (`req-<instance>-<n>` today) is
//! explicitly not part of the contract, and reconstructing one from the
//! numeric JSON-RPC id would be a guess that silently answers the wrong
//! request after an engine restart.
//!
//! Concurrency is real here: background subagents and scheduled runs can raise
//! requests while a foreground turn has one open. They queue, oldest first,
//! and answering resolves exactly the one on screen.

use coda_client::Responder;
use coda_proto::state::{PendingRequestDto, PendingRequestKind};

use crate::state::PendingPrompt;

/// One outstanding decision.
pub struct Interaction {
    /// The engine's opaque handle, when it gave us one. `None` only for a
    /// legacy engine that predates the handle, where the raw responder is the
    /// only way to answer and discovery cannot describe it.
    pub handle: Option<String>,
    /// The engine process that raised it. A handle is only valid against the
    /// instance that minted it.
    pub engine_instance_id: Option<String>,
    pub prompt: PendingPrompt,
    /// The raw round-trip, when this client is the one the engine asked.
    responder: Option<Responder>,
    /// Stable identity for an entry with no handle.
    local_id: u64,
    /// The engine event that announced this decision, on the engine's own
    /// clock: the `seq` of the `event/requestPending` frame that named it, or
    /// the `cursor` of the snapshot that listed it.
    ///
    /// `None` means **unknown**, and that is a real state rather than a
    /// missing default. The raw `request/*` round-trip carries no sequence
    /// and bypasses the event fence entirely, so a request whose announcement
    /// is still held behind a resync — or was dropped, or was never sent by a
    /// legacy engine — is known to this client without any engine time
    /// attached to it. Nothing is retired on a guess about one; what settles
    /// those is [`received_at_local`](Self::received_at_local).
    learned_at: Option<i64>,
    /// When this client took delivery of the raw `request/*` frame, on the
    /// registry's own monotonic counter of raw receipts.
    ///
    /// `Some` for a decision whose round-trip this client received — whether
    /// the entry was created by that frame or adopted it — and `None` for one
    /// known only from discovery, which is somebody else's evidence and says
    /// nothing about when *this* client learned anything.
    ///
    /// It is second-class evidence and used only where it is sound: a
    /// `session/getState` answer is built when the request arrives, never
    /// served from a cache, and the engine registers a request before it
    /// writes the frame. So a raw frame this client had already taken
    /// delivery of when it *issued* a read was registered before the engine
    /// built that read's answer, and the answer's silence about it is
    /// conclusive even though the announcement carrying its `seq` was lost.
    received_at_local: Option<u64>,
}

impl Interaction {
    /// Whether this client owns the original round-trip.
    pub fn has_responder(&self) -> bool {
        self.responder.is_some()
    }

    /// A stable key for equality checks and tests.
    pub fn key(&self) -> String {
        match &self.handle {
            Some(handle) => handle.clone(),
            None => format!("local-{}", self.local_id),
        }
    }

        /// Records the engine time at which this decision was announced.
        ///
        /// Called when discovery describes a request this client already holds
        /// the raw round-trip for — the ordinary case, since the engine writes
        /// the announcement first but only the announcement goes through the
        /// event fence, so a resync can deliver them in the other order. The
        /// earliest engine time wins: it is the one the engine actually announced
        /// it at, and a later observation does not move when it happened.
        fn learned_at_or_earlier(&mut self, at: Option<i64>) {
            let Some(at) = at else { return };
            self.learned_at = Some(match self.learned_at {
                Some(known) => known.min(at),
                None => at,
            });
        }
    }

impl std::fmt::Debug for Interaction {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Interaction")
            .field("handle", &self.handle)
            .field("prompt", &self.prompt)
            .field("has_responder", &self.responder.is_some())
            .finish()
    }
}

/// How an answer must be delivered.
pub enum Delivery {
    /// Answer the raw round-trip we own. This is the exactly-once path and is
    /// always preferred: it needs no second RPC, so there is no window in
    /// which the responder is alive but abandoned.
    Raw(Responder),
    /// We never owned the round-trip (we discovered the request after a
    /// reconnect, or another surface raised it), so the out-of-band RPC is
    /// the only way to answer.
    Rpc { request_id: String },
    /// The decision belonged to an engine process that is no longer the one
    /// on the other end. Nothing is sent: its handle names nothing in the
    /// current process, and its raw request id would address whichever
    /// request now happens to hold that number.
    Stale,
    /// Nothing to answer.
    None,
}

/// The FIFO of outstanding decisions.
#[derive(Debug, Default)]
pub struct PendingInteractions {
    entries: Vec<Interaction>,
    next_local: u64,
    /// The number the next raw `request/*` receipt will take.
    ///
    /// Monotonic, local, and not a clock: it counts the raw round-trips this
    /// client has taken delivery of, in the order it took them, and is never
    /// compared with an engine sequence.
    next_received: u64,
}

/// The engine time a list of outstanding requests describes.
///
/// The whole of the reconciliation rule turns on this. A list — a snapshot's
/// `requests` at its `cursor`, or the one carried by an
/// `event/requestPending` at its `seq` — is the engine's own answer to "what
/// am I waiting for?" *as of that point in its event stream*, and it is
/// authoritative about exactly that much. Treating it as a statement about
/// everything, and retiring whatever it does not name, discards decisions the
/// engine raised after it was taken.
///
/// The variants are separate rather than one "list at a fence" because they
/// carry different evidence, and the difference is structural: only a
/// snapshot may fall back on this client's own record of when it received a
/// raw frame, because only a snapshot is *built to order* by the engine after
/// this client asked for it. An announcement is broadcast on its own
/// schedule, so no local receipt can be placed relative to it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Authority {
    /// A `session/getState` answer: the engine's list at `cursor`, built for a
    /// read this client issued after taking `received_before` from
    /// [`PendingInteractions::raw_watermark`].
    Snapshot { cursor: i64, received_before: u64 },
    /// The list carried by an `event/requestPending`, at that frame's `seq`.
    ///
    /// Engine evidence only: an announcement retires what the engine's own
    /// ordering places before it, and nothing else.
    Announced(i64),
    /// A list with no engine time on it: a legacy engine that stamps no
    /// sequence, or an out-of-band read that reports no cursor. It can reveal
    /// decisions; it retires nothing, because there is no way to tell what it
    /// is older than.
    Unsequenced,
}

impl Authority {
    /// Whether this list is late enough to speak about `entry`.
    ///
    /// Two kinds of evidence, in strict order of authority:
    ///
    /// 1. **The engine's own.** When this client knows the `seq` the request
    ///    was announced at, that decides it — including *against* the list: an
    ///    announcement newer than the snapshot means the snapshot was built
    ///    before the request existed, however long ago its raw frame arrived
    ///    here. The local receipt is not consulted at all in that case.
    /// 2. **This client's receipt of the raw frame**, and only for a
    ///    [`Snapshot`](Self::Snapshot). The engine registers a request before
    ///    it writes it, and a fresh `session/getState` is built when it
    ///    arrives, so a raw frame already taken delivery of when this read was
    ///    issued was registered before the answer was built. Absence is then
    ///    conclusive — which is what closes the otherwise permanent "raw
    ///    arrived, announcement and resolution both lost" modal.
    ///
    /// Everything else is unknown, and an unknown is never resolved by
    /// assuming the worst.
    fn covers(self, entry: &Interaction) -> bool {
        match self {
            Authority::Snapshot { cursor, received_before } => match entry.learned_at {
                Some(announced) => announced <= cursor,
                None => entry.received_at_local.is_some_and(|at| at < received_before),
            },
            Authority::Announced(seq) => {
                entry.learned_at.is_some_and(|announced| announced <= seq)
            }
            Authority::Unsequenced => false,
        }
    }

    /// The engine time to stamp on a request this list describes.
    fn stamps(self) -> Option<i64> {
        match self {
            Authority::Snapshot { cursor, .. } => Some(cursor),
            Authority::Announced(seq) => Some(seq),
            Authority::Unsequenced => None,
        }
    }
}

/// What a reconciliation changed.
#[derive(Debug, Default, PartialEq, Eq)]
pub struct Reconciliation {
    /// Handles the list revealed that this client had not shown yet.
    pub opened: Vec<String>,
    /// Keys of decisions the list retired: the engine is not waiting for them
    /// any more, so nothing was answered and nothing may be.
    pub retired: Vec<String>,
}

impl Reconciliation {
    /// Whether the decision identified by `key` was retired by this
    /// reconciliation.
    pub fn retired_key(&self, key: &str) -> bool {
        self.retired.iter().any(|retired| retired == key)
    }
}

impl PendingInteractions {
    pub fn new() -> Self {
        Self::default()
    }

    /// The watermark to pair with one `session/getState`.
    ///
    /// Taken **immediately before the read is enqueued**, and carried with
    /// *that* read to [`reconcile`](Self::reconcile) as
    /// [`Authority::Snapshot::received_before`]. Every raw round-trip this
    /// client had already taken delivery of is numbered below it; anything
    /// received afterwards — a frame still sitting unread in the channel when
    /// the read went out — is numbered above it and is therefore something the
    /// answer cannot speak about.
    ///
    /// Re-reading it when the answer comes back would destroy exactly that
    /// distinction: it would then include the frames that arrived *during* the
    /// read, and the snapshot would retire decisions the engine raised after
    /// it was built. It is a value paired with one read, never a "latest".
    pub fn raw_watermark(&self) -> u64 {
        self.next_received
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// The decision currently on screen: the oldest outstanding one.
    pub fn current(&self) -> Option<&Interaction> {
        self.entries.first()
    }

    pub fn keys(&self) -> Vec<String> {
        self.entries.iter().map(Interaction::key).collect()
    }

    /// Where a handle sits in the queue, if it is outstanding at all.
    ///
    /// `Some(0)` is the decision currently on screen. The distinction is
    /// load-bearing: a background subagent's request resolving elsewhere must
    /// not close the modal the operator is reading, and must not narrate an
    /// outcome over it.
    pub fn position(&self, request_id: &str) -> Option<usize> {
        self.entries.iter().position(|e| e.handle.as_deref() == Some(request_id))
    }

    /// The raw `request/*` arrived.
    ///
    /// Returns `true` when this is a decision the UI has not shown yet. A
    /// request already known from discovery is *merged*: it gains the
    /// responder and keeps its place in the queue, so no second modal opens
    /// and no responder is abandoned.
    pub fn on_server_request(
        &mut self,
        handle: Option<String>,
        engine_instance_id: Option<String>,
        prompt: PendingPrompt,
        responder: Responder,
    ) -> bool {
        // One number per raw frame taken delivery of, assigned before
        // anything else can fail: whether it opens an entry or merges into
        // one, this is the moment this client became able to answer it.
        let receipt = self.next_received;
        self.next_received += 1;
        if let Some(handle) = handle.as_deref() {
            if let Some(existing) = self.entries.iter_mut().find(|e| e.handle.as_deref() == Some(handle)) {
                // Discovery got here first. Adopt the responder rather than
                // opening a second prompt for the same decision.
                if let Some(previous) = existing.responder.replace(responder) {
                    // Two raw frames for one handle cannot happen, but if the
                    // engine ever did that, failing the older one explicitly
                    // beats leaking it.
                    previous.fail(
                        coda_proto::error_codes::INTERNAL_ERROR,
                        "superseded by a second request with the same handle",
                    );
                }
                existing.prompt = prompt;
                // The entry is now one this client holds the round-trip for,
                // so it carries a receipt like any other — the earliest one,
                // because that is when the frame arrived.
                existing.received_at_local.get_or_insert(receipt);
                return false;
            }
        }
        let local_id = self.next_local;
        self.next_local += 1;
        self.entries.push(Interaction {
            handle,
            engine_instance_id,
            prompt,
            responder: Some(responder),
            local_id,
            // The raw round-trip carries no sequence and does not go through
            // the event fence, so this client knows the decision exists
            // without knowing where in the engine's stream it was raised.
            // Discovery stamps that in when it catches up; until then the
            // receipt below is the only evidence there is.
            learned_at: None,
            received_at_local: Some(receipt),
        });
        true
    }

    /// Discovery (`session/getPendingRequests` or `event/requestPending`)
    /// described a request.
    ///
    /// `at` is the engine time of the description: the announcing frame's
    /// `seq`, or the `cursor` of the snapshot that listed it. `None` for a
    /// read that reports neither.
    ///
    /// Returns `true` when it is genuinely new. A request whose raw frame we
    /// already hold is *not* re-added and its responder is not touched — but
    /// it **is** stamped with `at`, and that is the point of doing it here:
    /// the announcement goes through the event fence and the raw frame does
    /// not, so a resync routinely delivers them in the opposite order to the
    /// one the engine wrote them in. Without the stamp the entry stays
    /// "announced at an unknown time" for ever, and no list can ever speak
    /// about it.
    pub fn on_discovered(
        &mut self,
        dto: &PendingRequestDto,
        engine_instance_id: Option<String>,
        at: Option<i64>,
    ) -> bool {
        if let Some(existing) = self
            .entries
            .iter_mut()
            .find(|e| e.handle.as_deref() == Some(dto.request_id.as_str()))
        {
            existing.learned_at_or_earlier(at);
            return false;
        }
        let Some(prompt) = prompt_from_dto(dto) else { return false };
        let local_id = self.next_local;
        self.next_local += 1;
        self.entries.push(Interaction {
            handle: Some(dto.request_id.clone()),
            engine_instance_id,
            prompt,
            responder: None,
            local_id,
            learned_at: at,
            // Discovery is somebody else's evidence: it says when the *engine*
            // announced the request, not when this client took delivery of
            // anything. A handle-only entry therefore has no local receipt,
            // and no list may retire it on local evidence.
            received_at_local: None,
        });
        true
    }

    /// Reconciles against a list of outstanding requests.
    ///
    /// The list is the engine's own answer to "what am I waiting for?" as of
    /// [`Authority`] — a snapshot at its `cursor`, or an announcement at its
    /// `seq` — and it is authoritative about exactly that much. So a request
    /// is retired only when all of this holds:
    ///
    /// * the engine named it (there is a handle to compare),
    /// * the list is late enough to speak about it
    ///   ([`Authority::covers`], which is where the two kinds of evidence and
    ///   their order of authority live), and
    /// * the list does not contain it.
    ///
    /// Then, and only then, the engine has stopped waiting for it. That is
    /// the case a lost `event/requestResolved` used to leave behind — a
    /// dropped ring, an overflowed hold — with the modal still up over a
    /// decision the engine had already finished with, whose eventual answer
    /// went out as a bare numeric id into a registry that had reused it.
    ///
    /// Ownership of the round-trip is deliberately **not** part of that test.
    /// Keeping every entry with a responder was what left the stale modal up;
    /// retiring every absent one discards a live approval, because the raw
    /// `request/*` bypasses the event fence while its announcement can sit in
    /// the resync hold buffer, so a snapshot taken before the request existed
    /// routinely arrives after the client already holds it. What separates
    /// those two is evidence about ordering, and nothing else.
    ///
    /// A retired responder is [`Responder::discard`]ed — never dropped, which
    /// would *send* a cancellation to an id the engine has since reused — and
    /// no outcome is invented for it.
    ///
    /// Either way a list from a *different* engine process says nothing about
    /// the previous one's requests except that they are gone, so those are
    /// retired first.
    pub fn reconcile(
        &mut self,
        listed: &[PendingRequestDto],
        engine_instance_id: Option<String>,
        authority: Authority,
    ) -> Reconciliation {
        let mut outcome = Reconciliation {
            opened: Vec::new(),
            retired: self.retire_other_instances(engine_instance_id.as_deref()),
        };
        let at = authority.stamps();
        for dto in listed {
            if self.on_discovered(dto, engine_instance_id.clone(), at) {
                outcome.opened.push(dto.request_id.clone());
            }
        }

        let mut kept = Vec::with_capacity(self.entries.len());
        for mut entry in std::mem::take(&mut self.entries) {
            let named = match entry.handle.as_deref() {
                Some(handle) => listed.iter().any(|dto| dto.request_id == handle),
                // Nothing to compare: a legacy engine's request cannot appear
                // in a list that identifies requests by a handle it never
                // minted, so no list is evidence about it.
                None => true,
            };
            if named || !authority.covers(&entry) {
                kept.push(entry);
                continue;
            }
            if let Some(responder) = entry.responder.take() {
                responder.discard();
            }
            outcome.retired.push(entry.key());
        }
        self.entries = kept;
        outcome
    }

    /// Drops every decision raised by an engine process other than `current`,
    /// naming what it retired.
    ///
    /// Retired, not answered: a handle is only valid against the instance
    /// that minted it, and a raw request id from a replaced process addresses
    /// nothing in its replacement — over a proxy that kept the connection it
    /// would address whatever now holds that number. So the responder is
    /// explicitly [`Responder::discard`]ed rather than dropped, because
    /// dropping one *sends* a cancellation.
    ///
    /// The keys are returned rather than a count because the screen has to be
    /// brought into line with the registry, and only the caller knows which
    /// decision is on it: "two were retired" cannot distinguish the modal the
    /// operator is reading from one queued behind it.
    ///
    /// `None` for `current`, or for an entry's own instance, means the
    /// comparison cannot be made (a legacy engine, or a handshake that has
    /// not reported an instance yet). Nothing is retired on a guess.
    pub fn retire_other_instances(&mut self, current: Option<&str>) -> Vec<String> {
        let Some(current) = current else { return Vec::new() };
        let mut retired = Vec::new();
        let mut kept = Vec::with_capacity(self.entries.len());
        for mut entry in std::mem::take(&mut self.entries) {
            let stale = entry.engine_instance_id.as_deref().is_some_and(|id| id != current);
            if stale {
                if let Some(responder) = entry.responder.take() {
                    responder.discard();
                }
                retired.push(entry.key());
            } else {
                kept.push(entry);
            }
        }
        self.entries = kept;
        retired
    }

    /// The engine reported a terminal outcome for a request.
    ///
    /// Removes it. A responder we still hold is answered by its own `Drop`,
    /// which the engine's registry treats as an already-resolved handle and
    /// ignores — the outcome that actually applied is the one the engine
    /// already published.
    pub fn on_resolved(&mut self, request_id: &str) -> bool {
        let before = self.entries.len();
        self.entries.retain(|e| e.handle.as_deref() != Some(request_id));
        before != self.entries.len()
    }

    /// Takes the current decision for answering, against the engine process
    /// that is on the other end *now*.
    ///
    /// `engine_instance_id` is not decoration: a decision raised by a
    /// previous process must not be answered against its replacement, whether
    /// through the raw round-trip (whose id means something different there)
    /// or through the out-of-band RPC (whose handle means nothing there).
    pub fn take_current(&mut self, engine_instance_id: Option<&str>) -> Delivery {
        if self.entries.is_empty() {
            return Delivery::None;
        }
        let mut entry = self.entries.remove(0);
        let stale = match (entry.engine_instance_id.as_deref(), engine_instance_id) {
            (Some(raised_by), Some(current)) => raised_by != current,
            // Nothing to compare: a legacy engine, or a connection that has
            // not named an instance. Refusing here would decline live
            // requests on every legacy connection.
            _ => false,
        };
        if stale {
            if let Some(responder) = entry.responder.take() {
                responder.discard();
            }
            return Delivery::Stale;
        }
        match entry.responder.take() {
            Some(responder) => Delivery::Raw(responder),
            None => match entry.handle {
                Some(request_id) => Delivery::Rpc { request_id },
                // No responder and no handle: nothing addressable. Only
                // reachable if a legacy engine's request was already
                // answered, in which case there is nothing left to do.
                None => Delivery::None,
            },
        }
    }

    /// Declines every decision the engine on the other end raised, and
    /// discards the rest.
    ///
    /// For shutting this client down while the connection is still open. A
    /// request raised by the process we are still talking to is *answered*:
    /// an error reply is the wire signal the engine reads as "the operator
    /// refused" (`deny` / `noAnswer.declined` / `reject`), so the tool call
    /// waiting on it fails closed at once instead of being left to the
    /// engine's end-of-connection sweep. No answer content is ever
    /// fabricated: a question aborts as `noAnswer`, never as an empty string.
    ///
    /// Anything this client cannot place on the current process — a handle
    /// from a replaced engine, or a legacy connection that never named one —
    /// is [`Responder::discard`]ed instead. A decline addressed to a
    /// connection whose engine has been replaced either goes nowhere or, over
    /// a proxy that kept the socket, resolves a request the *new* engine
    /// owns.
    ///
    /// Returns how many were declined.
    pub fn decline_live(&mut self, current: Option<&str>) -> usize {
        let mut declined = 0;
        for mut entry in std::mem::take(&mut self.entries) {
            let Some(responder) = entry.responder.take() else { continue };
            let known_current = matches!(
                (entry.engine_instance_id.as_deref(), current),
                (Some(raised_by), Some(now)) if raised_by == now
            );
            if known_current {
                responder.fail(
                    coda_proto::error_codes::REQUEST_CANCELLED,
                    "the operator closed this client without answering",
                );
                declined += 1;
            } else {
                responder.discard();
            }
        }
        declined
    }

    /// Drops every entry, e.g. because the engine process was replaced.
    ///
    /// Responders are discarded rather than dropped: dropping one *answers*
    /// it with a decline, and a decline addressed to a connection whose
    /// engine has been replaced either goes nowhere or — through a proxy that
    /// kept the socket — resolves a request the new engine owns.
    pub fn clear(&mut self) {
        for mut entry in std::mem::take(&mut self.entries) {
            if let Some(responder) = entry.responder.take() {
                responder.discard();
            }
        }
    }
}

/// Reconstructs the prompt a discovery DTO describes.
///
/// The `display` payload is the engine's own bounded projection, so this
/// reads exactly the fields it publishes and gives up (rather than inventing
/// an empty prompt) when they are not there.
pub fn prompt_from_dto(dto: &PendingRequestDto) -> Option<PendingPrompt> {
    let display = &dto.display;
    let text = |key: &str| -> Option<String> {
        match display.get(key)? {
            serde_json::Value::String(s) => Some(s.clone()),
            // Capped free text is `{text, omittedReason, fullLength}`.
            other => other.get("text").and_then(|v| v.as_str()).map(str::to_string),
        }
    };
    match dto.kind {
        PendingRequestKind::Permission => Some(PendingPrompt::Permission {
            tool: text("toolName").unwrap_or_default(),
            preview: text("inputPreview").unwrap_or_default(),
        }),
        PendingRequestKind::Question => Some(PendingPrompt::Question {
            question: text("question")?,
            options: display
                .get("options")
                .and_then(|v| serde_json::from_value(v.clone()).ok())
                .unwrap_or_default(),
            multi_select: display.get("multiSelect").and_then(|v| v.as_bool()).unwrap_or(false),
            allow_free_text: display
                .get("allowFreeText")
                .and_then(|v| v.as_bool())
                .unwrap_or(true),
        }),
        PendingRequestKind::PlanApproval => {
            Some(PendingPrompt::PlanApproval { plan: text("plan").unwrap_or_default() })
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use coda_proto::{encode_frame, FrameDecoder, RequestId};
    use serde_json::json;
    use tokio::sync::mpsc;

    /// A responder wired to a channel we can inspect, so "was it answered,
    /// and how?" is a real assertion rather than a mock's opinion.
    fn responder(id: i64) -> (Responder, mpsc::UnboundedReceiver<Vec<u8>>) {
        let (tx, rx) = mpsc::unbounded_channel();
        (Responder::new(RequestId::Number(id), tx), rx)
    }

    fn answered(rx: &mut mpsc::UnboundedReceiver<Vec<u8>>) -> Option<serde_json::Value> {
        let frame = rx.try_recv().ok()?;
        let mut decoder = FrameDecoder::new();
        decoder.feed(&frame);
        let bytes = decoder.next_frame().ok()??;
        serde_json::from_slice(&bytes).ok()
    }

    fn dto(handle: &str, kind: PendingRequestKind, display: serde_json::Value) -> PendingRequestDto {
        PendingRequestDto {
            request_id: handle.into(),
            kind,
            issued_at: "now".into(),
            turn_id: None,
            call_id: None,
            display,
            fail_closed_default: kind.fail_closed_default().into(),
        }
    }

    fn permission_dto(handle: &str) -> PendingRequestDto {
        dto(
            handle,
            PendingRequestKind::Permission,
            json!({ "toolName": "edit", "inputPreview": "src/main.rs" }),
        )
    }

    /// A snapshot at `cursor` that was issued before this client had taken
    /// delivery of any raw frame at all.
    ///
    /// The default for the cases about *engine* evidence: with no local
    /// receipt covered, only the announced `seq` can retire anything, so
    /// those tests cannot pass by accident on the local rule. The cases about
    /// local evidence build their `Authority` from a real watermark instead.
    fn snapshot_at(cursor: i64) -> Authority {
        Authority::Snapshot { cursor, received_before: 0 }
    }

    #[test]
    fn one_request_described_twice_opens_exactly_one_prompt() {
        // The raw frame and the discovery list are two descriptions of one
        // decision. Two modals for one decision is the bug.
        let mut pending = PendingInteractions::new();
        let dto = permission_dto("req-e1-1");
        assert!(pending.on_discovered(&dto, Some("e1".into()), None));

        let (responder, _rx) = responder(1);
        let opened = pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "src/main.rs".into() },
            responder,
        );
        assert!(!opened, "the raw frame described a request already on screen");
        assert_eq!(pending.len(), 1);
        assert!(pending.current().expect("current").has_responder());
    }

    #[test]
    fn adopting_a_discovered_request_never_drops_the_responder() {
        // Dropping a responder answers it — with a decline. A merge that let
        // the responder fall out of scope would deny a tool the user is about
        // to allow.
        let mut pending = PendingInteractions::new();
        pending.on_discovered(&permission_dto("req-e1-1"), Some("e1".into()), None);
        let (responder, mut rx) = responder(1);
        pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );
        assert!(answered(&mut rx).is_none(), "the responder must still be waiting");

        match pending.take_current(Some("e1")) {
            Delivery::Raw(responder) => responder.respond(json!({ "allow": true })),
            other => panic!("expected the raw round-trip, got {}", label(&other)),
        }
        let reply = answered(&mut rx).expect("the responder answered");
        assert_eq!(reply["result"]["allow"], true);
    }

    #[test]
    fn a_request_only_ever_discovered_is_answered_over_the_rpc() {
        let mut pending = PendingInteractions::new();
        pending.on_discovered(&permission_dto("req-e1-9"), Some("e1".into()), None);
        match pending.take_current(Some("e1")) {
            Delivery::Rpc { request_id } => assert_eq!(request_id, "req-e1-9"),
            other => panic!("expected the out-of-band path, got {}", label(&other)),
        }
    }

    #[test]
    fn several_concurrent_requests_queue_and_only_the_selected_one_is_answered() {
        // Background subagents can ask while a foreground turn is waiting.
        let mut pending = PendingInteractions::new();
        let (first, mut first_rx) = responder(1);
        let (second, mut second_rx) = responder(2);
        pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "a".into() },
            first,
        );
        pending.on_server_request(
            Some("req-e1-2".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "run".into(), preview: "b".into() },
            second,
        );
        assert_eq!(pending.len(), 2);
        assert_eq!(pending.current().expect("current").key(), "req-e1-1");

        match pending.take_current(Some("e1")) {
            Delivery::Raw(responder) => responder.respond(json!({ "allow": true })),
            other => panic!("expected raw, got {}", label(&other)),
        }
        assert_eq!(answered(&mut first_rx).expect("first answered")["result"]["allow"], true);
        assert!(answered(&mut second_rx).is_none(), "the queued request must be untouched");
        assert_eq!(pending.len(), 1);
        assert_eq!(pending.current().expect("current").key(), "req-e1-2");
    }

    #[test]
    fn resolving_out_of_band_removes_the_entry_it_names_and_no_other() {
        let mut pending = PendingInteractions::new();
        pending.on_discovered(&permission_dto("req-e1-1"), Some("e1".into()), None);
        pending.on_discovered(&permission_dto("req-e1-2"), Some("e1".into()), None);
        assert!(pending.on_resolved("req-e1-1"));
        assert_eq!(pending.keys(), ["req-e1-2"]);
        assert!(!pending.on_resolved("req-e1-1"), "a duplicate resolution is a no-op");
    }

    #[test]
    fn a_raw_request_announced_after_the_snapshot_is_kept_and_never_answered() {
        // The interleaving the ordering guarantees, and the one a naive
        // "retire everything absent" rule loses. The engine writes
        // `event/requestPending(seq)` before the raw `request/*` on the same
        // outgoing channel — but only the announcement goes through the event
        // fence, so during a resync the raw frame is handled at once while the
        // announcement is held. A snapshot taken *before* either was written
        // therefore lands with the request already registered here, and its
        // silence about it is not evidence.
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(1);
        pending.on_server_request(
            Some("req-e1-6".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );
        // The held announcement replays after the snapshot, stamping the entry
        // this client already had.
        pending.on_discovered(&permission_dto("req-e1-6"), Some("e1".into()), Some(12));

        let outcome = pending.reconcile(&[], Some("e1".into()), snapshot_at(9));

        assert!(outcome.retired.is_empty(), "a live approval was discarded: {outcome:?}");
        assert_eq!(pending.keys(), ["req-e1-6"]);
        assert!(pending.current().expect("current").has_responder(), "the round-trip was lost");
        assert!(answered(&mut rx).is_none(), "nothing may go out for a request that is still live");
    }

    #[test]
    fn a_handle_only_request_newer_than_the_snapshot_is_kept_too() {
        // Ownership of the round-trip is not what decides this. A decision
        // discovered from a *later* announcement is just as new as one whose
        // raw frame this client happens to hold, and an older snapshot is no
        // more entitled to retire it.
        let mut pending = PendingInteractions::new();
        pending.on_discovered(&permission_dto("req-e1-7"), Some("e1".into()), Some(20));

        let outcome = pending.reconcile(&[], Some("e1".into()), snapshot_at(15));

        assert!(outcome.retired.is_empty(), "a newer discovery was discarded: {outcome:?}");
        assert_eq!(pending.keys(), ["req-e1-7"]);
    }

    #[test]
    fn a_request_the_snapshot_was_late_enough_to_know_about_is_retired() {
        // The lost-resolution case. `event/requestResolved` never arrived — a
        // dropped ring, an overflowed hold — so this client still held the
        // round-trip for a decision the engine had already finished with.
        // Keeping it left a modal the operator could answer into a registry
        // that had reused the id.
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(1);
        pending.on_server_request(
            Some("req-e1-5".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );
        pending.on_discovered(&permission_dto("req-e1-5"), Some("e1".into()), Some(4));

        let outcome = pending.reconcile(&[], Some("e1".into()), snapshot_at(9));

        assert_eq!(outcome.retired, ["req-e1-5"]);
        assert!(pending.is_empty(), "a request the engine is not waiting for stayed outstanding");
        assert!(
            answered(&mut rx).is_none(),
            "a retired request must be discarded, never answered: its id has been reused"
        );
    }

    #[test]
    fn a_snapshot_retires_a_raw_frame_that_was_in_hand_before_it_was_issued() {
        // The raw-only case, and the one that would otherwise never end: the
        // announcement carrying the `seq` was lost and so was the resolution,
        // so the engine's ordering says nothing about this decision at all.
        // The read this client issued afterwards is still conclusive — the
        // engine registers a request before it writes it, and a fresh
        // `session/getState` is built, not cached — so silence means gone.
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(1);
        pending.on_server_request(
            Some("req-e1-3".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );
        // Issued now: everything received so far is below this watermark.
        let received_before = pending.raw_watermark();

        let outcome = pending.reconcile(
            &[],
            Some("e1".into()),
            Authority::Snapshot { cursor: 40, received_before },
        );

        assert_eq!(outcome.retired, ["req-e1-3"]);
        assert!(pending.is_empty(), "a decision nothing is waiting for stayed outstanding");
        assert!(
            answered(&mut rx).is_none(),
            "a retired request must be discarded, never answered: its id has been reused"
        );
    }

    #[test]
    fn a_snapshot_keeps_a_raw_frame_that_arrived_after_it_was_issued() {
        // The frame was still sitting unread in the channel when the read
        // went out, so the engine may well have registered it after building
        // the answer. Its absence proves nothing, and retiring on it would
        // abandon a tool call the engine is blocked on. The next read, issued
        // after this client took delivery, is the one that may speak.
        let mut pending = PendingInteractions::new();
        let received_before = pending.raw_watermark();
        let (responder, mut rx) = responder(2);
        pending.on_server_request(
            Some("req-e1-4".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );

        let outcome = pending.reconcile(
            &[],
            Some("e1".into()),
            Authority::Snapshot { cursor: 40, received_before },
        );
        assert!(outcome.retired.is_empty(), "a decision in flight was discarded: {outcome:?}");
        assert_eq!(pending.keys(), ["req-e1-4"]);
        assert!(answered(&mut rx).is_none());

        // The next read is issued after the frame was in hand, and settles it.
        let received_before = pending.raw_watermark();
        let outcome = pending.reconcile(
            &[],
            Some("e1".into()),
            Authority::Snapshot { cursor: 41, received_before },
        );
        assert_eq!(outcome.retired, ["req-e1-4"]);
        assert!(answered(&mut rx).is_none(), "still nothing may be sent for it");
    }

    #[test]
    fn an_announcement_never_retires_on_this_clients_own_receipts() {
        // Local evidence is sound only against a list built for a read this
        // client issued. An `event/requestPending` is broadcast on the
        // engine's own schedule, so nothing can be placed relative to it, and
        // the variant carrying it structurally cannot see a watermark.
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(3);
        pending.on_server_request(
            Some("req-e1-5".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );

        let outcome = pending.reconcile(&[], Some("e1".into()), Authority::Announced(99));

        assert!(outcome.retired.is_empty(), "an announcement used a local receipt: {outcome:?}");
        assert_eq!(pending.keys(), ["req-e1-5"]);
        assert!(answered(&mut rx).is_none());
    }

    #[test]
    fn an_announcement_newer_than_the_snapshot_outranks_an_older_receipt() {
        // Both kinds of evidence, disagreeing. The raw frame was in hand
        // before the read went out — local evidence would retire it — but the
        // engine has since said the request was announced *after* the
        // snapshot's cursor. The engine's own ordering is the better
        // evidence and wins: this decision is live.
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(4);
        pending.on_server_request(
            Some("req-e1-6".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );
        let received_before = pending.raw_watermark();
        // The held announcement replays: seq 30, after the snapshot below.
        pending.on_discovered(&permission_dto("req-e1-6"), Some("e1".into()), Some(30));

        let outcome = pending.reconcile(
            &[],
            Some("e1".into()),
            Authority::Snapshot { cursor: 20, received_before },
        );

        assert!(
            outcome.retired.is_empty(),
            "the local receipt overrode the engine's own ordering: {outcome:?}"
        );
        assert_eq!(pending.keys(), ["req-e1-6"]);
        assert!(answered(&mut rx).is_none());
    }

    #[test]
    fn a_handle_only_request_is_never_retired_on_a_receipt_it_does_not_have() {
        // Discovery is somebody else's evidence: it says when the engine
        // announced something, not when this client took delivery of
        // anything. A handle-only entry with no announced seq therefore has
        // nothing a snapshot can weigh, however late the read was issued.
        let mut pending = PendingInteractions::new();
        pending.on_discovered(&permission_dto("req-e1-7"), Some("e1".into()), None);
        let received_before = pending.raw_watermark();

        let outcome = pending.reconcile(
            &[],
            Some("e1".into()),
            Authority::Snapshot { cursor: 99, received_before },
        );

        assert!(outcome.retired.is_empty(), "a handle-only entry was retired: {outcome:?}");
        assert_eq!(pending.keys(), ["req-e1-7"]);
    }

    #[test]
    fn an_unsequenced_request_is_never_retired_on_a_guess() {        // Two ways to be unknown, and neither may be resolved by assuming the
        // worst: a raw round-trip whose announcement never arrived (so this
        // client has no engine time for it), and a list with no engine time on
        // it at all (a legacy engine that stamps no sequence).
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(1);
        pending.on_server_request(
            Some("req-e1-8".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );
        pending.on_discovered(&permission_dto("req-e1-9"), Some("e1".into()), Some(3));

        // Unknown entry, sequenced list.
        let outcome = pending.reconcile(&[], Some("e1".into()), snapshot_at(99));
        assert_eq!(outcome.retired, ["req-e1-9"], "the known one is still retired");
        assert_eq!(pending.keys(), ["req-e1-8"], "an unsequenced entry was retired on a guess");

        // Known entry, unsequenced list.
        pending.on_discovered(&permission_dto("req-e1-9"), Some("e1".into()), Some(3));
        let outcome = pending.reconcile(&[], Some("e1".into()), Authority::Unsequenced);
        assert!(outcome.retired.is_empty(), "an unsequenced list retired something: {outcome:?}");
        assert_eq!(pending.keys(), ["req-e1-8", "req-e1-9"]);
        assert!(answered(&mut rx).is_none(), "nothing was answered for an unknown request");
    }

    #[test]
    fn a_snapshot_keeps_what_it_still_lists_however_this_client_heard_of_it() {
        let mut pending = PendingInteractions::new();
        let (responder, _rx) = responder(3);
        pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );

        let outcome = pending.reconcile(
            &[permission_dto("req-e1-1"), permission_dto("req-e1-2")],
            Some("e1".into()),
            snapshot_at(30),
        );

        assert_eq!(outcome.opened, ["req-e1-2"]);
        assert!(outcome.retired.is_empty());
        assert_eq!(pending.keys(), ["req-e1-1", "req-e1-2"]);
        assert!(pending.current().expect("current").has_responder(), "the round-trip was lost");
    }

    #[test]
    fn a_request_a_snapshot_listed_can_be_retired_by_a_later_one() {
        // A request this client only ever saw in a list is stamped with that
        // list's own engine time, so the next list that is at least as late
        // and no longer names it retires it. Without the stamp every
        // discovered request would be permanently unknown.
        let mut pending = PendingInteractions::new();
        let opened = pending.reconcile(
            &[permission_dto("req-e1-1")],
            Some("e1".into()),
            snapshot_at(5),
        );
        assert_eq!(opened.opened, ["req-e1-1"]);

        let outcome = pending.reconcile(&[], Some("e1".into()), snapshot_at(6));

        assert_eq!(outcome.retired, ["req-e1-1"]);
        assert!(pending.is_empty());
    }

    #[test]
    fn discovery_stamps_the_earliest_engine_time_it_was_told() {
        // The announcement is when the engine raised it; a later list that
        // still names it is a second sighting, not a later birth. Taking the
        // later one would make the entry look newer than it is and keep it
        // outstanding after the engine had finished with it.
        let mut pending = PendingInteractions::new();
        pending.on_discovered(&permission_dto("req-e1-2"), Some("e1".into()), Some(10));
        pending.on_discovered(&permission_dto("req-e1-2"), Some("e1".into()), Some(4));

        let outcome = pending.reconcile(&[], Some("e1".into()), snapshot_at(7));

        assert_eq!(outcome.retired, ["req-e1-2"]);
    }

    #[test]
    fn a_discovered_question_keeps_its_options_and_capped_text() {
        let dto = dto(
            "req-e1-3",
            PendingRequestKind::Question,
            json!({
                "question": { "text": "Which one?", "omittedReason": "tooLarge", "fullLength": 90 },
                "options": ["a", "b"],
                "multiSelect": false,
                "allowFreeText": true,
            }),
        );
        let prompt = prompt_from_dto(&dto).expect("a question prompt");
        match prompt {
            PendingPrompt::Question { question, options, .. } => {
                assert_eq!(question, "Which one?");
                assert_eq!(options, ["a", "b"]);
            }
            other => panic!("expected a question, got {other:?}"),
        }
    }

    #[test]
    fn a_replaced_engine_retires_every_decision_the_previous_process_raised() {
        // An engine restart (or a proxy swapping the engine behind the same
        // connection) invalidates every handle the old process minted. An
        // entry we still hold the raw responder for must go too — and it must
        // go *silently*: the numeric id it carries addresses nothing in the
        // new process, so answering it there would resolve some other request.
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(1);
        pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );
        pending.on_discovered(&permission_dto("req-e1-2"), Some("e1".into()), None);

        let retired = pending.retire_other_instances(Some("e2"));

        assert_eq!(retired.len(), 2, "both entries belonged to the previous engine");
        assert!(pending.is_empty());
        assert!(
            answered(&mut rx).is_none(),
            "a stale request id must never be answered against the replacement engine"
        );
    }

    #[test]
    fn retiring_keeps_the_current_instances_requests_and_legacy_ones() {
        let mut pending = PendingInteractions::new();
        pending.on_discovered(&permission_dto("req-e2-1"), Some("e2".into()), None);
        // A legacy engine names no instance; there is nothing to compare, and
        // discarding on a guess would decline a live request.
        pending.on_discovered(&permission_dto("req-legacy"), None, None);

        assert!(pending.retire_other_instances(Some("e2")).is_empty());
        assert_eq!(pending.keys(), ["req-e2-1", "req-legacy"]);
    }

    #[test]
    fn reconciling_against_a_new_instance_retires_the_previous_ones_entries() {
        // The list is the *new* engine's. Keeping a responder-backed entry
        // from the old one — the rule that protects a live local request —
        // would keep a request no engine is waiting on.
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(3);
        pending.on_server_request(
            Some("req-e1-9".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );

        let outcome = pending.reconcile(
            &[permission_dto("req-e2-1")],
            Some("e2".into()),
            snapshot_at(40),
        );

        assert_eq!(outcome.opened, ["req-e2-1"]);
        assert_eq!(outcome.retired, ["req-e1-9"]);
        assert_eq!(pending.keys(), ["req-e2-1"], "the old process's request survived");
        assert!(answered(&mut rx).is_none(), "and it was answered on the new engine");
    }

    #[test]
    fn answering_never_addresses_a_request_raised_by_a_different_engine() {
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(4);
        pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );

        match pending.take_current(Some("e2")) {
            Delivery::Stale => {}
            other => panic!("expected the stale path, got {}", label(&other)),
        }
        assert!(pending.is_empty(), "the stale entry is not left on screen");
        assert!(answered(&mut rx).is_none(), "nothing may be sent for a stale request");
    }

    #[test]
    fn clearing_discards_responders_rather_than_declining_on_a_new_connection() {
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(5);
        pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );
        pending.clear();
        assert!(answered(&mut rx).is_none());
    }

    #[test]
    fn shutting_down_declines_the_live_engines_own_decisions_explicitly() {
        // The engine on the other end raised this and is still blocked on it.
        // An explicit decline resolves it now, as a decision; letting the
        // connection close instead leaves the engine to infer a disconnect,
        // which is a different (and later) story.
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(7);
        pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );

        assert_eq!(pending.decline_live(Some("e1")), 1);
        assert!(pending.is_empty());

        let reply = answered(&mut rx).expect("the live request was answered");
        assert_eq!(
            reply["error"]["code"].as_i64(),
            Some(coda_proto::error_codes::REQUEST_CANCELLED),
            "an explicit decline is an error reply, which the engine reads as \
             'the operator refused': {reply}"
        );
    }

    #[test]
    fn shutting_down_still_discards_a_replaced_engines_decisions() {
        // A handle minted by a process that is gone addresses nothing in its
        // replacement — and over a proxy that kept the socket it would
        // address whatever now holds that number.
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(8);
        pending.on_server_request(
            Some("req-e1-1".into()),
            Some("e1".into()),
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );

        assert_eq!(pending.decline_live(Some("e2")), 0, "nothing may be answered on e2");
        assert!(pending.is_empty());
        assert!(answered(&mut rx).is_none(), "a stale request must not be answered");
    }

    #[test]
    fn shutting_down_declines_nothing_it_cannot_place() {
        // A legacy connection names no instance, so "is this the process that
        // asked?" has no answer. Nothing is answered on a guess; the engine's
        // own end-of-connection handling applies instead.
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(9);
        pending.on_server_request(
            Some("req-1".into()),
            None,
            PendingPrompt::Permission { tool: "edit".into(), preview: "p".into() },
            responder,
        );

        assert_eq!(pending.decline_live(None), 0);
        assert!(answered(&mut rx).is_none());
    }

    #[test]
    fn shutting_down_declines_a_question_without_inventing_an_answer() {
        // The decline must reach the engine as a refusal, never as an empty
        // answer: `noAnswer` is the outcome a question aborts with.
        let mut pending = PendingInteractions::new();
        let (responder, mut rx) = responder(10);
        pending.on_server_request(
            Some("req-e1-2".into()),
            Some("e1".into()),
            PendingPrompt::Question {
                question: "Which?".into(),
                options: vec!["a".into()],
                multi_select: false,
                allow_free_text: true,
            },
            responder,
        );

        assert_eq!(pending.decline_live(Some("e1")), 1);
        let reply = answered(&mut rx).expect("the question was answered");
        assert!(reply.get("result").is_none(), "an answer was fabricated: {reply}");
        assert_eq!(
            reply["error"]["code"].as_i64(),
            Some(coda_proto::error_codes::REQUEST_CANCELLED),
            "{reply}"
        );
    }

    fn label(delivery: &Delivery) -> &'static str {
        match delivery {
            Delivery::Raw(_) => "raw",
            Delivery::Rpc { .. } => "rpc",
            Delivery::Stale => "stale",
            Delivery::None => "none",
        }
    }

    // Keeps the import used in every build configuration.
    #[test]
    fn frames_encode() {
        assert!(!encode_frame(b"{}").is_empty());
    }
}
