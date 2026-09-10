//! Terminal input for host-local authentication, without a TUI.
//!
//! # Why this is not `crossterm`
//!
//! The engine binary must stay free of a terminal renderer (see
//! `crates/coda-engine/tests/independence.rs`), and both hosts need exactly
//! four things from a console: "is anyone there?", "is stdin itself a
//! terminal?", "read a line", and "read a secret without echoing it". That is
//! a console-mode toggle and an input loop, so it is written here against the
//! platform API directly rather than by linking a rendering stack into the
//! engine.
//!
//! # Cancelling has to actually work
//!
//! Turning echo off is not enough. On Windows, a console left with
//! `ENABLE_PROCESSED_INPUT` turns `Ctrl-C` into a *signal* whose default
//! action terminates the process — skipping every `Drop`, and leaving the
//! terminal with echo disabled. Reading through the ordinary byte stream also
//! never surfaces `Esc`. So the masked prompt:
//!
//! * clears `ENABLE_PROCESSED_INPUT` (and `ENABLE_VIRTUAL_TERMINAL_INPUT`,
//!   which would turn `Esc` into the first byte of a sequence), and reads
//!   **key events** with `ReadConsoleInputW`, where `Esc` is a virtual key and
//!   `Ctrl-C` is an ordinary `0x03` character;
//! * installs a console control handler *for the lifetime of the prompt only*,
//!   so a `Ctrl-Break`, a console close, a logoff or a shutdown — which are
//!   not ours to swallow — still put the console mode back before the default
//!   action runs.
//!
//! On unix the equivalent is clearing `ISIG` along with `ECHO`/`ICANON`, so
//! `Ctrl-C` arrives as a byte instead of `SIGINT`. No global signal handler is
//! installed there: the prompt runs on the calling thread with the terminal
//! restored by [`ModeGuard`], and a process-wide `SIGINT`/`SIGTERM` handler
//! would fight with the host's own (and with tokio's).
//!
//! # What this guarantees
//!
//! * **Nothing typed at a secret prompt is echoed.** Characters are replaced
//!   by `*` on the *prompt* stream (stderr), one per character rather than one
//!   per UTF-8 byte, and the collected bytes go straight into a
//!   [`coda_auth::Secret`] through the one validator in
//!   [`crate::secret_input`].
//! * **The terminal mode is always restored** — on success, on cancellation,
//!   on an unwind, and on an external console control event.
//! * **A terminal that cannot hide input says so**
//!   ([`PromptError::EchoUnavailable`]) rather than echoing the key.
//!   `mintty`/`msys` terminals report themselves as terminals but are pipes to
//!   Win32, and this is the case that catches them.
//!
//! # Blocking
//!
//! Every function here blocks the calling thread. They are called from the
//! host's *synchronous* planning stage, before the async runtime exists, so a
//! user standing at a prompt can never hold a runtime worker or delay a
//! shutdown.

use std::io::{IsTerminal, Write};

#[cfg(any(unix, test))]
use std::io::Read;

use coda_auth::Secret;

use crate::secret_input::{validate_api_key_bytes, SecretInputError, MAX_API_KEY_BYTES};

