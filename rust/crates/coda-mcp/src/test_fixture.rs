//! The test executable doubles as a fake MCP stdio server so process-level
//! behaviour (EOF handling, crashes, grandchildren) can be tested for real.

use std::collections::HashMap;
use std::io::{BufRead, Write};
use std::time::Duration;

use serde_json::{Value, json};

use crate::config::McpConnectable;

pub(crate) const MODE_ENV: &str = "CODA_MCP_FAKE_SERVER";
pub(crate) const PID_FILE_ENV: &str = "CODA_MCP_FAKE_PID_FILE";

const ENTRY_TEST: &str = "test_fixture::fake_mcp_server_entry";

/// Program + args + env that start this test binary as a fake server in `mode`.
pub(crate) fn fake_server_command(mode: &str) -> (String, Vec<String>, HashMap<String, String>) {
    let exe = std::env::current_exe()
        .expect("current exe")
        .to_string_lossy()
        .into_owned();
    let args = [
        ENTRY_TEST,
        "--exact",
        "--ignored",
        "--nocapture",
        "--test-threads=1",
        "-q",
    ]
    .iter()
    .map(|s| s.to_string())
    .collect();
    let env = HashMap::from([(MODE_ENV.to_string(), mode.to_string())]);
    (exe, args, env)
}

pub(crate) fn fake_server_connectable(
    name: &str,
    mode: &str,
    extra_env: &[(&str, &str)],
) -> McpConnectable {
    let (command, args, mut env) = fake_server_command(mode);
    for (k, v) in extra_env {
        env.insert(k.to_string(), v.to_string());
    }
    McpConnectable {
        name: name.to_string(),
        command,
        args,
        env,
    }
}

#[cfg(windows)]
pub(crate) fn is_process_alive(pid: u32) -> bool {
    use windows_sys::Win32::Foundation::{CloseHandle, STILL_ACTIVE};
    use windows_sys::Win32::System::Threading::{
        GetExitCodeProcess, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION,
    };
    // SAFETY: plain Win32 calls; the handle is closed before returning.
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
        if handle.is_null() {
            return false;
        }
        let mut code = 0u32;
        let ok = GetExitCodeProcess(handle, &mut code);
        CloseHandle(handle);
        ok != 0 && code == STILL_ACTIVE as u32
    }
}

#[cfg(unix)]
pub(crate) fn is_process_alive(pid: u32) -> bool {
    // SAFETY: signal 0 only checks for existence.
    unsafe { libc::kill(pid as libc::pid_t, 0) == 0 }
}

pub(crate) async fn wait_until(timeout: Duration, f: impl Fn() -> bool) -> bool {
    let deadline = tokio::time::Instant::now() + timeout;
    loop {
        if f() {
            return true;
        }
        if tokio::time::Instant::now() >= deadline {
            return false;
        }
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
}

#[test]
#[ignore = "entry point for the fake MCP server child process"]
fn fake_mcp_server_entry() {
    if let Ok(mode) = std::env::var(MODE_ENV) {
        run(&mode);
        std::process::exit(0);
    }
}

fn run(mode: &str) {
    match mode {
        "sleep" => {
            std::thread::sleep(Duration::from_secs(60));
            return;
        }
        "exit-now" => std::process::exit(3),
        "grandchild" => spawn_grandchild(),
        _ => {}
    }

    let tools = if mode == "tools2" {
        json!([tool("echo"), tool("extra")])
    } else {
        json!([tool("echo")])
    };
    serve(&tools);

    match mode {
        "hang" => std::thread::sleep(Duration::from_secs(60)),
        "crash" => std::process::exit(3),
        _ => {}
    }
}

fn tool(name: &str) -> Value {
    json!({ "name": name, "description": "", "inputSchema": { "type": "object" } })
}

fn spawn_grandchild() {
    let exe = std::env::current_exe().expect("current exe");
    let (_, args, _) = fake_server_command("sleep");
    let child = std::process::Command::new(exe)
        .args(args)
        .env(MODE_ENV, "sleep")
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .spawn()
        .expect("spawn grandchild");
    if let Ok(path) = std::env::var(PID_FILE_ENV) {
        // Write-then-rename so a reader never sees a partial pid.
        let partial = format!("{path}.partial");
        std::fs::write(&partial, child.id().to_string()).expect("write pid file");
        std::fs::rename(&partial, &path).expect("publish pid file");
    }
}

fn serve(tools: &Value) {
    let stdin = std::io::stdin();
    let mut stdout = std::io::stdout();
    for line in stdin.lock().lines() {
        let Ok(line) = line else { break };
        let Ok(msg) = serde_json::from_str::<Value>(&line) else {
            continue;
        };
        let id = msg.get("id").cloned();
        let method = msg.get("method").and_then(Value::as_str).unwrap_or("");
        let result = match method {
            "initialize" => json!({
                "protocolVersion": "2025-06-18",
                "serverInfo": { "name": "fake", "version": "1" },
                "capabilities": {}
            }),
            "tools/list" => json!({ "tools": tools }),
            "tools/call" => json!({
                "content": [{ "type": "text", "text": format!("pid:{}", std::process::id()) }],
                "isError": false
            }),
            _ => json!({}),
        };
        if id.is_none() {
            continue;
        }
        let reply = json!({ "jsonrpc": "2.0", "id": id, "result": result });
        if writeln!(stdout, "{reply}").and_then(|_| stdout.flush()).is_err() {
            break;
        }
    }
}
