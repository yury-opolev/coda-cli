//! A real console host between a terminal and Coda's Windows input backend.
//!
//! The decoder's unit tests construct console records by hand. This test
//! instead starts a pseudo console (ConPTY, the console host Windows Terminal
//! and VS Code use), runs Coda's input backend inside it, and writes exactly
//! what Windows Terminal writes for typing and for a paste: win32-input-mode
//! key sequences and a raw bracketed paste. What the backend reports is what
//! the application would receive.
//!
//! The same test binary is both sides: run normally it is the terminal; run
//! with `CODA_CONPTY_CHILD_OUT` set it is the application under the console.
#![cfg(windows)]

use std::io::Write;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use coda_tui::input::windows::WindowsVtInput;
use coda_tui::input::InputAction;
use crossterm::event::{KeyCode, KeyModifiers};
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE};
use windows_sys::Win32::Storage::FileSystem::{
    CreateFileW, ReadFile, WriteFile, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
};
use windows_sys::Win32::System::Console::{
    ClosePseudoConsole, CreatePseudoConsole, SetStdHandle, COORD, HPCON, STD_INPUT_HANDLE,
    STD_OUTPUT_HANDLE,
};
use windows_sys::Win32::System::Pipes::CreatePipe;
use windows_sys::Win32::System::Threading::{
    CreateProcessW, InitializeProcThreadAttributeList, UpdateProcThreadAttribute,
    WaitForSingleObject, EXTENDED_STARTUPINFO_PRESENT, LPPROC_THREAD_ATTRIBUTE_LIST,
    PROCESS_INFORMATION, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, STARTUPINFOEXW,
};

const CHILD_ENV: &str = "CODA_CONPTY_CHILD_OUT";

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

fn console(name: &str) -> HANDLE {
    let name = wide(name);
    // SAFETY: NUL-terminated name that outlives the call.
    unsafe {
        CreateFileW(
            name.as_ptr(),
            0x8000_0000 | 0x4000_0000,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            std::ptr::null(),
            OPEN_EXISTING,
            0,
            std::ptr::null_mut(),
        )
    }
}

/// The application side. Does nothing unless started by the terminal side.
#[test]
fn conpty_child() {
    let Ok(out) = std::env::var(CHILD_ENV) else { return };
    // The test harness's own handles are pipes to the parent; the backend
    // must talk to the pseudo console, as the real application does.
    // SAFETY: valid handles from CreateFileW.
    unsafe {
        SetStdHandle(STD_INPUT_HANDLE, console("CONIN$"));
        SetStdHandle(STD_OUTPUT_HANDLE, console("CONOUT$"));
    }
    let mut input = WindowsVtInput::activate(true).expect("the console accepts VT input");
    let mut stdout = std::io::stdout();
    write!(stdout, "\x1b[?2004hCODA-READY\r\n").unwrap();
    stdout.flush().unwrap();

    let mut seen = Vec::new();
    let deadline = Instant::now() + Duration::from_secs(10);
    while Instant::now() < deadline {
        match input.poll(Instant::now()).expect("console input") {
            Some(InputAction::Key(key)) if key.code == KeyCode::F(12) => break,
            Some(action) => seen.push(format!("{action:?}")),
            None => std::thread::sleep(Duration::from_millis(5)),
        }
    }
    input.deactivate().expect("console mode restored");
    std::fs::write(out, seen.join("\n")).unwrap();
}

