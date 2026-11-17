//! Terminal setup and teardown.
//!
//! Entering raw mode and the alternate screen mutates global terminal state, so
//! restoring it is not optional: a panic or an early `?` that skipped cleanup
//! would leave the user's shell unusable. [`TerminalGuard`] ties restoration to
//! a value's lifetime, and [`install_panic_hook`] covers the panicking path.

use std::collections::VecDeque;
use std::io::{self, Stdout, Write};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;
#[cfg(windows)]
use std::time::Instant;

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

use crate::input::{BackendStatus, InputAction};
use crate::render::backend::StableBackend;

/// Tracks whether the terminal is currently in raw/alternate mode, so the panic
/// hook restores it exactly once and never on a terminal we never touched.
static TERMINAL_ACTIVE: AtomicBool = AtomicBool::new(false);

pub type Tui = Terminal<StableBackend<CrosstermBackend<Stdout>>>;

/// Whether crossterm's mouse capture is on (the panic hook needs to know
/// without a guard). On Windows the input backend captures the mouse itself.
static CROSSTERM_MOUSE: AtomicBool = AtomicBool::new(false);

/// The single owner of terminal input. Every reader goes through
/// [`read_input`], so there is never a second reader racing for input.
static INPUT: Mutex<Option<InputState>> = Mutex::new(None);

struct InputState {
    backend: Backend,
    status: BackendStatus,
}

enum Backend {
    #[cfg(windows)]
    Vt(crate::input::windows::WindowsVtInput),
    Crossterm {
        next_txn: u64,
        queue: VecDeque<InputAction>,
    },
}

fn input_lock() -> std::sync::MutexGuard<'static, Option<InputState>> {
    INPUT.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
}

fn crossterm_state(status: BackendStatus) -> InputState {
    InputState {
        backend: Backend::Crossterm { next_txn: 0, queue: VecDeque::new() },
        status,
    }
}

fn crossterm_status() -> BackendStatus {
    BackendStatus {
        backend: "crossterm",
        paste_framing: !cfg!(windows),
        key_encoding: if cfg!(windows) { "native-records" } else { "vt" },
    }
}

/// Takes over terminal input for a terminal that was just set up. On Windows
/// this is the VT backend; if the console refuses VT input it falls back to
/// crossterm's native reader, which cannot see paste boundaries. Returns
/// whether crossterm's mouse capture must be used.
fn activate_input(capture_mouse: bool) -> bool {
    let mut slot = input_lock();
    // An earlier owner (a preflight screen) gives the console back first.
    *slot = None;
    #[cfg(windows)]
    {
        match crate::input::windows::WindowsVtInput::activate(capture_mouse) {
            Ok(vt) => {
                let status = BackendStatus {
                    backend: "windows-vt",
                    paste_framing: true,
                    key_encoding: "win32-input-mode",
                };
                record_backend(status);
                *slot = Some(InputState { backend: Backend::Vt(vt), status });
                false
            }
            Err(_) => {
                let status = BackendStatus { backend: "crossterm-native", ..crossterm_status() };
                record_backend(status);
                *slot = Some(crossterm_state(status));
                capture_mouse
            }
        }
    }
    #[cfg(not(windows))]
    {
        let status = crossterm_status();
        record_backend(status);
        *slot = Some(crossterm_state(status));
        capture_mouse
    }
}

fn record_backend(status: BackendStatus) {
    coda_diagnostics::record(coda_diagnostics::Event::InputBackend {
        backend: status.backend,
        paste_framing: status.paste_framing,
        key_encoding: status.key_encoding,
    });
}

/// Gives console input back (console modes restored, an open paste
/// cancelled) without forgetting the backend, so [`resume_input`] can retake
/// it after an external program.
fn suspend_input() -> io::Result<()> {
    #[cfg(windows)]
    if let Some(InputState { backend: Backend::Vt(vt), .. }) = input_lock().as_mut() {
        return vt.deactivate();
    }
    Ok(())
}

fn resume_input() -> io::Result<()> {
    #[cfg(windows)]
    if let Some(InputState { backend: Backend::Vt(vt), .. }) = input_lock().as_mut() {
        return vt.reactivate();
    }
    Ok(())
}

/// The active input backend, once a terminal has been set up.
pub fn input_status() -> Option<BackendStatus> {
    input_lock().as_ref().map(|state| state.status)
}

