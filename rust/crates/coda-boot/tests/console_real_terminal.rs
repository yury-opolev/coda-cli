//! Real-console regression for the masked API-key prompt.
//!
//! # Why this is not a byte-loop test
//!
//! Every interesting property of [`coda_boot::console`] belongs to the
//! *operating system*, not to a byte loop: whether `Ctrl-C` reaches the prompt
//! as a keystroke or as a signal whose default action kills the process before
//! anything is restored, whether `Esc` survives the console's own processing,
//! and whether the mode the prompt changed is actually put back. Feeding bytes
//! to an in-memory reader proves none of them.
//!
//! So this allocates a **real console** — a private one, in a child process,
//! so the developer's terminal is never touched — points the child's stdin and
//! stderr at it, injects real key events with `WriteConsoleInputW`, and
//! measures the console mode before, during and after the prompt.
//!
//! # What the measurements mean
//!
//! * `MODE_DURING` is asserted to have `ENABLE_PROCESSED_INPUT` clear. That is
//!   the flag which decides whether a *keyboard* `Ctrl-C` becomes a keystroke
//!   or a `CTRL_C_EVENT` whose default action terminates the process — the
//!   failure this test exists to prevent. It is also asserted to have echo,
//!   line input and virtual-terminal input clear.
//! * `MODE_AFTER == MODE_BEFORE` is the restoration guarantee, measured the
//!   same way an outside observer would.
//! * The injected `Esc` and `Ctrl-C` prove the reader acts on them. Injection
//!   deliberately bypasses the console's own `Ctrl-C` processing, which is why
//!   the `ENABLE_PROCESSED_INPUT` assertion above is the other half of the
//!   claim rather than an aside.

#![cfg(windows)]

use std::process::{Command, Stdio};

/// The environment variable that turns this test binary into the child that
/// owns a console. Re-executing the harness avoids adding a binary target to a
/// library crate just to have something to run.
const SCENARIO_ENV: &str = "CODA_CONSOLE_SCENARIO";

