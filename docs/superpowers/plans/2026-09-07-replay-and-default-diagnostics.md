# Reasoning Replay and Default Diagnostics Implementation Plan

> **For agentic workers:** Execute the two fixes serially with red/green regression evidence. An independent subagent critiques this plan before implementation; an independent final review gates release.

**Goal:** Repair continuation of saved signed-reasoning conversations and ensure ordinary Rust Coda launches leave bounded, privacy-safe, session-correlated operational diagnostics.

**Architecture:** Fix reasoning at the existing Responses serialization boundary without changing saved signature formats. Add a small leaf `coda-diagnostics` crate with typed, allowlisted JSONL records, bounded local file rotation, and explicitly propagated async context. Initialize it in each executable process; do not enable arbitrary existing tracing targets or content-audit output by default.

**Tech Stack:** Existing Rust workspace, serde/serde_json, Tokio, UUID, chrono, directories, existing Cargo test infrastructure. No remote telemetry, new testing framework, or external log service.

## Scope and sequencing

1. Critique and revise this plan.
2. BUG 2: regression, minimal serializer fix, decoder/history/replay coverage, safe continuation reproduction.
3. BUG 3: diagnostic writer, entry points, process/context wiring, failure/retry coverage, discovery and documentation.
4. Restore YOLO outside-repository file access (added user request), with its own red/green coverage.
5. Independent final review; correct important findings and rerun affected tests.
6. Commit/push feature PR, merge, build/package/install Rust Coda, separate version PR.

The original user sessions, settings, credentials, prompts, and opaque reasoning are not modified or copied. Live reproduction may use only a small arithmetic prompt through the already-configured provider; automated coverage uses fake credentials and local HTTP fixtures.

## Confirmed defects and constraints

- `rust\crates\coda-llm\src\copilot\responses.rs::append_assistant_input` reconstructs `{type,id,encrypted_content}` without the required item-level `summary`.
- Top-level `reasoning.summary = "auto"` is unrelated to the required input field.
- `Content::Thinking.text` already contains the accumulated visible provider summary; signatures already contain `id` and `encrypted_content`.
- `coda` initializes optional file tracing only in interactive mode. Its headless and serve branches do not initialize logging.
- Standalone `coda-tui` also requires `--log-file`.
- Engine stderr and malformed protocol frames can contain arbitrary content. Existing `LlmError::Display` can contain server-authored text or credential-bearing URLs. None are safe routine diagnostic fields.
- Windows profile isolation must use `CODA_HOME`; changing `HOME` or `USERPROFILE` alone is insufficient.
- Audit sidecars contain content and are not a substitute for safe diagnostics. This change will document their current limitations rather than silently enabling content capture.

### C# comparison

- `src\LlmClient\OpenAiResponsesRequest.cs::AppendAssistantInput` also omits
  item-level `summary`. BUG 2 is an inherited serializer defect newly exposed
  by Rust preserving encrypted-only reasoning, not a C#-correct schema that
  Rust alone removed.
- C# `TelemetryResolver.Resolve` defaults to `TelemetrySettings.Disabled`;
  it nevertheless has fuller saved-settings/environment integration and
  bounded log rotation. BUG 3 includes that Rust integration gap, but
  always-on privacy-safe operational logging is an explicit improvement
  beyond C# default behavior. Do not describe C# as always-on.

## Decisions

### Reasoning replay

For each signed thinking block, keep the current ID/encrypted-content extraction and existing invalid-signature behavior. Add `summary` derived from that block's text:

```rust
let summary = if text.is_empty() {
    Vec::new()
} else {
    vec![json!({"type": "summary_text", "text": text})]
};
input.push(json!({
    "type": "reasoning",
    "id": id,
    "encrypted_content": enc,
    "summary": summary,
}));
```

Do not trim, expose, decrypt, discard, or rewrite the stored reasoning. An old signature containing only `id` and `encrypted_content` remains sufficient. A single summary_text entry preserves the already-accumulated visible summary without inventing lost part boundaries.

### Default logging versus tracing

Use a typed writer, not a blanket `tracing_subscriber` at info/warn:

