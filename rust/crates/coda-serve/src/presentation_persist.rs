//! Persists the presentation timeline into the session transcript.
//!
//! There is exactly one writer of a session's transcript in a serve host:
//! every save that touches it goes through [`PresentationPersister`]'s
//! writer lock, and the timeline is snapshotted *inside* that lock, so a
//! write can never replace a newer timeline with an older one.
//!
//! Observers (the message bus, the task manager) never do I/O: a change made
//! while no turn runs calls [`PresentationPersister::notify`], which wakes a
//! single background task. Changes made during a turn are written by the
//! turn-end save, in the same atomic write as the messages.

use std::path::PathBuf;
use std::sync::{Arc, OnceLock, Weak};

use coda_agent::{SessionTranscriptStore, StoredUsage};
use coda_llm::Message;
use tokio::sync::{Mutex, Notify};

use crate::state::EngineState;

pub struct PresentationPersister {
    working_dir: PathBuf,
    state: Weak<EngineState>,
    writer: Mutex<()>,
    wake: Arc<Notify>,
    started: OnceLock<()>,
    self_ref: OnceLock<Weak<PresentationPersister>>,
}

impl PresentationPersister {
    /// Builds the persister and installs it as `state`'s presentation hook.
    pub fn install(working_dir: impl Into<PathBuf>, state: &Arc<EngineState>) -> Arc<Self> {
        let persister = Arc::new(Self {
            working_dir: working_dir.into(),
            state: Arc::downgrade(state),
            writer: Mutex::new(()),
            wake: Arc::new(Notify::new()),
            started: OnceLock::new(),
            self_ref: OnceLock::new(),
        });
        let _ = persister.self_ref.set(Arc::downgrade(&persister));
        let hook = Arc::downgrade(&persister);
        state.set_presentation_hook(Arc::new(move || {
            if let Some(persister) = hook.upgrade() {
                persister.notify();
            }
        }));
        persister
    }

    /// Asks the background task to write the timeline. Never blocks, never
    /// does I/O; safe to call with any lock held.
    pub fn notify(&self) {
        self.ensure_started();
        self.wake.notify_one();
    }

    /// Lazy because the host is built synchronously, sometimes with no Tokio
    /// runtime at all. A wake before the task exists is kept by `Notify`.
    fn ensure_started(&self) {
        if self.started.get().is_some() {
            return;
        }
        let Ok(runtime) = tokio::runtime::Handle::try_current() else { return };
        let Some(me) = self.self_ref.get().cloned() else { return };
        if self.started.set(()).is_err() {
            return;
        }
        let wake = Arc::clone(&self.wake);
        runtime.spawn(async move {
            loop {
                wake.notified().await;
                // Holds the persister only while writing, never while parked.
                let Some(persister) = me.upgrade() else { return };
                persister.persist_now().await;
            }
        });
    }

    /// Writes the timeline into the existing transcript now, unless the
    /// newest change is already on disk. A session with no transcript yet
    /// is left alone; its first save carries the timeline.
    pub async fn persist_now(&self) {
        let _writer = self.writer.lock().await;
        let Some(state) = self.state.upgrade() else { return };
        let checkpoint = state.presentation_checkpoint();
        if checkpoint.persisted {
            return;
        }
        let store = SessionTranscriptStore::new(&self.working_dir);
        match store.save_presentation(&checkpoint.session_id, &checkpoint.value).await {
            Ok(true) => state.mark_presentation_durable(&checkpoint),
            Ok(false) => {}
            Err(error) => {
                tracing::warn!(%error, "cannot persist the presentation timeline");
                state.mark_presentation_persist_failed(&checkpoint);
            }
        }
    }

    /// Saves the conversation of `session_id` together with the timeline,
    /// in one atomic write, under the single writer lock.
    pub async fn save_transcript(
        &self,
        session_id: &str,
        messages: &[Message],
        usage: Option<&StoredUsage>,
    ) -> std::io::Result<()> {
        let _writer = self.writer.lock().await;
        let store = SessionTranscriptStore::new(&self.working_dir);
        let state = self.state.upgrade();
        let checkpoint = state
            .as_ref()
            .map(|s| s.presentation_checkpoint())
            .filter(|c| c.session_id == session_id);
        let result = store
            .save_with_presentation(
                session_id,
                messages,
                None,
                usage,
                checkpoint.as_ref().map(|c| &c.value),
            )
            .await;
        if let (Some(state), Some(checkpoint)) = (state, checkpoint) {
            match &result {
                Ok(()) if !messages.is_empty() => state.mark_presentation_durable(&checkpoint),
                Ok(()) => {}
                Err(_) => state.mark_presentation_persist_failed(&checkpoint),
            }
        }
        result
    }

    /// Holds the single writer lock for a save that does not go through
    /// this type (for example, a fork writing a new transcript).
    pub async fn lock_writer(&self) -> tokio::sync::MutexGuard<'_, ()> {
        self.writer.lock().await
    }
}

impl Drop for PresentationPersister {
    fn drop(&mut self) {
        self.wake.notify_waiters();
    }
}