/// Why a prompt produced no value.
#[derive(Debug, thiserror::Error)]
pub enum PromptError {
    /// There is no terminal to prompt on.
    #[error("this prompt needs an interactive terminal")]
    NotInteractive,
    /// The terminal claims to be one but cannot suppress echo, so a secret
    /// typed into it would be visible.
    #[error("this terminal cannot hide typed input")]
    EchoUnavailable,
    /// The user pressed `Esc` or `Ctrl-C`.
    #[error("cancelled")]
    Cancelled,
    /// The input ended before an answer was given.
    #[error("the input ended before an answer was given")]
    EndOfInput,
    /// The value was read but is not usable. Never quotes the input.
    #[error("{0}")]
    Invalid(#[from] SecretInputError),
    /// The terminal itself could not be read or reconfigured.
    #[error("the terminal could not be read ({0:?})")]
    Io(std::io::ErrorKind),
}

impl PromptError {
    fn io(error: std::io::Error) -> Self {
        Self::Io(error.kind())
    }
}

/// Whether stdin itself is a terminal.
///
/// Distinct from [`is_interactive`] on purpose: whether a key may be read from
/// stdin *without echoing it* depends only on what stdin is, and a caller that
/// asked to read one from a redirected stdin must be refused when stdin turns
/// out to be the keyboard.
pub fn stdin_is_terminal() -> bool {
    std::io::stdin().is_terminal()
}

/// Whether this process can prompt: both the answer stream and the question
/// stream must be a terminal.
///
/// stderr, not stdout, carries the question — a host whose stdout is being
/// captured can still be prompted, and the report it prints stays machine
/// readable.
pub fn is_interactive() -> bool {
    stdin_is_terminal() && std::io::stderr().is_terminal()
}

/// Read one visible line from the terminal (a menu choice, never a secret).
pub fn read_line(prompt: &str) -> Result<String, PromptError> {
    if !is_interactive() {
        return Err(PromptError::NotInteractive);
    }
    let mut stderr = std::io::stderr();
    write!(stderr, "{prompt}").map_err(PromptError::io)?;
    stderr.flush().map_err(PromptError::io)?;

    let mut line = String::new();
    let read = std::io::stdin().read_line(&mut line).map_err(PromptError::io)?;
    if read == 0 {
        return Err(PromptError::EndOfInput);
    }
    Ok(line.trim().to_owned())
}

/// Read one secret line from the terminal with echo suppressed.
///
/// `Esc`/`Ctrl-C` cancel with [`PromptError::Cancelled`]. The console mode is
/// restored before this returns, whichever way it returns.
pub fn read_masked_line(prompt: &str) -> Result<Secret<String>, PromptError> {
    if !is_interactive() {
        return Err(PromptError::NotInteractive);
    }
    let mut stderr = std::io::stderr();
    write!(stderr, "{prompt}").map_err(PromptError::io)?;
    stderr.flush().map_err(PromptError::io)?;

    // Held for the whole read: dropping it restores the console mode even if
    // the loop below returns early or panics, and its control handler covers
    // the events that would otherwise skip the drop entirely.
    let _mode = match ModeGuard::suppress_echo() {
        Ok(guard) => guard,
        Err(error) => {
            let _ = writeln!(stderr);
            return Err(error);
        }
    };
    let result = platform::read_masked(&mut stderr);
    // The user's newline was consumed silently; end the prompt line ourselves
    // so whatever prints next starts in a sane place.
    let _ = writeln!(stderr);
    let _ = stderr.flush();
    result
}

/// The byte loop shared by the unix reader and the tests, separated from the
/// terminal so the accumulation rules can be exercised without one.
///
/// Windows reads key events instead (see [`platform::read_masked`]), so this
/// is compiled there only for the tests that pin the shared rules.
#[cfg(any(unix, test))]
fn read_masked_bytes(
    input: &mut impl Read,
    echo: &mut impl Write,
) -> Result<Secret<String>, PromptError> {
    let mut buffer = MaskedBuffer::default();
    let mut byte = [0u8; 1];
    loop {
        match input.read(&mut byte) {
            Ok(0) => return Err(PromptError::EndOfInput),
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(error) => return Err(PromptError::io(error)),
        }
        match buffer.feed(byte[0]) {
            Step::Cancelled => return Err(PromptError::Cancelled),
            Step::Done => return buffer.finish().map_err(PromptError::Invalid),
            Step::Continue(mark) => show(echo, mark),
        }
    }
}

fn show(echo: &mut impl Write, mark: Echo) {
    let _ = match mark {
        Echo::None => Ok(()),
        Echo::Mask => echo.write_all(b"*"),
        Echo::Erase => echo.write_all(b"\x08 \x08"),
    };
    let _ = echo.flush();
}

/// What the prompt should show for one accepted byte.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Echo {
    None,
    Mask,
    Erase,
}