- Required records: process start/end, engine launch/exit, session initialized/resumed, turn start/end/failure, HTTP attempt/result/retry, streamed API failure, transport/protocol failure, startup/auth configuration failure.
- Fixed record envelope: UTC timestamp, product version, process role/PID, run ID, event kind, optional session ID, turn ID, internal request ID, canonical provider/model.
- Optional failure fields: controlled classification, HTTP status, validated bounded provider request ID, attempt number, retry delay, duration, recognized safe parameter path.
- No prompt, system prompt, raw error Display/Debug, response/request body, headers, encrypted reasoning, tool name/arguments/results, paths from requests, stderr contents, or arbitrary formatted message field.
- Preserve useful safe details for the reported error: a recognized missing-parameter error may record `parameter: "input[22].summary"` and a fixed explanation. Unknown provider text is represented by category/status only.
- Routine records are always enabled regardless of legacy `telemetry.enabled`.
  Add `--diagnostic-verbosity normal|debug|trace`. Keep `--log-filter`/`CODA_LOG`
  as documented compatibility verbosity hints (validate existing EnvFilter
  syntax and use its maximum requested level); module selectors do not enable
  arbitrary raw tracing. The new explicit verbosity flag takes precedence.
  Replace, rather than coexist with, the previous file tracing subscriber.
  No unrestricted payload-tracing mode is added.
- `--log-file` becomes a destination override. Root logging flags must work for interactive, run, and serve modes; standalone TUI honors the same contract.

The alternative of routing current warn/debug events into a JSON formatter was rejected because filtering on level or module does not make their fields safe.

### Files, rotation, and failures

- Default directory: `<CODA_HOME or OS user home>\.coda\logs\diagnostics`.
- Each process owns a unique file: `coda-<utc>-<pid>-<uuid>.jsonl`. Parents and children never write the same file.
- Explicit parent destination is used as requested; its child writes a distinct engine companion in the same directory. A per-launch run ID links them.
- Keep each segment below 5 MiB, at most four segments per process stream, and each record below 8 KiB. Enforce the limit before writing a whole JSON record; never truncate serialized JSON bytes.
- Managed default-directory retention: seven days and a 100 MiB inactive-file budget. Apply at startup and rotation, touching only recognized diagnostic filenames, never arbitrary files, symlinks, or active process streams. Use an OS-backed advisory lease (the small `fs2` library is justified if no existing cross-platform helper exists), not an mtime guess: a long-idle live process must remain protected. Default UUID-named streams cannot be reused; explicit-destination lockfiles remain stable.
- Use private file/directory permissions where supported. A missing parent directory is created. An invalid explicit `--log-file` is a startup error. An unavailable default directory instead creates an unhealthy logging state, emits a payload-free warning, and keeps the app usable. `/log` must expose this degraded state; the native frontend warns if the child reports no diagnostic path.
- Writes use a mutex-protected synchronous file and flush complete records promptly; no unbounded queue or process-exit-only flush. Track runtime write failure, emit a one-time payload-free warning, and expose unhealthy logging in discovery.
- Explicit files append rather than truncate existing logs, rotating into bounded `.1`, `.2`, `.3` suffixes. Hold an OS-backed exclusive advisory lock on a stable companion lockfile for the writer lifetime. A second explicit writer fails clearly; a crash automatically releases the OS lock.

### Context across async/process boundaries

- One logger per executable process; library-only callers/tests need not touch real profiles.
- A scoped `DiagnosticContext` carries logger + run/session/turn/request/provider/model identity through async work; never use a process-global current session.
- Generate an internal diagnostic turn ID before prompt execution, including startup/prompt failures. Log the agent root-turn ID when available so diagnostic and protocol identities can be related.
- Create an internal request ID for each outer stream attempt in `stream_with_retries`, scoping both `client.stream().await` and `drive_stream`. HTTP retry metadata is recorded inside `send_with_retry` before headers/body are consumed. Stream/pump errors return to `drive_stream`, where diagnostics retain context; no pump task-local propagation is necessary unless a pump itself gains diagnostic records.
- Child engines initialize their own logger and return its path in the existing optional `InitializeResult.telemetryLogPath`. Parent records that path/run association as operational metadata, not raw stderr.
- Engine exits are recorded by the parent using exit code and correlation only. Raw stderr remains excluded even if it contains plausible token prefixes or multiline JSON.
- `/log` reports the actual frontend and engine log paths, enabled operational mode, safe verbosity, and writer health. It must not pretend changes to currently-unused legacy telemetry settings disable essential diagnostics.
- Failure-detail extraction accepts only a bounded structured `error.param`
  matching `^input\[[0-9]{1,6}\]\.(summary|id|encrypted_content)$` or a small
  explicit top-level parameter allowlist. A recognized missing-parameter
  message may extract that same path, never copy the message. Explanation
  text is generated from an internal enum. Other details remain category-only.
