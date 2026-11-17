//! The Windows console input backend.
//!
//! crossterm's Windows reader turns console records straight into key events
//! and never sees a bracketed paste, because the console host only reports the
//! paste brackets to a client that asked for VT input. This backend owns the
//! console input handle instead: it enables VT input together with
//! win32-input-mode (`ESC[?9001h`, so keys keep their modifiers), reads the
//! raw `INPUT_RECORD`s, and hands them to the pure [`Decoder`].
//!
//! It is the only reader of console input while it is active, and the only
//! code that changes the input console mode: crossterm's mouse-capture command
//! is not used alongside it, because that command overwrites the whole mode
//! and would switch VT input back off.

use std::io::{self, Write};
use std::time::{Duration, Instant};

use windows_sys::Win32::Foundation::{CloseHandle, GENERIC_READ, GENERIC_WRITE, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::Storage::FileSystem::{CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING};
use windows_sys::Win32::System::Console::{
    GetConsoleMode, GetConsoleScreenBufferInfo, GetNumberOfConsoleInputEvents, GetStdHandle,
    ReadConsoleInputW, SetConsoleMode, CONSOLE_SCREEN_BUFFER_INFO, ENABLE_EXTENDED_FLAGS,
    ENABLE_MOUSE_INPUT, ENABLE_QUICK_EDIT_MODE, ENABLE_VIRTUAL_TERMINAL_INPUT, ENABLE_WINDOW_INPUT,
    FOCUS_EVENT, INPUT_RECORD, KEY_EVENT, MOUSE_EVENT, STD_OUTPUT_HANDLE, WINDOW_BUFFER_SIZE_EVENT,
};

use super::decoder::{Decoder, KeyRecord, MouseRecord, Record};
use super::InputAction;

/// Asks the console host to encode every key as a win32-input-mode sequence.
const WIN32_INPUT_MODE_ON: &[u8] = b"\x1b[?9001h";
const WIN32_INPUT_MODE_OFF: &[u8] = b"\x1b[?9001l";
/// Records read per call; the rest stay queued in the console.
const BATCH: usize = 128;
/// ConPTY does not always post a buffer-size record, so the window size is
/// also checked this often.
const SIZE_CHECK: Duration = Duration::from_millis(250);
/// The longest one poll keeps reading before handing control back.
const READ_BUDGET: Duration = Duration::from_millis(20);

pub struct WindowsVtInput {
    handle: HANDLE,
    saved_mode: u32,
    mouse: bool,
    active: bool,
    decoder: Decoder,
    size: Option<(u16, u16)>,
    size_checked: Option<Instant>,
}

// The handle is a process-wide console handle used under the terminal lock.
unsafe impl Send for WindowsVtInput {}

impl WindowsVtInput {
    /// Takes over console input. Fails, leaving the console as it was, when
    /// the console rejects VT input (legacy console mode).
    pub fn activate(mouse: bool) -> io::Result<Self> {
        let handle = open_console_input()?;
        let mut input = Self {
            handle,
            saved_mode: 0,
            mouse,
            active: false,
            decoder: Decoder::new(),
            size: crossterm::terminal::size().ok(),
            size_checked: None,
        };
        input.enable()?;
        Ok(input)
    }

    fn enable(&mut self) -> io::Result<()> {
        let mut saved = 0;
        // SAFETY: `handle` is an open console input handle owned by `self`.
        if unsafe { GetConsoleMode(self.handle, &mut saved) } == 0 {
            return Err(io::Error::last_os_error());
        }
        let mut mode = saved | ENABLE_EXTENDED_FLAGS | ENABLE_WINDOW_INPUT | ENABLE_VIRTUAL_TERMINAL_INPUT;
        if self.mouse {
            mode = (mode & !ENABLE_QUICK_EDIT_MODE) | ENABLE_MOUSE_INPUT;
        }
        // SAFETY: as above.
        let accepted = unsafe { SetConsoleMode(self.handle, mode) } != 0;
        let mut applied = 0;
        // SAFETY: as above.
        let read_back = unsafe { GetConsoleMode(self.handle, &mut applied) } != 0;
        if !accepted || !read_back || applied & ENABLE_VIRTUAL_TERMINAL_INPUT == 0 {
            // SAFETY: as above.
            unsafe { SetConsoleMode(self.handle, saved) };
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "the console does not accept virtual terminal input",
            ));
        }
        self.saved_mode = saved;
        self.active = true;
        let mut stdout = io::stdout();
        stdout.write_all(WIN32_INPUT_MODE_ON)?;
        stdout.flush()
    }

    /// Gives console input back: drops any half-received paste (reported as
    /// cancelled) and restores the console mode saved at activation.
    pub fn deactivate(&mut self) -> io::Result<()> {
        if !self.active {
            return Ok(());
        }
        self.active = false;
        self.decoder.cancel();
        let mut stdout = io::stdout();
        let written = stdout.write_all(WIN32_INPUT_MODE_OFF).and_then(|()| stdout.flush());
        // SAFETY: `handle` is an open console input handle owned by `self`.
        let restored = unsafe { SetConsoleMode(self.handle, self.saved_mode) } != 0;
        written?;
        if restored {
            Ok(())
        } else {
            Err(io::Error::last_os_error())
        }
    }

    /// Re-takes console input after [`deactivate`](Self::deactivate).
    pub fn reactivate(&mut self) -> io::Result<()> {
        if self.active {
            return Ok(());
        }
        self.enable()
    }

    /// The next decoded action, reading whatever the console has queued.
    /// Never blocks.
    ///
    /// Keeps reading while records are queued and nothing has been decoded
    /// yet, for at most [`READ_BUDGET`]: a paste produces no action until its
    /// end marker, and in win32-input-mode every pasted character arrives as
    /// a few dozen records, so reading one batch per poll would drain a large
    /// paste at a crawl.
    pub fn poll(&mut self, now: Instant) -> io::Result<Option<InputAction>> {
        if let Some(action) = self.decoder.next_action() {
            return Ok(Some(action));
        }
        if !self.active {
            return Ok(None);
        }
        let started = Instant::now();
        let top = window_top();
        while self.queued_records()? > 0 {
            // SAFETY: INPUT_RECORD is plain data; all-zero is a valid value.
            let mut records: [INPUT_RECORD; BATCH] = unsafe { std::mem::zeroed() };
            let mut read = 0;
            // SAFETY: the buffer holds BATCH records and `read` receives the count.
            if unsafe { ReadConsoleInputW(self.handle, records.as_mut_ptr(), BATCH as u32, &mut read) } == 0 {
                return Err(io::Error::last_os_error());
            }
            for record in &records[..read as usize] {
                if let Some(record) = self.convert(record, top) {
                    self.decoder.feed(record, now);
                }
            }
            if self.decoder.has_action() || started.elapsed() >= READ_BUDGET {
                break;
            }
        }
        self.decoder.tick(now);
        self.check_size(now);
        Ok(self.decoder.next_action())
    }

    /// Whether the console still holds unread records, so the caller should
    /// poll again at once rather than sleep.
    pub fn has_queued_input(&self) -> bool {
        self.active && self.queued_records().is_ok_and(|count| count > 0)
    }

    fn queued_records(&self) -> io::Result<u32> {
        let mut available = 0;
        // SAFETY: `handle` is an open console input handle owned by `self`.
        if unsafe { GetNumberOfConsoleInputEvents(self.handle, &mut available) } == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(available)
    }

    fn convert(&mut self, record: &INPUT_RECORD, top: i16) -> Option<Record> {
        match u32::from(record.EventType) {
            KEY_EVENT => {
                // SAFETY: EventType says the union holds a key record.
                let key = unsafe { record.Event.KeyEvent };
                Some(Record::Key(KeyRecord {
                    vk: key.wVirtualKeyCode,
                    scan: key.wVirtualScanCode,
                    // SAFETY: the console fills the UTF-16 member for W reads.
                    unit: unsafe { key.uChar.UnicodeChar },
                    down: key.bKeyDown != 0,
                    state: key.dwControlKeyState,
                    repeat: key.wRepeatCount,
                }))
            }
            MOUSE_EVENT => {
                // SAFETY: EventType says the union holds a mouse record.
                let mouse = unsafe { record.Event.MouseEvent };
                Some(Record::Mouse(MouseRecord {
                    column: mouse.dwMousePosition.X.max(0) as u16,
                    row: mouse.dwMousePosition.Y.saturating_sub(top).max(0) as u16,
                    buttons: mouse.dwButtonState,
                    flags: mouse.dwEventFlags,
                    state: mouse.dwControlKeyState,
                }))
            }
            WINDOW_BUFFER_SIZE_EVENT => {
                let (cols, rows) = crossterm::terminal::size().ok()?;
                self.size = Some((cols, rows));
                Some(Record::Resize { cols, rows })
            }
            FOCUS_EVENT => {
                // SAFETY: EventType says the union holds a focus record.
                let focus = unsafe { record.Event.FocusEvent };
                Some(Record::Focus(focus.bSetFocus != 0))
            }
            _ => None,
        }
    }

    fn check_size(&mut self, now: Instant) {
        if self.size_checked.is_some_and(|at| now.duration_since(at) < SIZE_CHECK) {
            return;
        }
        self.size_checked = Some(now);
        if let Ok(size) = crossterm::terminal::size() {
            if self.size != Some(size) {
                self.size = Some(size);
                self.decoder.feed(Record::Resize { cols: size.0, rows: size.1 }, now);
            }
        }
    }
}

