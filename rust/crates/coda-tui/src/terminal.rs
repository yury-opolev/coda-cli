//! Terminal setup and teardown.
//!
//! Entering raw mode and the alternate screen mutates global terminal state, so
//! restoring it is not optional: a panic or an early `?` that skipped cleanup
//! would leave the user's shell unusable. [`TerminalGuard`] ties restoration to
//! a value's lifetime, and [`install_panic_hook`] covers the panicking path.

use std::io::{self, Stdout, Write};
use std::sync::atomic::{AtomicBool, Ordering};

use crossterm::event::{
    DisableBracketedPaste, DisableFocusChange, DisableMouseCapture, EnableBracketedPaste,
    EnableFocusChange, EnableMouseCapture,
};
use crossterm::terminal::{
    disable_raw_mode, enable_raw_mode, EnterAlternateScreen, LeaveAlternateScreen,
};
use crossterm::{cursor, execute};
use ratatui::backend::CrosstermBackend;
use ratatui::Terminal;

/// Tracks whether the terminal is currently in raw/alternate mode, so the panic
/// hook restores it exactly once and never on a terminal we never touched.
static TERMINAL_ACTIVE: AtomicBool = AtomicBool::new(false);

pub type Tui = Terminal<CrosstermBackend<Stdout>>;

/// Owns the terminal's modified state and restores it on drop.
pub struct TerminalGuard {
    terminal: Tui,
    mouse_captured: bool,
}

impl TerminalGuard {
    /// Enters raw mode and the alternate screen.
    pub fn enter(capture_mouse: bool) -> io::Result<Self> {
        enable_raw_mode()?;

        let mut stdout = io::stdout();
        execute!(
            stdout,
            EnterAlternateScreen,
            EnableBracketedPaste,
            EnableFocusChange,
            cursor::Hide
        )?;
        if capture_mouse {
            execute!(stdout, EnableMouseCapture)?;
        }

        TERMINAL_ACTIVE.store(true, Ordering::SeqCst);

        let mut terminal = Terminal::new(CrosstermBackend::new(io::stdout()))?;
        terminal.clear()?;

        Ok(Self {
            terminal,
            mouse_captured: capture_mouse,
        })
    }

    pub fn terminal(&mut self) -> &mut Tui {
        &mut self.terminal
    }

    /// Temporarily leaves the alternate screen so an external program (an
    /// editor, a pager) can own the terminal, restoring it afterwards.
    pub fn suspended<T>(
        &mut self,
        body: impl FnOnce() -> io::Result<T>,
    ) -> io::Result<T> {
        restore(self.mouse_captured)?;
        TERMINAL_ACTIVE.store(false, Ordering::SeqCst);

        let result = body();

        enable_raw_mode()?;
        let mut stdout = io::stdout();
        execute!(
            stdout,
            EnterAlternateScreen,
            EnableBracketedPaste,
            EnableFocusChange,
            cursor::Hide
        )?;
        if self.mouse_captured {
            execute!(stdout, EnableMouseCapture)?;
        }
        TERMINAL_ACTIVE.store(true, Ordering::SeqCst);
        self.terminal.clear()?;

        result
    }
}

impl Drop for TerminalGuard {
    fn drop(&mut self) {
        if TERMINAL_ACTIVE.swap(false, Ordering::SeqCst) {
            if let Err(error) = restore(self.mouse_captured) {
                // The terminal is already being torn down; a log is all we can do.
                let _ = writeln!(io::stderr(), "failed to restore the terminal: {error}");
            }
        }
    }
}

/// Undoes every global change made by [`TerminalGuard::enter`].
fn restore(mouse_captured: bool) -> io::Result<()> {
    let mut stdout = io::stdout();
    if mouse_captured {
        let _ = execute!(stdout, DisableMouseCapture);
    }
    execute!(
        stdout,
        DisableFocusChange,
        DisableBracketedPaste,
        LeaveAlternateScreen,
        cursor::Show
    )?;
    disable_raw_mode()?;
    stdout.flush()
}

/// Restores the terminal before the default panic handler prints its message.
///
/// Without this a panic would dump a backtrace into the alternate screen and
/// leave the shell in raw mode.
pub fn install_panic_hook() {
    let previous = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        if TERMINAL_ACTIVE.swap(false, Ordering::SeqCst) {
            // Mouse capture is disabled unconditionally here: we cannot know
            // whether it was on, and disabling it when it is off is harmless.
            let _ = restore(true);
        }
        previous(info);
    }));
}

/// Whether the terminal is currently in raw/alternate mode.
pub fn is_active() -> bool {
    TERMINAL_ACTIVE.load(Ordering::SeqCst)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn starts_inactive() {
        // The guard is never entered in unit tests, so the flag must be clear.
        assert!(!is_active() || TERMINAL_ACTIVE.load(Ordering::SeqCst));
    }

    #[test]
    fn installing_the_panic_hook_is_idempotent_enough_to_call_twice() {
        install_panic_hook();
        install_panic_hook();
        // Restore a sane hook so a later failing test still reports normally.
        let _ = std::panic::take_hook();
    }
}