- Remove raw payload/error/stderr fields from the directly involved tracing
  call sites, even though the new writer is not a tracing subscriber.

## Task 1: Replay regression and fix

**Modify/test:** `rust\crates\coda-llm\src\copilot\responses.rs`.

- [ ] Add an encrypted-only historical signature regression:

```rust
#[test]
fn encrypted_reasoning_replay_has_required_empty_summary() {
    let signature = json!({"id":"r1","encrypted_content":"opaque"}).to_string();
    let message = Message::new(Role::Assistant, vec![
        Content::Thinking { text: String::new(), signature: Some(signature) }
    ]);
    let body = build(&ChatRequest::new("gpt-6-astra", vec![message]));
    assert_eq!(body["input"][0]["summary"], json!([]));
    assert_eq!(body["input"][0]["id"], "r1");
    assert_eq!(body["input"][0]["encrypted_content"], "opaque");
}
```

- [ ] Run from `rust`: `cargo test -p coda-llm --lib reasoning_replay --quiet`. Confirm missing summary fails before production edits.
- [ ] Bind `text` in the existing Thinking match and implement the serialization snippet above.
- [ ] Add visible-summary, multiple-signed-item, and decoder-to-serialized-history-to-next-request coverage. Assert each item has its own summary and unchanged signature fields. Reuse `Message` serde/history codec rather than editing a real transcript.
- [ ] Run `cargo test -p coda-llm --lib copilot::responses::tests --quiet`.
- [ ] Reproduce two-turn continuation with fake local endpoint validation; if live credentials are available, use isolated arithmetic only and retain only success/timing metadata.

## Task 2: Bounded typed diagnostic writer

**Create:** `rust\crates\coda-diagnostics\Cargo.toml`, `src\lib.rs`, `src\writer.rs`, `src\context.rs`.
**Modify:** `rust\Cargo.toml` workspace members/dependencies and dependent crate manifests.

Implement these shared interfaces (exact names can follow existing conventions; behavior is fixed):

```rust
pub struct Options {
    pub directory: std::path::PathBuf,
    pub file: Option<std::path::PathBuf>,
    pub role: ProcessRole,
    pub version: String,
    pub verbosity: Verbosity,
}
pub struct Limits {
    pub segment_bytes: u64,
    pub retained_segments: usize,
    pub record_bytes: usize,
    pub retention_age: std::time::Duration,
    pub inactive_total_bytes: u64,
}
pub enum ProcessRole { Tui, Run, Serve }
pub enum Verbosity { Normal, Debug, Trace }
// Logger::open(options, limits) -> io::Result<Logger>
// Logger::record(context, typed_event) -> io::Result<()>
// Logger::status() -> active path, mode, health
// scope(context, future) propagates context through the awaited request/consumer.
```

- [ ] Red: with temporary paths and injected small limits, assert automatic file creation, parseable JSON records, stable context, UTF-8 record bounds, rotation before overflow, retention ownership filtering, append behavior, explicit-file collision behavior, and invalid destination failure.
- [ ] Red privacy matrix: feed sentinel secrets into candidate errors/IDs/paths; no raw sentinel or content-bearing field may appear. Token-pattern replacement alone is insufficient.
- [ ] Green: implement typed event serialization and safe structured fields, shared contextual scope, bounded writer and lease-protected owned-file cleanup. Resolve CODA_HOME/OS-home paths at executable call sites, not through a dependency from this leaf crate on coda-auth.
- [ ] Run `cargo test -p coda-diagnostics --quiet`.

## Task 3: Default entry-point and process wiring

**Modify:**
- `rust\crates\coda\src\main.rs`
- `rust\crates\coda-tui\src\main.rs`, `src\cli.rs`, `src\app\mod.rs`, `src\app\slash\config.rs`
- `rust\crates\coda-client\src\process.rs`
- `rust\crates\coda-serve\src\host.rs`, `src\transport.rs`
- Corresponding Cargo manifests.