impl Drop for WindowsVtInput {
    fn drop(&mut self) {
        let _ = self.deactivate();
        // SAFETY: the handle was opened by `open_console_input` and is closed once.
        unsafe { CloseHandle(self.handle) };
    }
}

fn open_console_input() -> io::Result<HANDLE> {
    let name: Vec<u16> = "CONIN$".encode_utf16().chain(Some(0)).collect();
    // SAFETY: `name` is a NUL-terminated UTF-16 string that outlives the call.
    let handle = unsafe {
        CreateFileW(
            name.as_ptr(),
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            std::ptr::null(),
            OPEN_EXISTING,
            0,
            std::ptr::null_mut(),
        )
    };
    if handle == INVALID_HANDLE_VALUE || handle.is_null() {
        Err(io::Error::last_os_error())
    } else {
        Ok(handle)
    }
}

/// The first buffer row shown in the window: native mouse rows are absolute
/// buffer rows.
fn window_top() -> i16 {
    // SAFETY: plain-data out parameter; failure leaves it zeroed.
    unsafe {
        let mut info: CONSOLE_SCREEN_BUFFER_INFO = std::mem::zeroed();
        if GetConsoleScreenBufferInfo(GetStdHandle(STD_OUTPUT_HANDLE), &mut info) == 0 {
            0
        } else {
            info.srWindow.Top
        }
    }
}