/// What one byte means for the prompt.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Step {
    Continue(Echo),
    Done,
    Cancelled,
}

/// Accumulates a secret without ever echoing it.
///
/// Overlong input is remembered as an overflow rather than silently truncated:
/// half of a key is not a key, and storing one would produce a credential the
/// user never entered.
#[derive(Default)]
struct MaskedBuffer {
    bytes: Vec<u8>,
    overflow: bool,
}

impl std::fmt::Debug for MaskedBuffer {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("MaskedBuffer")
            .field("len", &self.bytes.len())
            .field("overflow", &self.overflow)
            .finish()
    }
}

const ETX: u8 = 0x03;
const EOT: u8 = 0x04;
const BACKSPACE: u8 = 0x08;
const ESCAPE: u8 = 0x1b;
const DELETE: u8 = 0x7f;

/// Whether `byte` continues a multi-byte UTF-8 character rather than starting
/// one.
fn is_continuation(byte: u8) -> bool {
    byte & 0b1100_0000 == 0b1000_0000
}

impl MaskedBuffer {
    fn feed(&mut self, byte: u8) -> Step {
        match byte {
            b'\r' | b'\n' | EOT => Step::Done,
            ETX | ESCAPE => Step::Cancelled,
            BACKSPACE | DELETE => {
                // Pop a whole character, not a byte: a UTF-8 continuation left
                // behind would make the key unreadable rather than shorter.
                let mut erased = false;
                while let Some(last) = self.bytes.pop() {
                    erased = true;
                    if !is_continuation(last) {
                        break;
                    }
                }
                Step::Continue(if erased { Echo::Erase } else { Echo::None })
            }
            _ => {
                if self.bytes.len() >= MAX_API_KEY_BYTES {
                    self.overflow = true;
                    return Step::Continue(Echo::None);
                }
                self.bytes.push(byte);
                // One mask per *character*: emitting one per byte while
                // erasing one per character leaves stale stars on screen after
                // a backspace over anything non-ASCII.
                Step::Continue(if is_continuation(byte) { Echo::None } else { Echo::Mask })
            }
        }
    }

    fn finish(self) -> Result<Secret<String>, SecretInputError> {
        if self.overflow {
            return Err(SecretInputError::TooLong);
        }
        validate_api_key_bytes(self.bytes)
    }
}

// ── Terminal mode ────────────────────────────────────────────────────────────

/// Restores the console mode this guard changed, on drop *and* on an external
/// console control event.
struct ModeGuard {
    _private: (),
}

impl ModeGuard {
    fn suppress_echo() -> Result<Self, PromptError> {
        platform::suppress_echo()?;
        Ok(Self { _private: () })
    }
}

impl Drop for ModeGuard {
    fn drop(&mut self) {
        platform::restore();
    }
}

#[cfg(windows)]
mod platform {
    use std::ffi::c_void;
    use std::io::Write;
    use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
    use std::sync::Mutex;

    use coda_auth::Secret;

    use super::{show, MaskedBuffer, PromptError, Step};

    const STD_INPUT_HANDLE: u32 = -10i32 as u32;
    const ENABLE_PROCESSED_INPUT: u32 = 0x0001;
    const ENABLE_LINE_INPUT: u32 = 0x0002;
    const ENABLE_ECHO_INPUT: u32 = 0x0004;
    const ENABLE_WINDOW_INPUT: u32 = 0x0008;
    const ENABLE_MOUSE_INPUT: u32 = 0x0010;
    const ENABLE_VIRTUAL_TERMINAL_INPUT: u32 = 0x0200;
    const INVALID_HANDLE_VALUE: *mut c_void = -1isize as *mut c_void;
    const KEY_EVENT: u16 = 0x0001;
    const VK_ESCAPE: u16 = 0x1b;