- [ ] Red: root CLI accepts log destination/verbosity for every mode; standalone TUI shares defaults. Initialization produces a log without flags, and explicit destination does not require turning telemetry on.
- [ ] Green: initialize before mode dispatch; retain logger across the async run. Record process-end explicitly before any `process::exit`; a Drop-only shutdown record is insufficient.
- [ ] Red: parent and child get distinct files but matching run IDs; initialized engine path is returned and visible via `/log`.
- [ ] Green: forward only internal run/path identity to child via `EngineCommand` environment API, not auth data; propagate it through engine restarts. Do not redirect protocol stdout.
- [ ] Red: failure before initialize and unexpected child exit leave safe records; child stderr sentinel never appears in persistent logs.
- [ ] Green: record bounded lifecycle/exit metadata and show genuine logging state/path rather than only saved legacy preferences.

## Task 4: Correlated provider failures and retries

**Modify:**
- `rust\crates\coda-llm\src\error.rs`, `src\retry.rs`, `src\stream.rs` (or actual pump module)
- `rust\crates\coda-llm\src\anthropic\client.rs`, `src\copilot\client.rs`
- `rust\crates\coda-agent\src\agent\stream.rs`
- `rust\crates\coda-serve\src\host.rs`, `src\transport.rs`.

- [ ] Red: mocked HTTP 400 missing input summary produces a durable record with session/turn/request/provider/model, status 400, and safe missing-parameter detail, without full message/body.
- [ ] Red: mock 429/503 then success records retry attempt/delay and recovery with the same request correlation.
- [ ] Red: streamed API error, incomplete stream, and transport error retain classification/context when consumed from a spawned pump.
- [ ] Red: malicious body, URL query, Authorization, encrypted reasoning, prompt and tool-result sentinels are absent at all verbosity levels.
- [ ] Green: add an explicit conversion from `LlmError` to safe diagnostic fields; never `%error` or `format!("{error:?}")` into the writer.
- [ ] Green: record HTTP metadata before consuming the response. Capture bounded validated `request-id`/`x-request-id` when present; unavailable identifiers remain absent, never invented.
- [ ] Green: wrap complete prompt execution (including preflight failures and cleanup) in diagnostic context. Scope the model stream attempt around client creation/request and stream consumption. Record failure before returning it to the UI; log HTTP retries where decided.
- [ ] Run targeted error/retry/stream, serve prompt/transport, and TUI discovery tests using existing runners.

## Task 5: No-flags executable regression

**Create:** `rust\crates\coda\tests\default_diagnostics.rs`.
**Reuse:** existing `coda-client` framed connection and `CODA_HOME` isolation.

- [ ] Launch the real `CARGO_BIN_EXE_coda` serve process with no logging flags, temporary CODA_HOME/working directory and no MCP; remove inherited auth/startup variables.
- [ ] Initialize and submit a hermetic failing request; close stdin and wait for exit. Assert an on-disk operational log, initialized path discovery, session/turn identity and failure record without opening the transcript.
- [ ] Exercise an actual fake local provider for HTTP/stream/transport failure, not just no-credential failure, through the same host diagnostic path.
- [ ] Use the existing `--api-key`/`--endpoint` seam and a local fake
  Anthropic Messages SSE server for executable fixtures; no Copilot token
  exchange or actual credential is needed.
- [ ] Launch a real headless frontend with child engine and a controlled failing fixture; correlate both log files. Verify no payload logs and bounded growth under small injected test limits.
- [ ] Ensure a TUI terminal-independent initialization test covers the no-argument interactive branch; do not require a user's console or live login.

## Task 6: YOLO file-access parity (added user request)

**Investigate/modify/test:** `rust\crates\coda-serve\src\host.rs`, the serve
permission-prompt adapter, `rust\crates\coda-agent\src\agent\tools.rs` and
`src\subagents\host.rs` only as needed for shared effective mode.

The Rust `coda-tool::sandbox::try_resolve_within_root` already honors
`allow_outside_root`. `AgentLoopBuilder` supports `with_permission_mode_state`,
but the current serve-host construction does not pass it. Compare the full
runtime path with C# before changing it.

- [ ] Red: use real built-in file tools against temporary sibling directories
  through the serve/agent path. Normal mode must reject outside-root access;
  bypassPermissions/YOLO must permit it.
- [ ] Cover both CLI startup YOLO and session/setPermissionMode, mode changes
  during a turn, subagent inheritance, and reset to normal mode.
- [ ] Green: pass the effective shared permission-mode state into tool
  execution; do not hardcode allow_outside_root, weaken path resolution, or
  override explicit tool restrictions.
