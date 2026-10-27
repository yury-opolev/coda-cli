# Evidence: how coding-agent harnesses handle long-running work and mid-run user input

- **Date:** 2026-10-27
- **Supports:** [../2026-10-27-nonblocking-agent-runtime.md](../2026-10-27-nonblocking-agent-runtime.md)
- **Method:** source reading of shallow clones (default branches) for Codex, Gemini CLI,
  OpenCode, Cline, Goose, Aider; official docs + changelogs for Claude Code and GitHub Copilot
  CLI. Line numbers are snapshots and will drift. Items marked *unverified* were not confirmed in
  source/docs.

## Commits read

| Repo | Commit |
|---|---|
| openai/codex (`codex-rs/`) | `2ebf42f269d8d267e2d55a699c741fbb00402e22` |
| google-gemini/gemini-cli | `363a1311` |
| sst/opencode | `4a85158e` |
| cline/cline | `775d0398` |
| block/goose | `398165c9` |
| Aider-AI/aider | `5dc9490b` |

Permalink form: `https://github.com/<org>/<repo>/blob/<sha>/<path>#L<a>-L<b>`.

## 1. OpenAI Codex CLI (source)

**Shell tools.** With UnifiedExec, the model gets `exec_command` + `write_stdin`
(`core/src/tools/spec_plan.rs:1242-1276`). `exec_command` "Runs a command in a PTY, returning
output or a session ID for ongoing interaction" (`core/src/tools/handlers/shell_spec.rs:89-97`).

| Constant (`core/src/unified_exec/mod.rs:66-75`) | Value |
|---|---|
| default `yield_time_ms` (`handlers/unified_exec.rs:113-115`) | 10,000 |
| `MIN_YIELD_TIME_MS` / `MAX_YIELD_TIME_MS` | 250 / 30,000 |
| Windows floor for first yield | 10,000 |
| `DEFAULT_MAX_BACKGROUND_TERMINAL_TIMEOUT_MS` (max empty-poll wait) | 300,000 |
| `MAX_UNIFIED_EXEC_PROCESSES` | 64 (LRU eviction, 8 most recent protected; `process_manager.rs:1723-1794`) |

- Still running at the yield deadline → partial output + `session_id`
  (`process_manager.rs:607-660, 742-752`).
- *No kill timeout* for interactive exec; yield only decides when to return
  (`process_manager.rs:648-660`).