    /// The flags a masked prompt must clear. Named once so the reader and the
    /// test that pins the reason agree.
    const QUIET_MASK: u32 = ENABLE_ECHO_INPUT
        | ENABLE_LINE_INPUT
        | ENABLE_PROCESSED_INPUT
        | ENABLE_MOUSE_INPUT
        | ENABLE_WINDOW_INPUT
        | ENABLE_VIRTUAL_TERMINAL_INPUT;

    /// `KEY_EVENT_RECORD`. The `uChar` union is two bytes wide, and this
    /// record is the widest member of the `INPUT_RECORD` union, so the
    /// 20-byte total is asserted rather than assumed.
    #[repr(C)]
    #[derive(Clone, Copy)]
    struct KeyEventRecord {
        key_down: i32,
        repeat_count: u16,
        virtual_key_code: u16,
        virtual_scan_code: u16,
        unicode_char: u16,
        control_key_state: u32,
    }

    #[repr(C)]
    #[derive(Clone, Copy)]
    struct InputRecord {
        event_type: u16,
        // `repr(C)` inserts the two bytes of padding the real union needs.
        event: KeyEventRecord,
    }

    #[link(name = "kernel32")]
    extern "system" {
        fn GetStdHandle(which: u32) -> *mut c_void;
        fn GetConsoleMode(handle: *mut c_void, mode: *mut u32) -> i32;
        fn SetConsoleMode(handle: *mut c_void, mode: u32) -> i32;
        fn ReadConsoleInputW(
            handle: *mut c_void,
            buffer: *mut InputRecord,
            length: u32,
            read: *mut u32,
        ) -> i32;
        fn SetConsoleCtrlHandler(
            handler: Option<unsafe extern "system" fn(u32) -> i32>,
            add: i32,
        ) -> i32;
    }

    /// The mode to put back, readable from the control handler — which runs on
    /// a thread the OS creates, so it cannot be handed a borrow.
    static SAVED_MODE: AtomicU32 = AtomicU32::new(0);
    static MODE_SAVED: AtomicBool = AtomicBool::new(false);
    // Win32 control handlers run on another thread. Serialize applying quiet
    // mode with restoration so an interrupt cannot restore first, then lose
    // to an in-flight SetConsoleMode.
    static MODE_CHANGE: Mutex<()> = Mutex::new(());

    fn stdin_handle() -> Option<*mut c_void> {
        let handle = unsafe { GetStdHandle(STD_INPUT_HANDLE) };
        (!handle.is_null() && handle != INVALID_HANDLE_VALUE).then_some(handle)
    }

    /// Restores the console before the default action for a control event
    /// runs, then declines to handle it.
    ///
    /// `Ctrl-C` does not arrive here while a prompt is running — the prompt
    /// clears `ENABLE_PROCESSED_INPUT`, so it is an ordinary keystroke — but a
    /// `Ctrl-Break`, a closed console window, a logoff or a shutdown still
    /// terminate this process without unwinding. Returning `FALSE` keeps the
    /// default behaviour, so this handler changes *when* the terminal is put
    /// back and nothing else.
    unsafe extern "system" fn control_handler(_event: u32) -> i32 {
        restore();
        0
    }

