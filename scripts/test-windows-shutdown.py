"""Hermetic Windows console acceptance: Python + pywinpty + psutil."""

import argparse
import json
import math
import os
from pathlib import Path
import re
import sys
import tempfile
import threading
import time


def mcp_fixture():
    pid_file = Path(os.environ["CODA_SHUTDOWN_FIXTURE_PID"])
    pid_file.write_text(str(os.getpid()))
    for line in sys.stdin:
        request = json.loads(line)
        if "id" not in request or request.get("method") == "shutdown":
            continue
        if request["method"] == "initialize":
            result = {
                "protocolVersion": request["params"]["protocolVersion"],
                "capabilities": {},
                "serverInfo": {"name": "shutdown-fixture", "version": "1"},
            }
        elif request["method"] == "tools/list":
            result = {"tools": []}
        else:
            result = {}
        print(json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}), flush=True)
    if os.environ.get("CODA_SHUTDOWN_FIXTURE_HANG") == "1":
        time.sleep(30)
    pid_file.with_suffix(".closed").write_text("stdin EOF")


MODES = """
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShutdownConsoleModes {
    [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr h, out uint mode);
}
'@
function Write-Modes($label) {
    [uint32]$i = 0; [uint32]$o = 0
    if (-not [ShutdownConsoleModes]::GetConsoleMode([ShutdownConsoleModes]::GetStdHandle(-10), [ref]$i)) {
        throw 'Cannot read console input mode'
    }
    if (-not [ShutdownConsoleModes]::GetConsoleMode([ShutdownConsoleModes]::GetStdHandle(-11), [ref]$o)) {
        throw 'Cannot read console output mode'
    }
    Write-Output ('__MODES_'+$label+'__:'+ $i + ':' + $o)
}
Write-Modes 'BEFORE'
"""