- Processes survive turn end and `Op::Interrupt` ("Abort current task without terminating
  background terminal processes", `protocol/src/protocol.rs:697-699`). Killed by `/stop`
  (`Op::CleanBackgroundTerminals`), shutdown, or eviction
  (`core/src/session/handlers.rs:64-66, 313-317`).
- Shell completion is emitted to clients (`async_watcher.rs:175-240`) but *not pushed into model
  input*; the model polls with empty `write_stdin`.
- `/ps` lists, `/stop` kills all; both usable mid-task (`tui/src/slash_command.rs:131-132, 249,
  292-293`). Footer: "N background terminals running · /ps to view · /stop to close"
  (`tui/src/bottom_pane/unified_exec_footer.rs:53`).

**Parallel tools.** `parallel_tool_calls: true` (`core/src/session/turn.rs:1708`); read/write-lock
runtime: parallel-safe tools (incl. `exec_command`, `write_stdin`) share, others exclusive
(`core/src/tools/parallel.rs:49-55, 279-291`).

**Mid-turn input.** Single `Op::TurnInput { mode }` with `StartOrSteer | StartIfIdle |
ContinueIfIdle | Steer` (`protocol/src/turn_input.rs:188-198`). Steered input is drained at the
top of each sampling iteration (`core/src/session/turn.rs:466-485`), after in-flight tools finish
(`turn.rs:3436-3447`); since exec yields ≤ 30 s, steering lands within that bound. `wait_agent`
and `sleep` end early on new input ("Wait interrupted by new input.",
`handlers/multi_agents_v2/wait.rs:70-74, 200-217`). TUI: Enter = steer, Tab = queue
(`tui/src/bottom_pane/chat_composer.rs:836-837`). Esc with pending steers interrupts then submits
them (`chatwidget/interaction.rs:272-284`).

**Side questions.** `/side` (alias `/btw`): "start a side conversation in an ephemeral fork",
usable during a task (`tui/src/slash_command.rs:146-148, 307-308`); fork is `ephemeral = true`
with instructions not to continue parent tasks or mutate (`tui/src/app/side.rs:28-42, 629-643`).

**Subagents.** `spawn_agent` returns immediately (`handlers/multi_agents_spec.rs:79, 421-457`).
Completion → `InterAgentCommunication` into the parent's mailbox
(`core/src/agent/control/completion.rs:60-135`), drained at sampling boundaries; an idle parent
starts a turn only for `trigger_turn` mail or with `WakeOnAgentMessages`
(`core/src/tasks/mod.rs:452-545`). `wait_agent` default 30 s, max 1 h
(`core/src/config/mod.rs:260-264`).

*Unverified:* how the model kills a session (likely Ctrl-C via `write_stdin`); whether
`apply_patch` is exclusive (trait default false).

## 2. Claude Code (docs, changelog)

Sources: <https://code.claude.com/docs/en/tools-reference>,
<https://code.claude.com/docs/en/interactive-mode>, <https://code.claude.com/docs/en/sub-agents>,
<https://code.claude.com/docs/en/commands>,
<https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md> (fetched 2026-10-27).

- Bash timeout default 2 min, max 10 min (`BASH_DEFAULT_TIMEOUT_MS`, `BASH_MAX_TIMEOUT_MS`);
  `run_in_background: true` returns a task id immediately.
- *At timeout the command is moved to the background, not killed* (except commands starting
  with `sleep`): "Command did not complete within its 120s timeout and was moved to the
  background", with task id and output-file path. Disabled by
  `CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1`.
- Background limits: none in local interactive sessions; 30 min (up to 2 h) in unattended runs.
- Output read with Read on the output file (`TaskOutput` removed v2.1.277); `TaskStop` stops;
  `Monitor` streams output lines as events ("Claude interjects when an event arrives").
- *Completions pushed* inside `<system-reminder>`, mid-turn and between turns (v2.1.234).
  *Unverified:* whether an idle session starts a new turn by itself.
- Ctrl+B backgrounds running Bash calls and agents (unified in v2.1.0). Exact tool result on
  Ctrl+B is *unverified*.
- Queued messages are delivered within the same turn after the current tool calls; *Send now*
  (Ctrl+Enter, v2.1.275+) moves backgroundable running work to the background and delivers
  immediately; otherwise interrupts (v2.1.281).
- `/btw`: sees conversation incl. tool results; *no tools*; not added to history; runs while
  Claude works; aware of in-flight tool call (v2.1.280); `f` forks to a background subagent.
- Subagents start in the *background by default*; results arrive as a completion notification in
  a later turn; 20 concurrent by default; `/tasks` manages; Ctrl+X Ctrl+K stops all.
- Background subagents' permission prompts surface in the main session, naming the subagent.

## 3. GitHub Copilot CLI (docs, changelog, observed)

Sources: <https://docs.github.com/en/copilot/reference/cli-command-reference>,
<https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/steer-agents>,
<https://docs.github.com/en/copilot/how-tos/copilot-cli/use-copilot-cli/ask-a-side-question>,
<https://github.com/github/copilot-cli/blob/main/changelog.md> (fetched 2026-10-27).

- Shell tools: `bash`/`powershell`, `read_*`, `write_*`, `stop_*`, `list_*`. Observed (not in
  public docs): `mode: sync|async`, `initial_wait` (30–600 s, default 30) returning partial output
  while the command keeps running, `detach: true`.
- *1.0.74: "Steering interrupts shell output waits without stopping the running command."*
- 0.0.422: automatic notifications when background shells and agents complete; not duplicated
  when a read is already waiting (1.0.28, 1.0.70).
- Ctrl+X B moves the running task/shell to background (1.0.39). Observed tool result: "moved to
  background by the user… do NOT call any more tools for this command." `/tasks`: B background,
  X kill, R remove.
- *1.0.35: sync task calls now block until done instead of auto-moving to background after 60 s;
  use `mode: "background"`.* (Reason not documented.)
- 1.0.92: messaging a running background agent steers its active turn.
- Enter while busy = steer; Ctrl+Enter/Ctrl+Q = queue for next turn.
- `/ask` (alias `/btw`): conversation context, no tools, not in history, works while busy.

## 4. OpenCode (source, sst/opencode @ 4a85158e)

- shell tool `{command, workdir?, timeout?, background?}`
  (`packages/core/src/tool/plugin/shell.ts:48-61`); description: "You will be notified when it
  completes. DO NOT poll for completion" (`:57-60`); result repeats "end your response; you will
  be resumed automatically" (`:26-27, 96-101`).
- Foreground default 2 min, then *killed* (`core/src/shell.ts:366-393`); background has no
  timeout (`tool/plugin/shell.ts:201`).
- User `Session.background()` → `jobs.backgroundAll()`: running foreground calls return "Command
  moved to the background (shell ID …)", plus a synthetic message "User requested that active
  blocking work be moved to the background… Do not wait, sleep, poll…"
  (`core/src/session.ts:513-530`; `tool/plugin/shell.ts:259-267`).
- *Completion pushed* as a synthetic session message (`tool/plugin/shell.ts:151-186`).
- Durable inbox with per-item steer (default, next step boundary) or queue (turn idle)
  (`core/src/session/inbox.ts:52-57, 566-607`; `session/runner/llm.ts:62-68, 85-90, 159-170`).
- `Session.generate`: "Generates text from current Session context without admitting input or
  mutating history" (`session.ts:217-221, 486-493`) — UI exposure *unverified*.
- Model has no list/kill tool (HTTP endpoints only).

## 5. Gemini CLI (source, 363a1311)

- Shell `is_background?`, `delay_ms?` (`packages/core/src/tools/shell.ts:98-105`); background
  returns PID + initial output (`:810-860`).
- Inactivity timeout 300 s, resets on output; *kills* (`config/config.ts:1318-1319`;
  `shell.ts:705-714`).
- `list_background_processes`, `read_background_output` (`tools/shellBackgroundTools.ts`); kill is
  UI-only (Ctrl+B background, Ctrl+L list, Ctrl+K kill; `cli/src/ui/key/keyBindings.ts:409-412`).
- Completion behaviour `inject | notify | silent`, *default silent*
  (`cli/src/config/settingsSchema.ts:1612-1619`); injected output wrapped in
  `<background_output>` as data (`utils/fastAckHelper.ts:81-93`).
- Input while busy is queued by default (`cli/src/ui/hooks/useMessageQueue.ts:70-82`); steering
  is experimental (`settingsSchema.ts:2320-2327`).

## 6. Cline (source, 775d0398)

- `run_commands {commands}`; host picks foreground terminal vs headless exec
  (`apps/vscode/src/sdk/vscode-run-commands-tool.ts:1-11, 46`).
- "Proceed While Running": user button or automatic after 300 s detaches; output streams to a
  capped log; model told to read the log (`vscode-run-commands-tool.ts:51-60, 207-217, 269`).
  *No push.*
- Steer interrupts only the current model request; "running tools finish normally"
  (`sdk/packages/agents/src/agent-runtime.ts:635-638`).

## 7. Goose and Aider (source)

- Goose: `timeout_secs` default 300 s, kill on timeout or cancel; no background mode
  (`crates/goose/src/agents/platform_extensions/developer/shell.rs:183-189, 469-485`). Steering
  queue drained between turns (`agents/state_machine/ops_steer.rs:61-82`).
- Aider: user-confirmed, synchronous commands, no timeout, no background, no mid-run input
  (`aider/run_cmd.py`, `aider/coders/base_coder.py:2434-2490`).

## 8. Cross-harness patterns

1. Bounded wait + handle (Codex yield, Copilot `initial_wait`) — the strongest responsiveness
   guarantee without killing work.
2. User override to background running work (Claude, Copilot, OpenCode, Gemini, Cline).
3. Pushed completion with explicit "do not poll" (Claude, OpenCode, Copilot); absence of push is
   the most common failure (Gemini default, Cline, Codex shells).
4. Steer vs queue as distinct user intents (Codex, Copilot, OpenCode, Cline, Goose).
5. Interrupting a turn does not have to kill processes (Codex) — killing is a separate command.
6. Side questions are ephemeral and history-free (Claude, Copilot, Codex).

Anti-patterns: kill-on-timeout for builds (Gemini, Goose, OpenCode foreground); silent background
completion; model unable to stop its own background work; long invisible timers that change a
tool's return shape (Copilot reverted its 60 s task auto-background).