    pub fn suppress_echo() -> Result<(), PromptError> {
        let _change = MODE_CHANGE.lock().map_err(|_| PromptError::EchoUnavailable)?;
        let Some(handle) = stdin_handle() else {
            return Err(PromptError::EchoUnavailable);
        };
        // One prompt at a time. A nested acquisition would save the *quiet*
        // mode as the one to restore, and the terminal would be left with echo
        // off after both prompts finished.
        if MODE_SAVED.load(Ordering::SeqCst) {
            return Err(PromptError::EchoUnavailable);
        }
        let mut mode: u32 = 0;
        // A real console handle is required. `mintty`/`msys` terminals report
        // themselves as terminals but are pipes to Win32, and fail here rather
        // than echoing a secret.
        if unsafe { GetConsoleMode(handle, &mut mode) } == 0 {
            return Err(PromptError::EchoUnavailable);
        }
        SAVED_MODE.store(mode, Ordering::SeqCst);
        MODE_SAVED.store(true, Ordering::SeqCst);
        if unsafe { SetConsoleCtrlHandler(Some(control_handler), 1) } == 0 {
            MODE_SAVED.store(false, Ordering::SeqCst);
            return Err(PromptError::EchoUnavailable);
        }
        if unsafe { SetConsoleMode(handle, mode & !QUIET_MASK) } == 0 {
            restore_locked();
            return Err(PromptError::EchoUnavailable);
        }
        Ok(())
    }

    pub fn restore() {
        // Even if a prompt panicked, its saved mode must still be restored.
        let _change = MODE_CHANGE.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        restore_locked();
    }

    fn restore_locked() {
        // Claim the restoration once: the guard's drop and the control handler
        // may both run, and the second must not write a mode nobody saved.
        if !MODE_SAVED.swap(false, Ordering::SeqCst) {
            return;
        }
        unsafe { SetConsoleCtrlHandler(Some(control_handler), 0) };
        if let Some(handle) = stdin_handle() {
            unsafe { SetConsoleMode(handle, SAVED_MODE.load(Ordering::SeqCst)) };
        }
    }

    /// Read key events until the prompt is answered or cancelled.
    ///
    /// Key *events* rather than bytes: reading through the ordinary stdin
    /// stream never surfaces `Esc` on this platform at all.
    pub fn read_masked(echo: &mut impl Write) -> Result<Secret<String>, PromptError> {
        const _: () = assert!(std::mem::size_of::<InputRecord>() == 20);

        let Some(handle) = stdin_handle() else {
            return Err(PromptError::EchoUnavailable);
        };
        let mut buffer = MaskedBuffer::default();
        // A character outside the basic plane arrives as two UTF-16 units.
        let mut pending_high_surrogate: Option<u16> = None;

        loop {
            let mut record = InputRecord {
                event_type: 0,
                event: KeyEventRecord {
                    key_down: 0,
                    repeat_count: 0,
                    virtual_key_code: 0,
                    virtual_scan_code: 0,
                    unicode_char: 0,
                    control_key_state: 0,
                },
            };
            let mut read: u32 = 0;
            if unsafe { ReadConsoleInputW(handle, &mut record, 1, &mut read) } == 0 {
                return Err(PromptError::Io(std::io::Error::last_os_error().kind()));
            }
            if read == 0 {
                return Err(PromptError::EndOfInput);
            }
            if record.event_type != KEY_EVENT || record.event.key_down == 0 {
                continue;
            }

            let key = record.event;
            if key.virtual_key_code == VK_ESCAPE {
                return Err(PromptError::Cancelled);
            }
            let unit = key.unicode_char;
            if unit == 0 {
                // A modifier, an arrow, a function key: nothing to collect.
                continue;
            }

            // Held keys repeat; each repeat is a character the user typed.
            for _ in 0..key.repeat_count.max(1) {
                let text = match (pending_high_surrogate.take(), unit) {
                    (None, 0xD800..=0xDBFF) => {
                        pending_high_surrogate = Some(unit);
                        continue;
                    }
                    (Some(high), 0xDC00..=0xDFFF) => String::from_utf16_lossy(&[high, unit]),
                    // A lone surrogate is dropped rather than turned into a
                    // replacement character inside a secret.
                    (_, 0xD800..=0xDFFF) => continue,
                    (_, _) => String::from_utf16_lossy(&[unit]),
                };
                for byte in text.as_bytes() {
                    match buffer.feed(*byte) {
                        Step::Cancelled => return Err(PromptError::Cancelled),
                        Step::Done => return buffer.finish().map_err(PromptError::Invalid),
                        Step::Continue(mark) => show(echo, mark),
                    }
                }
            }
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        /// The record this module hands the OS must be laid out the way the OS
        /// expects, or every field read from it is garbage.
        #[test]
        fn the_input_record_matches_the_win32_layout() {
            assert_eq!(std::mem::size_of::<InputRecord>(), 20);
            assert_eq!(std::mem::size_of::<KeyEventRecord>(), 16);
            assert_eq!(std::mem::align_of::<InputRecord>(), 4);
        }

        /// The flags that make cancellation possible are the point of this
        /// module: `Ctrl-C` must stay a keystroke, `Esc` must stay one key
        /// event, and flags this prompt has no business changing must survive.
        #[test]
        fn the_quiet_mode_clears_every_flag_that_would_swallow_a_cancellation() {
            const ENABLE_EXTENDED_FLAGS: u32 = 0x0080;
            let before = ENABLE_PROCESSED_INPUT
                | ENABLE_LINE_INPUT
                | ENABLE_ECHO_INPUT
                | ENABLE_MOUSE_INPUT
                | ENABLE_WINDOW_INPUT
                | ENABLE_VIRTUAL_TERMINAL_INPUT
                | ENABLE_EXTENDED_FLAGS;
            let quiet = before & !QUIET_MASK;
            assert_eq!(quiet & ENABLE_PROCESSED_INPUT, 0, "Ctrl-C must not become a signal");
            assert_eq!(quiet & ENABLE_ECHO_INPUT, 0);
            assert_eq!(quiet & ENABLE_LINE_INPUT, 0);
            assert_eq!(quiet & ENABLE_VIRTUAL_TERMINAL_INPUT, 0, "Esc must stay one key event");
            assert_eq!(quiet & ENABLE_EXTENDED_FLAGS, ENABLE_EXTENDED_FLAGS);
        }
    }
}

#[cfg(unix)]
mod platform {
    use std::io::Write;