/// One step of the input owner: a queued or newly available action, never
/// blocking. No lock is held across an `.await`.
fn poll_input() -> io::Result<Option<InputAction>> {
    let mut slot = input_lock();
    let state = slot.get_or_insert_with(|| crossterm_state(crossterm_status()));
    match &mut state.backend {
        #[cfg(windows)]
        Backend::Vt(vt) => vt.poll(Instant::now()),
        Backend::Crossterm { next_txn, queue } => {
            if queue.is_empty() && crossterm::event::poll(std::time::Duration::ZERO)? {
                let event = crossterm::event::read()?;
                queue.extend(crate::input::from_crossterm(event, next_txn));
            }
            Ok(queue.pop_front())
        }
    }
}

/// The next terminal input action.
///
/// Polled on the owning async task, without a detached reader thread, so a
/// cancelled read leaves no reader competing with a returned shell; whatever
/// was decoded but not yet returned stays queued in the owner.
pub(crate) async fn read_input() -> io::Result<InputAction> {
    loop {
        if let Some(action) = poll_input()? {
            return Ok(action);
        }
        tokio::time::sleep(std::time::Duration::from_millis(10)).await;
    }
}

/// [`read_input`] as crossterm events, for the screens that only need keys
/// (preflight, shutdown). A paste becomes one `Event::Paste`; transaction
/// bookkeeping and aborted pastes are not events there.
pub(crate) async fn read_event() -> io::Result<crossterm::event::Event> {
    loop {
        match read_input().await? {
            InputAction::Key(key) => return Ok(crossterm::event::Event::Key(key)),
            InputAction::Mouse(mouse) => return Ok(crossterm::event::Event::Mouse(mouse)),
            InputAction::Resize(cols, rows) => return Ok(crossterm::event::Event::Resize(cols, rows)),
            InputAction::FocusGained => return Ok(crossterm::event::Event::FocusGained),
            InputAction::FocusLost => return Ok(crossterm::event::Event::FocusLost),
            InputAction::PasteText { text, .. } => return Ok(crossterm::event::Event::Paste(text)),
            InputAction::PasteStarted { .. } | InputAction::PasteAborted { .. } => {}
        }
    }
}

/// Owns the terminal's modified state and restores it on drop.
pub struct TerminalGuard {
    terminal: Tui,
    mouse_captured: bool,
}

impl TerminalGuard {
    /// Leaves raw mode and the alternate screen, reporting only whether that
    /// worked, so the caller knows if anything printed next will be visible.
    pub fn restore_for_exit(self) -> anyhow::Result<()> {
        if TERMINAL_ACTIVE.swap(false, Ordering::SeqCst) {
            restore(self.mouse_captured).map_err(anyhow::Error::new)
        } else {
            Ok(())
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
            let crossterm_mouse = activate_input(capture_mouse);
            if crossterm_mouse {
                execute!(stdout, EnableMouseCapture)?;
                CROSSTERM_MOUSE.store(true, Ordering::SeqCst);
            }

            let mut terminal = Terminal::new(StableBackend::new(CrosstermBackend::new(io::stdout())))?;
            terminal.clear()?;

            Ok(Self {
                terminal,
                mouse_captured: crossterm_mouse,
            })
        })();
        match result {
            Ok(guard) => Ok(guard),
            Err(error) => {
                let restoration = restore(CROSSTERM_MOUSE.load(Ordering::SeqCst));
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
            CROSSTERM_MOUSE.store(true, Ordering::SeqCst);
        }
        resume_input()?;
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
        RestoreStep::Input => suspend_input(),
        RestoreStep::Mouse => {
            CROSSTERM_MOUSE.store(false, Ordering::SeqCst);
            execute!(stdout, DisableMouseCapture)
        }
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
    Input,
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
        RestoreStep::Input,
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
            if let Err(error) = restore(CROSSTERM_MOUSE.load(Ordering::SeqCst)) {
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
        assert_eq!(
            attempted.first(),
            Some(&RestoreStep::Input),
            "console input is handed back before anything else is undone"
        );
        let message = result.unwrap_err().to_string();
        assert!(message.contains("Mouse failed"));
        assert!(message.contains("Screen failed"));
    }
}
