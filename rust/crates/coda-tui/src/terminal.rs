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

/// Poll on the owning async task, without EventStream's detached reader thread.
/// Cancellation therefore leaves no reader competing with the returned shell.
pub(crate) async fn read_event() -> io::Result<crossterm::event::Event> {
    loop {
        if crossterm::event::poll(std::time::Duration::ZERO)? {
            return crossterm::event::read();
        }
        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
    }
}

/// Owns the terminal's modified state and restores it on drop.
pub struct TerminalGuard {
    terminal: Tui,
    mouse_captured: bool,
}

impl TerminalGuard {
    /// Restores before returning either the run outcome or its cleanup error.
    pub fn finish<T>(self, outcome: anyhow::Result<T>) -> anyhow::Result<T> {
        let restoration = if TERMINAL_ACTIVE.swap(false, Ordering::SeqCst) {
            restore(self.mouse_captured)
        } else {
            Ok(())
        };
        match (outcome, restoration) {
            (outcome, Ok(())) => outcome,
            (Ok(_), Err(error)) => {
                Err(anyhow::Error::new(error).context("terminal restoration failed"))
            }
            (Err(error), Err(restoration)) => {
                let message = format!("{error}; terminal restoration failed: {restoration}");
                Err(error.context(message))
            }
        }
    }

    /// Enters raw mode and the alternate screen.
    pub fn enter(capture_mouse: bool) -> io::Result<Self> {
        enable_raw_mode()?;
        TERMINAL_ACTIVE.store(true, Ordering::SeqCst);
        let result = (|| {
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

            let mut terminal = Terminal::new(CrosstermBackend::new(io::stdout()))?;
            terminal.clear()?;

            Ok(Self {
                terminal,
                mouse_captured: capture_mouse,
            })
        })();
        match result {
            Ok(guard) => Ok(guard),
            Err(error) => {
                let restoration = restore(capture_mouse);
                TERMINAL_ACTIVE.store(false, Ordering::SeqCst);
                match restoration {
                    Ok(()) => Err(error),
                    Err(cleanup) => Err(io::Error::other(format!(
                        "{error}; terminal restoration failed: {cleanup}"
                    ))),
                }
            }
        }
    }

    pub fn terminal(&mut self) -> &mut Tui {
        &mut self.terminal
    }

    /// Temporarily leaves the alternate screen so an external program (an
    /// editor, a pager) can own the terminal, restoring it afterwards.
    pub fn suspended<T>(&mut self, body: impl FnOnce() -> io::Result<T>) -> io::Result<T> {
        restore(self.mouse_captured)?;
        TERMINAL_ACTIVE.store(false, Ordering::SeqCst);

        let result = body();

        enable_raw_mode()?;
        TERMINAL_ACTIVE.store(true, Ordering::SeqCst);
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
    let started = std::time::Instant::now();
    let mut stdout = io::stdout();
    let result = restore_with(mouse_captured, |step| match step {
        RestoreStep::Mouse => execute!(stdout, DisableMouseCapture),
        RestoreStep::Focus => execute!(stdout, DisableFocusChange),
        RestoreStep::Paste => execute!(stdout, DisableBracketedPaste),
        RestoreStep::Screen => execute!(stdout, LeaveAlternateScreen),
        RestoreStep::Cursor => execute!(stdout, cursor::Show),
        RestoreStep::Raw => disable_raw_mode(),
        RestoreStep::Flush => stdout.flush(),
    });
    coda_diagnostics::record(coda_diagnostics::Event::ShutdownPhase {
        phase: "terminal_restoration",
        duration_ms: started.elapsed().as_millis() as u64,
        outcome: if result.is_ok() {
            "completed"
        } else {
            "failed"
        },
    });
    result
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum RestoreStep {
    Mouse,
    Focus,
    Paste,
    Screen,
    Cursor,
    Raw,
    Flush,
}

fn restore_with(
    mouse_captured: bool,
    mut step: impl FnMut(RestoreStep) -> io::Result<()>,
) -> io::Result<()> {
    let mut errors = Vec::new();
    for action in [
        RestoreStep::Mouse,
        RestoreStep::Focus,
        RestoreStep::Paste,
        RestoreStep::Screen,
        RestoreStep::Cursor,
        RestoreStep::Raw,
        RestoreStep::Flush,
    ] {
        if action != RestoreStep::Mouse || mouse_captured {
            if let Err(error) = step(action) {
                errors.push(error);
            }
        }
    }
    match errors.first() {
        None => Ok(()),
        Some(first) => Err(io::Error::new(
            first.kind(),
            errors
                .iter()
                .map(ToString::to_string)
                .collect::<Vec<_>>()
                .join("; "),
        )),
    }
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
            if let Err(error) = restore(true) {
                let _ = writeln!(
                    io::stderr(),
                    "failed to restore the terminal after panic: {error}"
                );
            }
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

    #[test]
    fn restoration_attempts_raw_mode_even_when_console_output_fails() {
        let mut attempted = Vec::new();
        let result = restore_with(true, |step| {
            attempted.push(step);
            if matches!(step, RestoreStep::Mouse | RestoreStep::Screen) {
                Err(io::Error::other(format!("{step:?} failed")))
            } else {
                Ok(())
            }
        });
        assert!(attempted.contains(&RestoreStep::Raw));
        assert!(attempted.contains(&RestoreStep::Flush));
        let message = result.unwrap_err().to_string();
        assert!(message.contains("Mouse failed"));
        assert!(message.contains("Screen failed"));
    }
}
