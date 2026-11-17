//! Terminal input owned by Coda.
//!
//! On Windows, crossterm reads native console records and never assembles a
//! bracketed paste, so a multiline paste would arrive as a burst of key
//! presses where every newline submits. Coda therefore reads the console
//! records itself and decodes them here. The [`decoder`] is pure: it is fed
//! records and a clock and produces [`InputAction`]s, so it compiles and is
//! tested on every platform.

pub mod decoder;
pub mod keys;
#[cfg(windows)]
pub mod windows;

use crossterm::event::{Event, KeyEvent, MouseEvent};

/// Where pasted text came from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PasteSource {
    /// A bracketed paste delivered by the terminal.
    Terminal,
    /// Text read from the system clipboard by Coda itself.
    Clipboard,
}

impl PasteSource {
    pub fn as_str(self) -> &'static str {
        match self {
            PasteSource::Terminal => "terminal",
            PasteSource::Clipboard => "clipboard",
        }
    }
}

/// Why an open paste was dropped instead of delivered.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PasteAbort {
    /// The paste exceeded [`decoder::MAX_PASTE_BYTES`].
    TooLarge,
    /// The paste never saw its end marker before the stall timeout.
    Incomplete,
    /// The paste was cancelled by the application.
    Cancelled,
}

impl PasteAbort {
    pub fn as_str(self) -> &'static str {
        match self {
            PasteAbort::TooLarge => "too_large",
            PasteAbort::Incomplete => "incomplete",
            PasteAbort::Cancelled => "cancelled",
        }
    }
}

/// One decoded unit of terminal input.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum InputAction {
    Key(KeyEvent),
    Mouse(MouseEvent),
    Resize(u16, u16),
    FocusGained,
    FocusLost,
    PasteStarted {
        txn: u64,
    },
    PasteText {
        text: String,
        source: PasteSource,
        txn: u64,
    },
    PasteAborted {
        txn: u64,
        reason: PasteAbort,
        bytes: usize,
    },
}

/// Which input backend is active, for diagnostics.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BackendStatus {
    /// `"windows-vt"`, `"crossterm-native"` or `"crossterm"`.
    pub backend: &'static str,
    /// Whether pastes arrive framed (bracketed) rather than as key presses.
    pub paste_framing: bool,
    /// `"win32-input-mode"`, `"native-records"` or `"vt"`.
    pub key_encoding: &'static str,
}

/// Converts a crossterm event (Unix backend / native fallback) into actions.
pub fn from_crossterm(event: Event, next_txn: &mut u64) -> Vec<InputAction> {
    match event {
        Event::Key(key) => vec![InputAction::Key(key)],
        Event::Mouse(mouse) => vec![InputAction::Mouse(mouse)],
        Event::Resize(cols, rows) => vec![InputAction::Resize(cols, rows)],
        Event::FocusGained => vec![InputAction::FocusGained],
        Event::FocusLost => vec![InputAction::FocusLost],
        Event::Paste(text) => {
            *next_txn += 1;
            let txn = *next_txn;
            vec![
                InputAction::PasteStarted { txn },
                InputAction::PasteText {
                    text,
                    source: PasteSource::Terminal,
                    txn,
                },
            ]
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_crossterm_paste_opens_and_fills_a_numbered_transaction() {
        let mut txn = 4;
        let actions = from_crossterm(Event::Paste("a\nb".into()), &mut txn);
        assert_eq!(
            actions,
            vec![
                InputAction::PasteStarted { txn: 5 },
                InputAction::PasteText {
                    text: "a\nb".into(),
                    source: PasteSource::Terminal,
                    txn: 5
                },
            ]
        );
        assert_eq!(txn, 5);
    }
}