/// Run the child scenario and return the `KEY=VALUE` report it printed.
fn run_scenario(scenario: &str) -> std::collections::HashMap<String, String> {
    let exe = std::env::current_exe().expect("the test binary");
    let output = Command::new(exe)
        .args(["--exact", "console_child::the_child_drives_a_real_console", "--ignored", "--nocapture"])
        .env(SCENARIO_ENV, scenario)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .expect("the child test process runs");

    let stdout = String::from_utf8_lossy(&output.stdout).into_owned();
    let report: std::collections::HashMap<String, String> = stdout
        .lines()
        .filter_map(|line| line.strip_prefix("CONSOLE "))
        .filter_map(|line| line.split_once('='))
        .map(|(key, value)| (key.to_owned(), value.trim().to_owned()))
        .collect();
    assert!(
        !report.is_empty(),
        "the child produced no measurement for {scenario}:\nstdout: {stdout}\nstderr: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    report
}

fn mode(report: &std::collections::HashMap<String, String>, key: &str) -> u32 {
    report
        .get(key)
        .unwrap_or_else(|| panic!("{key} missing from {report:?}"))
        .parse()
        .unwrap_or_else(|_| panic!("{key} is not a number in {report:?}"))
}

const ENABLE_PROCESSED_INPUT: u32 = 0x0001;
const ENABLE_LINE_INPUT: u32 = 0x0002;
const ENABLE_ECHO_INPUT: u32 = 0x0004;
const ENABLE_VIRTUAL_TERMINAL_INPUT: u32 = 0x0200;

/// While the prompt is running, the console must be in a mode where a real
/// `Ctrl-C` is a keystroke and `Esc` is one key event — and echo is off.
fn assert_quiet_while_prompting(report: &std::collections::HashMap<String, String>) {
    let during = mode(report, "MODE_DURING");
    assert_eq!(
        during & ENABLE_PROCESSED_INPUT,
        0,
        "Ctrl-C would still be a signal that terminates the process: {report:?}"
    );
    assert_eq!(during & ENABLE_ECHO_INPUT, 0, "typed input would be echoed: {report:?}");
    assert_eq!(during & ENABLE_LINE_INPUT, 0, "keys would not reach the prompt: {report:?}");
    assert_eq!(
        during & ENABLE_VIRTUAL_TERMINAL_INPUT,
        0,
        "Esc would arrive as the start of a sequence: {report:?}"
    );
}

/// The console this prompt changed must be exactly as it was afterwards.
fn assert_restored(report: &std::collections::HashMap<String, String>) {
    assert_eq!(
        mode(report, "MODE_AFTER"),
        mode(report, "MODE_BEFORE"),
        "the console mode was not restored: {report:?}"
    );
}

#[test]
fn escape_cancels_the_masked_prompt_and_restores_the_console() {
    let report = run_scenario("escape");
    assert_eq!(report.get("RESULT").map(String::as_str), Some("cancelled"), "{report:?}");
    assert_quiet_while_prompting(&report);
    assert_restored(&report);
}

#[test]
fn ctrl_c_cancels_the_masked_prompt_and_restores_the_console() {
    let report = run_scenario("ctrl_c");
    assert_eq!(report.get("RESULT").map(String::as_str), Some("cancelled"), "{report:?}");
    assert_quiet_while_prompting(&report);
    assert_restored(&report);
}

#[test]
fn a_typed_key_is_collected_from_a_real_console_and_the_mode_is_restored() {
    let report = run_scenario("typed");
    assert_eq!(report.get("RESULT").map(String::as_str), Some("key"), "{report:?}");
    assert_eq!(report.get("LEN").map(String::as_str), Some("12"), "{report:?}");
    assert_quiet_while_prompting(&report);
    assert_restored(&report);
    // The key itself must never appear in anything the child wrote.
    assert!(!report.values().any(|value| value.contains("sk-real")), "{report:?}");
}

#[test]
fn an_empty_answer_from_a_real_console_is_refused_and_the_mode_is_restored() {
    let report = run_scenario("empty");
    assert_eq!(report.get("RESULT").map(String::as_str), Some("invalid"), "{report:?}");
    assert_restored(&report);
}

// ── The child ────────────────────────────────────────────────────────────────

/// Runs in a child process with its own console. Ignored by default; the tests
/// above start it explicitly.
mod console_child {
    use std::ffi::c_void;

    use super::SCENARIO_ENV;

    type Handle = *mut c_void;

    const STD_INPUT_HANDLE: u32 = -10i32 as u32;
    const STD_ERROR_HANDLE: u32 = -12i32 as u32;
    const GENERIC_READ: u32 = 0x8000_0000;
    const GENERIC_WRITE: u32 = 0x4000_0000;
    const FILE_SHARE_READ: u32 = 0x0000_0001;
    const FILE_SHARE_WRITE: u32 = 0x0000_0002;
    const OPEN_EXISTING: u32 = 3;
    const INVALID_HANDLE_VALUE: Handle = -1isize as Handle;
    const KEY_EVENT: u16 = 0x0001;
    const VK_ESCAPE: u16 = 0x1b;
    const LEFT_CTRL_PRESSED: u32 = 0x0008;

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
        event: KeyEventRecord,
    }

    #[link(name = "kernel32")]
    extern "system" {
        fn FreeConsole() -> i32;
        fn AllocConsole() -> i32;
        fn CreateFileW(
            name: *const u16,
            access: u32,
            share: u32,
            security: *mut c_void,
            disposition: u32,
            flags: u32,
            template: Handle,
        ) -> Handle;
        fn SetStdHandle(which: u32, handle: Handle) -> i32;
        fn GetConsoleMode(handle: Handle, mode: *mut u32) -> i32;
        fn WriteConsoleInputW(
            handle: Handle,
            records: *const InputRecord,
            count: u32,
            written: *mut u32,
        ) -> i32;
    }

    fn wide(value: &str) -> Vec<u16> {
        value.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn open(name: &str, access: u32) -> Handle {
        let name = wide(name);
        let handle = unsafe {
            CreateFileW(
                name.as_ptr(),
                access,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                std::ptr::null_mut(),
                OPEN_EXISTING,
                0,
                std::ptr::null_mut(),
            )
        };
        assert!(handle != INVALID_HANDLE_VALUE, "opening {name:?} failed");
        handle
    }

    fn key(unicode: u16, virtual_key: u16, control: u32) -> InputRecord {
        InputRecord {
            event_type: KEY_EVENT,
            event: KeyEventRecord {
                key_down: 1,
                repeat_count: 1,
                virtual_key_code: virtual_key,
                virtual_scan_code: 0,
                unicode_char: unicode,
                control_key_state: control,
            },
        }
    }

    fn typed(text: &str) -> Vec<InputRecord> {
        text.encode_utf16().map(|unit| key(unit, 0, 0)).collect()
    }

    fn inject(input: Handle, records: &[InputRecord]) {
        let mut written = 0u32;
        let ok = unsafe {
            WriteConsoleInputW(input, records.as_ptr(), records.len() as u32, &mut written)
        };
        assert!(ok != 0 && written as usize == records.len(), "injecting keys failed");
    }

    fn console_mode(input: Handle) -> u32 {
        let mut mode = 0u32;
        assert!(unsafe { GetConsoleMode(input, &mut mode) } != 0, "GetConsoleMode failed");
        mode
    }

    /// A private console, this process' stdin/stderr pointed at it, a scenario
    /// typed into it, and three console-mode measurements printed to the
    /// stdout pipe the parent still holds.
    #[test]
    #[ignore = "driven by the tests above, in a child process with its own console"]
    fn the_child_drives_a_real_console() {
        let Ok(scenario) = std::env::var(SCENARIO_ENV) else {
            panic!("{SCENARIO_ENV} must name the scenario");
        };

        // A console of this child's own. Detaching first is what keeps the
        // developer's terminal out of it; a process with no console to detach
        // from simply fails this call, which is fine.
        unsafe { FreeConsole() };
        assert!(unsafe { AllocConsole() } != 0, "AllocConsole failed");

        let input = open("CONIN$", GENERIC_READ | GENERIC_WRITE);
        let error = open("CONOUT$", GENERIC_READ | GENERIC_WRITE);
        // stdin and stderr become the console: that is what makes this a
        // terminal as far as the prompt is concerned. stdout is deliberately
        // left as the pipe the parent reads the measurements from.
        assert!(unsafe { SetStdHandle(STD_INPUT_HANDLE, input) } != 0);
        assert!(unsafe { SetStdHandle(STD_ERROR_HANDLE, error) } != 0);

        let before = console_mode(input);
        println!("CONSOLE MODE_BEFORE={before}");

        let (keys, expect_key): (Vec<InputRecord>, bool) = match scenario.as_str() {
            "escape" => (vec![key(0x1b, VK_ESCAPE, 0)], false),
            "ctrl_c" => (vec![key(0x03, u16::from(b'C'), LEFT_CTRL_PRESSED)], false),
            "typed" => {
                let mut records = typed("sk-real-key1");
                records.push(key(0x0d, 0, 0));
                (records, true)
            }
            "empty" => (vec![key(0x0d, 0, 0)], false),
            other => panic!("unknown scenario {other}"),
        };

        // Injected once the prompt has actually taken the console, so the mode
        // measured is the prompt's and nothing is typed before it takes
        // effect. A bounded readiness condition, not a fixed sleep: the wait
        // ends the moment echo is observed to be off.
        let input_bits = input as usize;
        let injector = std::thread::spawn(move || {
            const ENABLE_ECHO_INPUT: u32 = 0x0004;
            let handle = input_bits as Handle;
            let deadline = std::time::Instant::now() + std::time::Duration::from_secs(20);
            let mut during = console_mode(handle);
            while std::time::Instant::now() < deadline {
                during = console_mode(handle);
                if during & ENABLE_ECHO_INPUT == 0 {
                    break;
                }
                std::thread::sleep(std::time::Duration::from_millis(5));
            }
            inject(handle, &keys);
            during
        });

        let result = coda_boot::console::read_masked_line("key (not shown): ");
        let during = injector.join().expect("the injector thread");
        println!("CONSOLE MODE_DURING={during}");
        println!("CONSOLE MODE_AFTER={}", console_mode(input));

        match result {
            Ok(secret) => {
                assert!(expect_key, "this scenario must not produce a key");
                println!("CONSOLE RESULT=key");
                println!("CONSOLE LEN={}", secret.expose().len());
            }
            Err(coda_boot::console::PromptError::Cancelled) => {
                println!("CONSOLE RESULT=cancelled");
            }
            Err(coda_boot::console::PromptError::Invalid(_)) => {
                println!("CONSOLE RESULT=invalid");
            }
            Err(other) => println!("CONSOLE RESULT=error:{other}"),
        }
        // Flushed before the process exits, or the parent reads nothing.
        use std::io::Write;
        let _ = std::io::stdout().flush();
    }
}