- [ ] Use only `with_permission_mode_state(Arc::clone(&self.permission_mode))`
  for the main loop. Add the same shared Arc to SubagentHost and carry it
  through both host construction paths, child builders, and inherited nested
  hosts. Do not introduce a second snapshot permission mode.
- [ ] Explicitly keep Default, Plan and AcceptEdits outside-root-denied.
  Flip both directions during one turn and assert the next actual file call
  observes the new state. Scheduled subagents share this same live state.
- [ ] Run targeted permissions/tool-context/serve tests and include this
  amendment in independent critique/final review.

## Task 7: Review, documentation, and release

**Modify:** `README.md`, `rust\README.md`, directly-related CLI help/contract fixtures.

- [ ] Document default directory, rotation limits, explicit destination behavior, safe verbosity, `/log` discovery, child logging, and log-write failure behavior.
- [ ] Explain transcript vs operational diagnostics vs content audit sidecar separately. Do not claim every session has an audit sidecar.
- [ ] Update any existing protocol contract that assumed telemetryLogPath is null, with an explicit path-field normalization rather than ignoring unrelated differences.
- [ ] Independent final review: schema compliance, saved-history compatibility, privacy boundary, async context propagation, file ownership/retention safety, all launch modes.
- [ ] Correct every important finding; rerun only the affected selectors and an executable-level default logging regression.
- [ ] Commit/push/merge feature changes only after review. Run root `.\build.ps1 -Deploy`; commit the generated version separately and merge its PR. Confirm installed version and clean main.

## Plan critique checklist

The critic must challenge whether this plan actually proves:

1. Existing encrypted-only saved sessions can continue without losing reasoning.
2. Default logs exist in the *engine*, not only frontend.
3. HTTP, stream and transport failures correlate through spawned tasks.
4. The exact reported missing-summary failure leaves useful safe metadata.
5. No blanket enablement of unsafe tracing, error strings, stderr or content audits.
6. Rotation is valid JSON, per-file bounded and restricted to owned files.
7. Explicit log destinations cannot race between parent and child.
8. Failure to write logs is discoverable rather than silently ignored.
9. Tests use isolated profiles and local fixtures, not real secrets.
10. Implementation stays focused; neither a telemetry platform nor session schema migration is needed.

## Independent critique resolution

The Opus plan critic approved the replay approach and made logging conditional
on concrete amendments. Incorporated: nonfatal explicit degraded default
logging; hard failure only for explicit destination errors; HTTP capture at
the retry boundary and stream diagnostics at the consumer; strict safe-detail
allowlists; a distinct verbosity flag and no old unsafe subscriber; OS-backed
leases for rotation/collision safety; honest `/log` semantics; explicit
headless process-end recording; and real executable/local-provider fixtures.
The live two-turn replay reproduction is a required release gate when the
configured endpoint is accessible; a blocked endpoint must be reported rather
than called verified.

## Execution evidence

- BUG 2: both new local replay regressions failed on absent `summary`, then
  passed after the serializer change; all 29 Responses tests passed.
  A live isolated Astra session with encrypted-only reasoning failed its
  second turn on installed 0.1.141 and succeeded on both turns with the fix.
- YOLO: three real serve/agent/subagent fixture tests failed before mode
  wiring, then passed. A fourth verifies a built-in write outside the repo.
  Default, Plan and AcceptEdits stay restricted; live switching is covered.
- BUG 3 review found an actual headless hang: a preflight RPC error has no
  turn-complete notification, while `run_headless` waited only on notifications.
  The corrected loop also awaits its pending RPC response. Real executable
  parent/child and RPC-rejection tests failed before correction and now pass.
  This was not interactive authentication or a Windows-only test limitation.
- Writer review corrections are complete: strict bounds, exact managed names,
  stream-wide OS leases, retention at startup and rotation, meaningful safe
  verbosity, initial degraded warnings, and stop-reason normalization.
- Final independent review covered all three fixes. Its remaining Unix
  permissions requirement is implemented for newly created directories/data/
  lease files and rotation. The Unix-only permissions test is included but
  cannot run on this Windows host.
- Final observed Windows runs: 41 diagnostics tests, 5 real executable
  diagnostics tests, 255 serve tests (one intentional ignored diagnostic),
  4 TUI diagnostics tests, and 10 convention tests passed. Earlier replay
  and YOLO red/green results are recorded above. Release follows this gate.