def run_case(binary, mcp_count, hang, quit_mode):
    import psutil
    from winpty import Backend, PtyProcess

    os.environ["PYWINPTY_BLOCK"] = "0"
    os.environ["PYWINPTY_BACKEND"] = str(Backend.ConPTY)
    with tempfile.TemporaryDirectory(prefix="coda-shutdown-") as home:
        root = Path(home)
        env = {
            k: v for k, v in os.environ.items()
            if not k.upper().startswith(("CODA_", "ANTHROPIC_", "CLAUDE_", "GITHUB_", "GH_", "COPILOT_"))
        }
        env.update(
            CODA_HOME=home,
            CODA_SERVE_API_KEY="fixture-not-a-real-key",
            CODA_SERVE_DISABLE_MCP="0" if mcp_count else "1",
            CODA_DISABLE_PROJECT_MCP="0" if mcp_count else "1",
        )
        if mcp_count:
            servers = {
                f"fixture-{i}": {
                    "command": sys.executable,
                    "args": [str(Path(__file__).resolve()), "--mcp-fixture"],
                    "env": {
                        "CODA_SHUTDOWN_FIXTURE_PID": str(root / f"fixture-{i}.pid"),
                        "CODA_SHUTDOWN_FIXTURE_HANG": "1" if hang else "0",
                    },
                } for i in range(mcp_count)
            }
            (root / ".mcp.json").write_text(json.dumps({"mcpServers": servers}))
        script = root / "probe.ps1"
        script.write_text(
            MODES + "\nfunction prompt { '__SHELL_READY__> ' }\n"
            "& '" + str(binary).replace("'", "''") + "' --no-mouse\n"
            "$code = $LASTEXITCODE\nWrite-Modes 'AFTER'\n"
            "Write-Output ('__CODA_RETURNED__:' + $code)\n"
        )
        p = PtyProcess.spawn(
            ["powershell.exe", "-NoLogo", "-NoProfile", "-NoExit", "-File", str(script)],
            cwd=home, env=env, dimensions=(40, 140), backend=Backend.ConPTY,
        )
        owner = psutil.Process(p.pid)
        chunks = []
        reader_errors = []
        feedback_times = []
        owned = []

        def drain():
            try:
                while True:
                    text = p.read(65536)
                    chunks.append(text)
                    if "Shutting down" in text:
                        feedback_times.append(time.monotonic())
                    if "\x1b[6n" in text:
                        p.write("\x1b[1;1R")
            except EOFError:
                pass
            except Exception as error:
                reader_errors.append(str(error))

        reader = threading.Thread(target=drain, daemon=True)
        reader.start()

        def until(marker, timeout):
            end = time.monotonic() + timeout
            while marker not in "".join(chunks):
                if time.monotonic() > end:
                    raise AssertionError(
                        f"Missing {marker!r}; reader errors: {reader_errors}; output: {''.join(chunks)[-3000:]!r}"
                    )
                time.sleep(0.005)

        def console_modes(label):
            match = re.search(r"__MODES_" + label + r"__:(\d+):(\d+)", "".join(chunks))
            assert match, f"Missing {label} console modes"
            return tuple(map(int, match.groups()))

        try:
            until("Type /help", 20)
            time.sleep(0.5)
            owned = owner.children(recursive=True)
            assert len(list(root.glob("fixture-*.pid"))) == mcp_count, "MCP fixtures did not connect"
            if quit_mode == "ctrlc":
                p.write("\x03")
                time.sleep(0.1)
                accepted = time.monotonic()
                p.write("\x03")
            else:
                accepted = time.monotonic()
                p.write("/exit\r")
            if hang:
                time.sleep(0.15)
                p.write("\x03")
                # Incremental terminal draws need not emit the whole hint contiguously.
                until("Shutdown", 2)
            until("__CODA_RETURNED__", 10)
            returned = time.monotonic()
            output = "".join(chunks)
            left = output.rfind("\x1b[?1049l")
            assert left >= 0, "The alternate screen was never left"
            card = output[left:output.find("__CODA_RETURNED__", left)]
            assert "Session summary" in card, f"No session summary after the alternate screen: {card[-2000:]!r}"
            assert "--resume" in card, f"No resume reference after the alternate screen: {card[-2000:]!r}"
            if hang:
                assert "did not complete cleanly" in card, "A failed shutdown was not labelled unconfirmed"
            until("__SHELL_READY__", 5)
            p.write("Write-Output ('__HANDOFF_'+'OK__')\r")
            until("__HANDOFF_OK__", 3)
            handed_off = time.monotonic()
            assert not reader_errors, f"Console reader failed: {reader_errors}"
            match = re.search(r"__CODA_RETURNED__:(\d+)", "".join(chunks))
            assert match, "Missing frontend exit code"
            assert int(match[1]) == (1 if hang else 0), f"Unexpected frontend exit code: {match[1]}"
            before, after = console_modes("BEFORE"), console_modes("AFTER")
            assert before == after, f"Console modes changed: {before} -> {after}"
            assert feedback_times, "No explicit shutdown feedback"
            assert (feedback_times[0] - accepted) * 1000 < 100, "Shutdown feedback exceeded 100 ms"
            assert not any(child.is_running() for child in owned), "An owned descendant survived exit"
            eof_count = len(list(root.glob("fixture-*.closed")))
            assert hang or eof_count == mcp_count, "An MCP server was killed before graceful EOF"
            diagnostics = [
                json.loads(line) for log in (root / ".coda").rglob("*.jsonl")
                for line in log.read_text(encoding="utf-8-sig").splitlines()
            ]
            if not hang:
                assert not any(event.get("outcome") == "forced" for event in diagnostics)
                assert any(event.get("kind") == "engine_end" and event.get("exit_code") == 0
                           for event in diagnostics), "Engine exit was not observed"
                assert any(event.get("kind") == "entrypoint_completed" for event in diagnostics)
                elapsed = returned - accepted
                assert elapsed < 0.5, f"Healthy shutdown exceeded 500 ms: {elapsed:.3f}s"
            else:
                assert returned - accepted < 6, "Hung integration multiplied shutdown budgets"
                assert "forced termination" in "".join(chunks), "Forced stopping was not reported"
            return {
                "binary": str(binary), "mcp_count": mcp_count, "hung": hang, "quit": quit_mode,
                "shutdown_ms": round((returned - accepted) * 1000, 2),
                "feedback_ms": round((feedback_times[0] - accepted) * 1000, 2),
                "shell_command_ms": round((handed_off - returned) * 1000, 2),
                "input_mode": before[0], "output_mode": before[1], "mcp_eof": eof_count,
            }
        finally:
            try:
                descendants = owner.children(recursive=True)
                known = {(child.pid, child.create_time()): child for child in owned + descendants}
                owned = list(known.values())
            except psutil.NoSuchProcess:
                pass
            if p.isalive():
                p.write("exit\r")
                end = time.monotonic() + 2
                while p.isalive() and time.monotonic() < end:
                    time.sleep(0.01)
                if p.isalive():
                    p.terminate(force=True)
            for child in owned:
                if child.is_running():
                    child.kill()
                    child.wait(timeout=2)
            reader.join(timeout=2)


def main():
    if "--mcp-fixture" in sys.argv:
        mcp_fixture()
        return
    if os.name != "nt":
        raise SystemExit("This acceptance probe requires Windows.")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--runs", type=int, default=30)
    parser.add_argument("--mcp-count", type=int, default=0)
    parser.add_argument("--hang-mcp", action="store_true")
    parser.add_argument("--quit", choices=("ctrlc", "exit"), default="ctrlc")
    args = parser.parse_args()
    if args.runs < 1 or args.mcp_count < 0 or (args.hang_mcp and not args.mcp_count):
        parser.error("Use positive runs, nonnegative MCP count, and an MCP fixture for --hang-mcp.")
    rows = []
    for _ in range(args.runs):
        row = run_case(args.binary.resolve(), args.mcp_count, args.hang_mcp, args.quit)
        rows.append(row)
        print(json.dumps(row), flush=True)
    latencies = sorted(row["shutdown_ms"] for row in rows)
    print(json.dumps({"runs": len(rows), "p95_shutdown_ms": latencies[math.ceil(len(rows) * 0.95) - 1],
                      "max_shutdown_ms": max(latencies)}))


if __name__ == "__main__":
    main()