/// The terminal side: what Windows Terminal sends for one keystroke, one
/// multi-line paste and one Shift+Enter.
#[test]
fn a_terminal_paste_through_a_real_console_host_is_one_transaction() {
    if std::env::var(CHILD_ENV).is_ok() {
        return;
    }
    let dir = tempfile::tempdir().unwrap();
    let out = dir.path().join("actions.txt");

    // SAFETY: plain Win32 calls with owned, valid out-parameters; every
    // handle is closed or handed to the pseudo console.
    unsafe {
        let (mut in_read, mut in_write): (HANDLE, HANDLE) = (std::ptr::null_mut(), std::ptr::null_mut());
        let (mut out_read, mut out_write): (HANDLE, HANDLE) = (std::ptr::null_mut(), std::ptr::null_mut());
        assert_ne!(CreatePipe(&mut in_read, &mut in_write, std::ptr::null(), 0), 0);
        assert_ne!(CreatePipe(&mut out_read, &mut out_write, std::ptr::null(), 0), 0);
        let mut console: HPCON = 0;
        assert_eq!(CreatePseudoConsole(COORD { X: 100, Y: 30 }, in_read, out_write, 0, &mut console), 0);

        let mut size = 0usize;
        InitializeProcThreadAttributeList(std::ptr::null_mut(), 1, 0, &mut size);
        let mut attributes = vec![0u8; size];
        let list = attributes.as_mut_ptr() as LPPROC_THREAD_ATTRIBUTE_LIST;
        assert_ne!(InitializeProcThreadAttributeList(list, 1, 0, &mut size), 0);
        assert_ne!(
            UpdateProcThreadAttribute(
                list,
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE as usize,
                console as *const _,
                std::mem::size_of::<HPCON>(),
                std::ptr::null_mut(),
                std::ptr::null(),
            ),
            0
        );
        let mut startup: STARTUPINFOEXW = std::mem::zeroed();
        startup.StartupInfo.cb = std::mem::size_of::<STARTUPINFOEXW>() as u32;
        startup.lpAttributeList = list;
        let exe = std::env::current_exe().unwrap();
        let mut command = wide(&format!(
            "\"{}\" conpty_child --exact --nocapture --test-threads=1",
            exe.display()
        ));
        let mut environment: Vec<u16> = std::env::vars()
            .filter(|(key, _)| key != CHILD_ENV)
            .chain(Some((CHILD_ENV.to_string(), out.display().to_string())))
            .flat_map(|(key, value)| format!("{key}={value}").encode_utf16().chain(Some(0)).collect::<Vec<_>>())
            .collect();
        environment.push(0);
        let mut process: PROCESS_INFORMATION = std::mem::zeroed();
        assert_ne!(
            CreateProcessW(
                std::ptr::null(),
                command.as_mut_ptr(),
                std::ptr::null(),
                std::ptr::null(),
                0,
                EXTENDED_STARTUPINFO_PRESENT | 0x0000_0400, // CREATE_UNICODE_ENVIRONMENT
                environment.as_ptr() as *const _,
                std::ptr::null(),
                &startup.StartupInfo,
                &mut process,
            ),
            0,
            "the child could not be started"
        );

        let output = Arc::new(Mutex::new(Vec::<u8>::new()));
        let sink = Arc::clone(&output);
        let reader = out_read as usize;
        std::thread::spawn(move || {
            let mut buffer = [0u8; 4096];
            loop {
                let mut count = 0u32;
                if ReadFile(reader as HANDLE, buffer.as_mut_ptr(), buffer.len() as u32, &mut count, std::ptr::null_mut()) == 0
                    || count == 0
                {
                    break;
                }
                sink.lock().unwrap().extend_from_slice(&buffer[..count as usize]);
            }
        });

        let ready = Instant::now() + Duration::from_secs(20);
        loop {
            let seen = String::from_utf8_lossy(&output.lock().unwrap()).to_string();
            if seen.contains("CODA-READY") {
                assert!(seen.contains("\x1b[?9001h"), "win32-input-mode was not requested: {seen:?}");
                break;
            }
            assert!(Instant::now() < ready, "the child never became ready: {seen:?}");
            std::thread::sleep(Duration::from_millis(20));
        }

        let send = |bytes: &[u8]| {
            let mut written = 0u32;
            WriteFile(in_write, bytes.as_ptr(), bytes.len() as u32, &mut written, std::ptr::null_mut());
            std::thread::sleep(Duration::from_millis(150));
        };
        // A typed "x", as Windows Terminal encodes keys for the console host.
        send(b"\x1b[88;45;120;1;0;1_\x1b[88;45;120;0;0;1_");
        // A paste: raw text inside the brackets, split across two writes.
        send(b"\x1b[200~First line\r\nSecond li");
        send(b"ne\r\n\r\nFourth line\r\n\x1b[201~");
        // Shift+Enter, then F12 to end the child.
        send(b"\x1b[13;28;13;1;16;1_\x1b[13;28;13;0;16;1_");
        send(b"\x1b[123;88;0;1;0;1_\x1b[123;88;0;0;0;1_");

        assert_eq!(WaitForSingleObject(process.hProcess, 20_000), 0, "the child did not finish");
        ClosePseudoConsole(console);
        CloseHandle(process.hProcess);
        CloseHandle(process.hThread);
        CloseHandle(in_write);
    }

    let actions = std::fs::read_to_string(&out).expect("the child reported its actions");
    let x = format!("{:?}", InputAction::Key(crossterm::event::KeyEvent::new(KeyCode::Char('x'), KeyModifiers::NONE)));
    let shift_enter = format!("{:?}", InputAction::Key(crossterm::event::KeyEvent::new(KeyCode::Enter, KeyModifiers::SHIFT)));
    let paste = format!(
        "{:?}",
        InputAction::PasteText {
            text: "First line\nSecond line\n\nFourth line\n".into(),
            source: coda_tui::input::PasteSource::Terminal,
            txn: 1,
        }
    );
    let lines: Vec<&str> = actions.lines().filter(|line| !line.starts_with("Resize")).collect();
    assert_eq!(
        lines,
        [x.as_str(), "PasteStarted { txn: 1 }", paste.as_str(), shift_enter.as_str()],
        "what the application received"
    );
}