    use coda_auth::Secret;

    use super::PromptError;

    static SAVED: std::sync::Mutex<Option<libc::termios>> = std::sync::Mutex::new(None);

    pub fn suppress_echo() -> Result<(), PromptError> {
        // One prompt at a time; see the Windows note.
        if SAVED.lock().expect("terminal mode").is_some() {
            return Err(PromptError::EchoUnavailable);
        }
        let mut mode: libc::termios = unsafe { std::mem::zeroed() };
        if unsafe { libc::tcgetattr(libc::STDIN_FILENO, &mut mode) } != 0 {
            return Err(PromptError::EchoUnavailable);
        }
        let mut quiet = mode;
        // ISIG alongside ECHO/ICANON: without clearing it, Ctrl-C is SIGINT,
        // whose default action terminates this process before the terminal is
        // put back — the same failure the Windows path avoids by clearing
        // ENABLE_PROCESSED_INPUT.
        quiet.c_lflag &= !(libc::ECHO | libc::ICANON | libc::ISIG);
        // One byte at a time, no inter-byte timer: the prompt reacts to `Esc`
        // as soon as it is pressed.
        quiet.c_cc[libc::VMIN] = 1;
        quiet.c_cc[libc::VTIME] = 0;
        if unsafe { libc::tcsetattr(libc::STDIN_FILENO, libc::TCSANOW, &quiet) } != 0 {
            return Err(PromptError::EchoUnavailable);
        }
        *SAVED.lock().expect("terminal mode") = Some(mode);
        Ok(())
    }

    pub fn restore() {
        let previous = SAVED.lock().expect("terminal mode").take();
        if let Some(previous) = previous {
            unsafe { libc::tcsetattr(libc::STDIN_FILENO, libc::TCSANOW, &previous) };
        }
    }

