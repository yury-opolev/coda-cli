//! Terminal input, as the application sees it: decoded actions from the
//! single input owner in `crate::terminal`, dispatched to keys, the pointer
//! and paste transactions.

use anyhow::Result;
use crossterm::event::KeyEventKind;

use super::App;
use crate::input::InputAction;
use crate::terminal::TerminalGuard;
use crate::transcript::NoticeLevel;

impl App {
    pub(super) async fn on_input(
        &mut self,
        action: InputAction,
        guard: &mut TerminalGuard,
    ) -> Result<()> {
        if let InputAction::Resize(..) = action {
            // Force a relayout; cached rows were wrapped for the old width.
            self.laid_out_width = 0;
            self.dirty = true;
            guard.terminal().autoresize()?;
            return Ok(());
        }
        self.apply_input(action).await;
        Ok(())
    }

    /// Everything terminal input does to the application except resizing the
    /// real terminal, so tests can drive it without one.
    ///
    /// A paste is data for one editor and never passes through the key
    /// bindings: nothing here can turn pasted text into a submission, a
    /// command, an accepted completion or an approval.
    pub(super) async fn apply_input(&mut self, action: InputAction) {
        match action {
            // Windows reports key *release* (and repeat) events as well as
            // presses. Acting on a release is wrong for every binding, but it
            // is actively broken for the two-press chords: the release of the
            // first Ctrl+C disarms the chord immediately, so the second press
            // only ever re-arms and the app can never be exited from the
            // keyboard. Repeats are kept so held keys still autorepeat.
            InputAction::Key(key)
                if matches!(key.kind, KeyEventKind::Press | KeyEventKind::Repeat) =>
            {
                self.on_key(key).await
            }
            InputAction::Key(_) => {}
            InputAction::PasteStarted { txn } => self.begin_paste(txn),
            InputAction::PasteText { text, source, txn } => {
                self.apply_paste_text(text, source, txn)
            }
            InputAction::PasteAborted { txn, reason, bytes } => {
                self.abort_paste(txn, reason, bytes)
            }
            InputAction::Resize(..) => {
                self.laid_out_width = 0;
                self.dirty = true;
            }
            InputAction::Mouse(mouse) => self.on_mouse(mouse).await,
            InputAction::FocusGained | InputAction::FocusLost => {}
        }
    }

    /// Says once, up front, when this console cannot report where a paste
    /// begins and ends: a multi-line terminal paste would then arrive as
    /// typing, and every line break as Enter. Coda does not guess.
    pub(super) fn warn_if_paste_is_unframed(&mut self, status: Option<crate::input::BackendStatus>) {
        if status.is_some_and(|status| !status.paste_framing) {
            self.notice(
                "This console does not report paste boundaries, so a multi-line paste from the \
                 terminal may be sent line by line. Use Alt+V to paste from the clipboard \
                 instead.",
                NoticeLevel::Warning,
            );
        }
    }
}