    /// With `ICANON`/`ISIG` cleared, every key — including `Esc` and `Ctrl-C`
    /// — arrives as an ordinary byte, so the shared loop is the whole reader.
    pub fn read_masked(echo: &mut impl Write) -> Result<Secret<String>, PromptError> {
        super::read_masked_bytes(&mut std::io::stdin().lock(), echo)
    }
}

#[cfg(not(any(windows, unix)))]
mod platform {
    use std::io::Write;

    use coda_auth::Secret;

    use super::PromptError;

    pub fn suppress_echo() -> Result<(), PromptError> {
        Err(PromptError::EchoUnavailable)
    }

    pub fn restore() {}

    pub fn read_masked(_echo: &mut impl Write) -> Result<Secret<String>, PromptError> {
        Err(PromptError::EchoUnavailable)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn masked(input: &[u8]) -> (Result<Secret<String>, PromptError>, String) {
        let mut echo = Vec::new();
        let result = read_masked_bytes(&mut std::io::Cursor::new(input.to_vec()), &mut echo);
        (result, String::from_utf8_lossy(&echo).into_owned())
    }

    #[test]
    fn a_typed_key_is_collected_but_only_masks_are_shown() {
        let (result, echo) = masked(b"sk-typed-key\r");
        assert_eq!(result.expect("a key").expose(), "sk-typed-key");
        assert!(!echo.contains("sk-typed-key"), "{echo:?}");
        assert_eq!(echo, "*".repeat("sk-typed-key".len()));
    }

    #[test]
    fn escape_and_ctrl_c_cancel_rather_than_returning_an_empty_key() {
        for input in [b"partial\x1b".as_slice(), b"partial\x03"] {
            let (result, echo) = masked(input);
            assert!(matches!(result, Err(PromptError::Cancelled)), "{input:?}");
            assert!(!echo.contains("partial"));
        }
    }

    #[test]
    fn input_that_simply_ends_is_not_a_cancellation_and_is_not_a_key() {
        let (result, _) = masked(b"unterminated");
        assert!(matches!(result, Err(PromptError::EndOfInput)));
    }

    #[test]
    fn a_blank_answer_is_refused_instead_of_stored() {
        for input in [b"\r".as_slice(), b"   \n"] {
            let (result, _) = masked(input);
            assert!(
                matches!(result, Err(PromptError::Invalid(SecretInputError::Empty))),
                "{input:?}"
            );
        }
    }

    #[test]
    fn one_mask_is_shown_per_character_not_per_byte() {
        // Three characters, four bytes: a prompt that masked per byte would
        // leave a star behind when the accented character is erased.
        let (result, echo) = masked("kéy\r".as_bytes());
        assert_eq!(result.expect("a key").expose(), "kéy");
        assert_eq!(echo, "***");
    }

    #[test]
    fn backspace_erases_a_whole_character_and_leaves_no_stale_mask() {
        let (result, echo) = masked("keyé\x7f\x08!\r".as_bytes());
        assert_eq!(result.expect("a key").expose(), "ke!");
        // Four characters masked, two erased, one more masked: the erases must
        // match the characters removed and the masks the characters kept.
        assert_eq!(echo.matches("\x08 \x08").count(), 2);
        assert_eq!(echo.matches('*').count(), 5);

        let (result, _) = masked(b"\x08\x08x\r");
        assert_eq!(result.expect("a key").expose(), "x");
    }

    #[test]
    fn an_overlong_answer_is_an_error_rather_than_a_truncated_key() {
        let mut input = vec![b'x'; MAX_API_KEY_BYTES + 5];
        input.push(b'\r');
        let (result, _) = masked(&input);
        assert!(matches!(result, Err(PromptError::Invalid(SecretInputError::TooLong))));
    }

    #[test]
    fn the_buffer_never_debugs_its_contents() {
        let mut buffer = MaskedBuffer::default();
        for byte in b"sk-private-sentinel" {
            buffer.feed(*byte);
        }
        let debug = format!("{buffer:?}");
        assert!(!debug.contains("sk-private-sentinel"), "{debug}");
    }
}
