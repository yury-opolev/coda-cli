# Provider Authentication Parity Implementation Plan

> **For agentic workers:** Use subagent-driven-development or executing-plans.
> Each behavior change requires an observed failing regression, its fix, and
> the corresponding green result. Do not copy speculative implementations
> into the repository.

**Goal:** Restore .NET Coda's working provider setup, login, switching, status
and logout in Rust, including secure API-key entry and usable host-side CLI
commands.

**Architecture:** Provider authentication is host-local maintenance. Reuse
`coda-auth` for both trusted-local TUI flows and a TUI-free command runner
shared by `coda` and `coda-engine`. The engine and authentication commands
must use the same credential storage and provider-selection rules. A custom
engine connection must not authorize edits to the frontend's credentials.

**Tech stack:** Existing Rust workspace, Tokio, reqwest, clap, serde,
`coda-auth` providers/stores and the TUI's widget/surface toolkit. A small
echo-off console-input dependency is permitted if needed; `coda-engine`
must remain free of ratatui, crossterm, clipboard and rendering dependencies.

**Status:** Revised after independent Opus criticism and independently
approved. The earlier large copy-code draft is superseded: its storage
heuristics, plaintext console input and mutation ordering were unsafe.
Tasks 1-7 are implemented and their stage reviews are complete: storage,
provider primitives, the shared authentication service, both host CLIs,
command-to-engine conformance, and TUI authentication with engine-less
first-run setup. The core configuration consistency prerequisite is also
complete. Task 8's full milestone review is approved, including the final
transport-disconnection and authoritative prompt-recovery corrections.
The documented Debug and Release acceptance gates are complete; packaging
and release are the remaining operational steps. The task checklists below
retain the original implementation requirements.

## 1. Scope and binding decisions

- Support `claude-ai`, `github-copilot`, and `anthropic-api-key`. Preserve
  existing aliases and the engine's `anthropic` identifier without rewriting
  existing per-provider model preferences into incompatible keys.
- Restore `/login [provider]`, `/logout [provider]`, `/provider [provider]`
  and `/setup`. `/login` and `/provider` share the connection flow.
- Add `coda auth login|logout|status` and identical `coda-engine auth`
  commands. Login without a provider offers a picker only on a terminal.
- Support Copilot public/enterprise device authorization, Claude browser
  loopback PKCE, and masked Anthropic API-key entry into secure storage.
  Explicit `--api-key-stdin` accepts piped input. Never accept a literal key
  as a command-line argument or tell users to type it with echo enabled.
- Preserve environment-key support. Status and logout must say when an
  environment credential still applies. Logout does not revoke credentials
  at the provider, change the parent shell, or erase other processes' memory.
- Preserve the existing single-saved-provider policy. Explain replacement
  before completing a switch; cancellation or failed authentication must not
  remove the existing connection. Environment-only selection must validate
  its credential before removing another saved connection.
- An explicit provider always wins and never falls back to another account.
  A successfully saved user provider choice must select that provider on
  subsequent normal launches. Unconfigured startup may use an environment
  key or the sole stored provider. Status must use the same resolver or
  state the inputs without claiming which provider a running engine uses.
- Provider authentication remains local, not an OAuth-over-RPC service.
  ApiOnly commands explain how to authenticate on the engine host, without
  probing or changing this machine's credentials. Do not print this refusal
  on every healthy remote startup.
- Reject connection-changing commands during an active turn, rather than
  implicitly interrupting work. Do not silently discard pending drafts.
- Keep normal `coda serve --provider ...` fail-closed when credentials are
  missing. The trusted-local interactive launcher may perform authentication
  preflight before spawning that child. Do not weaken the serve contract
  merely to make a wizard reachable.
- Setup may verify with authenticated live model discovery rather than a
  paid completion. Require actual `source: "live"` evidence, not a catalog
  fallback. A saved OAuth token whose model probe fails is "signed in,
  connection not verified", never "Setup complete" or a guessed auth failure.
- A new login may select an explicit public/enterprise deployment over saved
  or ambient domain defaults. Effective endpoint overrides must be disclosed;
  never authenticate against a different tenant than the UI names.
- Do not commit, bump a version or install before the final milestone gate.
  Preserve all existing dirty work on `feat/serve-api-contract`.

## 2. Existing behavior and verified blockers

The legacy reference is `src/Coda.Tui/Commands/LoginCommand.cs`,
`LogoutCommand.cs`, `ProviderCommand.cs`, `SetupCommand.cs`,
`src/Coda.Tui/Setup/SetupWizard.cs` and `FirstRunDetector.cs`.

Its real behavior is provider picker -> provider-specific login -> active
provider/model update. Copilot offers public/enterprise selection and a URL/
device-code panel. Claude uses browser loopback PKCE. Setup verifies the
connection; there is no actual model-selection step despite old help text.
Status reads stored metadata without refreshing credentials. The old API-key
flow was environment-only, and its logout message was misleading; do not
preserve those limitations as requirements.

Verified Rust gaps:

| Area | Source | Required correction |
|---|---|---|
| Store selection | `coda-serve/src/host.rs::credential_store`, `coda-auth/src/store/mod.rs` | DPAPI selection currently depends only on a Copilot file; deleting a credential can switch backends. |
| Format collisions | `coda-auth/src/store/{dpapi,encrypted_file}.rs` | Both backends can target the same `.cred` paths. Never overwrite one format using the other. |
| Legacy compatibility | `.NET FileTokenStore` and Rust encrypted-file store | Do not assume identical filenames, key protection or encrypted layouts across OSes. Verify codecs before reading/migrating. |
| Refresh/logout | `coda-auth/src/manager.rs` | Refresh may resurrect a removed credential through stale fallback/persist; separate manager instances must not bypass coordination. |
| Claude login | `coda-auth/src/provider/claude_ai.rs::begin_login` | Listener is bound then dropped; the flow must own it until completion/cancellation. |
| Callback messaging | `coda-auth/src/loopback.rs` | Do not announce successful sign-in before state validation and token exchange. |
| Stored API key | `coda-serve/src/host.rs::build_client_for_provider` | Anthropic currently reads explicit/environment keys but not its stored key. |
| Automatic selection | `host.rs::try_build_client_with_diagnostic` | Only explicit key, environment and Copilot are considered; saved Claude must also work. |
| Diagnostics | `coda-auth/src/error.rs` | Raw OAuth bodies and URLs are unsafe for new UI/CLI error output. Reuse safe classification. |
| Enterprise input | `coda-auth/src/provider/copilot.rs::for_enterprise` | Byte slicing can panic on non-ASCII input; validate before network activity. |

## 3. Storage contract

Use a deterministic, profile-scoped primary backend: DPAPI on Windows and an
encrypted-file backend on other hosts. Never select a backend by counting
credential files or by the presence of one provider's credential.

Existing .NET Windows DPAPI files must remain directly usable. Earlier Rust
keyring/encrypted-file credentials need an explicit, tested compatibility
path. Do not silently treat a legacy credential as absent because it lives
in another backend. If a format cannot be identified or safely imported,
return an actionable error without changing it.

Requirements for the storage implementation:

1. Separate new encrypted-file storage from DPAPI paths. Detect an
   incompatible existing directory/key before any write.
2. Missing is distinct from permission, decryption, corruption and malformed
   key errors. Never replace an invalid existing encryption key.
3. Validate any explicit backend override; unknown values are errors, not
   instructions to probe a different store.
4. Honor isolated profiles before any legacy/keyring access. Explicit
   `CODA_HOME` test profiles must never consult the developer's global store.
5. Preserve all unknown/non-provider keys, including MCP credentials.
6. Successful adoption/import writes the primary copy before retiring the
   legacy copy. Logout must not reveal an old fallback credential again.
7. The engine, auth CLI, TUI maintenance and MCP resolver share the factory.
   Do not create a fresh incompatible manager/store inside each operation.
8. Coordinate cooperative Rust refresh/persist/logout across manager
   instances and processes using the same profile. A late refresh must
   re-check the current credential under the same commit coordination before
   persisting. No stale-value fallback after a deletion.

The implementation must test actual file backends, not only fabricated
presence counts. Cross-platform legacy cases must use verified fixtures,
not a claim that all `.cred` files are interchangeable.

## 4. Authentication transaction and UI ownership

Use a two-phase transition with one owner:

```text
choose provider/deployment and explain replacement
  -> prepare login (network + ephemeral challenges; no profile mutation)
  -> authenticate successfully
  -> stop and await the TUI's owned old engine
  -> commit credential + provider choice + deployment consistently
  -> start a fresh engine and resume via the public session API
  -> read actual provider/model and verify connection
```

The prepared result contains secrets and must have redacted formatting.
Cancellation before commit leaves credentials, settings and the current
engine unchanged. Do not store authorization URLs/device codes in transcript
notices, command history, diagnostic logs, replay buffers or clipboard.

The commit owner retains enough previous state to roll back a failed
credential/settings replacement. If rollback itself fails, report exactly
which operation failed in safe terms and do not claim the old or new
connection is restored. Never silently launch an engine against an
inconsistent credential/deployment pair.

For logout, stop and await the owned child before deleting credentials.
Then reconnect only to a genuinely credential-free or explicitly
environment-authenticated state, labelled honestly. Never kill an unowned
engine or another user's session.

The auth flow runs outside the UI event loop's blocking request path. Give
device/browser flows their own cancellable lifetime, not the 20-second
metadata timeout. Escape/Ctrl-C cancels the flow and releases its listener,
poller and any outstanding form response. Closing the application performs
the same cleanup.

Use existing forms and exclusive surfaces. Honor a rejected surface push;
surface-stack contention must produce a visible refusal, not silent
cancellation. Keep challenge URLs/codes in the ephemeral auth surface.

## 5. Implementation sequence

Each task: add the stated regression first, observe its relevant failure,
implement only that behavior, then run the targeted Cargo command. Give the
implementer current code context; interface names below describe ownership,
not permission to bypass existing helpers.

### Task 1: Stable storage and compatible profile access

Files: `coda-auth/src/store/{mod,factory,dpapi,encrypted_file,keyring}.rs`,
`coda-auth/src/manager.rs`; engine/MCP factory call sites in `coda-serve`.

- [ ] Prove a fresh profile chooses the same backend before login, after
  provider replacement, after logout and after process restart.
- [ ] Prove legacy .NET DPAPI credentials remain readable in a temporary
  Windows profile, for Claude and API-key keys as well as Copilot.
- [ ] Prove incompatible formats, invalid key lengths, inaccessible files
  and invalid overrides fail without rewriting files or choosing another
  backend.
- [ ] Prove isolated profiles never probe the global keyring or legacy home.
- [ ] Prove adopting earlier Rust credentials and subsequent logout cannot
  resurrect a fallback copy; preserve unrelated MCP entries.

Run from `rust`: `cargo test -p coda-auth store`.

### Task 2: Refresh, login and logout coordination

Files: `coda-auth/src/manager.rs` and storage commit coordination from Task 1.

- [ ] Hold a fake refresh at a barrier, logout through another manager using
  the same store, release refresh, and prove no credential is restored.
- [ ] Repeat with replacement by a different provider; prove the new
  credential and single-provider invariant survive.
- [ ] Cover the same race with cooperating child processes on an isolated
  file-backed profile; no sleeps as the correctness barrier.
- [ ] Keep one reusable manager in the auth service. No per-call manager
  construction that discards the mutation gate.
- [ ] Preserve existing single-flight refresh behavior and typed errors.

Run: `cargo test -p coda-auth manager`.

### Task 3: Complete provider flows and safe errors

Files: `coda-auth/src/provider/{claude_ai,copilot,api_key}.rs`,
`loopback.rs`, `error.rs`; shared endpoint resolver in `coda-serve/settings.rs`.

- [ ] Claude: a real loopback callback succeeds while the flow remains alive;
  mismatched state, missing code, OAuth error, timeout and cancellation do
  not store a credential or display successful sign-in.
- [ ] Copilot: pending/slow-down/success/denial/expiry/cancel work against a
  loopback HTTP fixture; honor `use_exchange` and validate token responses.
- [ ] Explicit public/enterprise choice wins over domain defaults; canceled
  or failed login changes no saved domain. Non-ASCII input never panics.
- [ ] API-key creation rejects empty input; later verification distinguishes
  authentication failure from unavailable model discovery.
- [ ] Shared public errors never contain injected tokens, raw server bodies,
  authorization codes or credential-bearing URLs.

Run: `cargo test -p coda-auth provider`, then relevant `loopback` tests.

### Task 4: Shared auth service and consistent provider selection

Files: new focused `coda-auth/src/service/` modules, shared settings/profile
helpers as needed; `coda-serve/src/host.rs` credential construction.

- [ ] Implement prepare/commit/status/logout ownership from sections 3-4.
- [ ] Status reports all discovered provider entries and inconsistencies;
  it does not stop at the first credential or refresh merely to show status.
- [ ] Explicit provider selection fails closed. Saved user choice, ambient
  key availability and stored provider selection agree between CLI and engine.
- [ ] Keep account identity separate from HTTP transport: `claude-ai` remains
  the engine/settings identity for a Claude subscription, while the stored
  `anthropic-api-key` identity maps to engine `anthropic`. A shared Anthropic
  transport must not collapse these accounts or their model preferences.
- [ ] A saved explicit choice with missing credentials reports `NeedsLogin`;
  it must not fall through to an ambient key or another stored provider.
- [ ] Engine startup consumes the stored API key and Claude credential.
- [ ] A failed profile-settings commit restores prior credential/deployment
  state, or reports an explicit restoration failure.
- [ ] Transaction rollback also preserves legacy-retirement metadata.
  Use an explicit metadata restoration operation rather than deleting a
  retirement key through the ordinary credential-deletion path, which would
  manufacture another retirement marker.
- [ ] Preserve existing model aliases/preferences and fetch the actual
  active model after engine replacement.
- [ ] Widen `CredentialSource::auth_headers` to a Result-based seam. A
  manager-backed missing/removed credential or store/refresh error must stop
  the request, not fall back to an old static token. Preserve generic
  `Ok(None)` only as an explicitly documented no-override result.
- [ ] Validate the provider identity of a loaded credential before invoking
  any provider refresh or constructing headers, not merely when persisting it.
- [ ] Verification uses `refresh_models()` to bypass provider-client caches,
  and never counts catalog fallback or a warm cache as a fresh auth probe.

Run: focused `cargo test -p coda-auth service` and credential-selection
tests in `coda-serve`.

### Task 5: Headless commands and safe input/browser launching

Files: `coda-boot/src/auth_cli.rs`, shared browser-opening helper,
`coda/src/main.rs`, `coda-engine/src/main.rs`, affected Cargo manifests.

- [ ] Both binaries implement `auth status`, `auth login [provider]` and
  `auth logout [provider]`, preserving existing bare/serve/run parsing.
- [ ] TTY API-key input is masked. Non-TTY requires explicit
  `--api-key-stdin`; keys never appear in argv, stdout or diagnostics.
- [ ] Reject provider-inapplicable options instead of ignoring them:
  `--public`/`--enterprise-domain` are Copilot-only; `--use-env` and
  `--api-key-stdin` are API-key-only and mutually exclusive.
- [ ] Use consistent exit codes: 0 success, 1 operational failure,
  2 invalid usage (clap convention), 130 user cancellation.
- [ ] Open browser URLs without a shell interpreting `&`, quotes or other
  URL characters. One helper serves both hosts; test actual platform argv
  or direct API composition. If opening fails, display a safe URL fallback.
- [ ] Status says environment auth remains available after stored logout.
- [ ] Verify `cargo tree -p coda-engine -e normal` remains TUI-free using
  the existing independence test.

Run: targeted CLI unit tests in `coda-boot`, `coda`, `coda-engine`.

### Task 6: Real command-to-engine conformance

Files: `coda/tests/auth_cli.rs`, `coda-engine/tests/` and existing shared
hermetic harnesses.

- [ ] Run the actual auth command to store a fake-provider credential,
  start a fresh real engine, and observe an authenticated request at the
  local fake provider.
- [ ] Logout through the actual command, start another engine, and prove
  it cannot authenticate using the removed credential.
- [ ] Repeat with an inherited environment key: report its continued
  availability explicitly, never claim a global sign-out.
- [ ] On Windows run production DPAPI variants with temporary profiles,
  not only a forced file-backend test.
- [ ] Seed verified .NET-format DPAPI data and prove the Rust engine reads
  it. Verify malformed/inaccessible profiles fail without fallback.
- [ ] No test may read real credentials, contact a real provider, silently
  skip its required binary, or reuse a stale binary.

Run: `cargo test -p coda --test auth_cli` plus the corresponding
`coda-engine` target.

### Task 7: TUI login/setup/logout and first-run wiring

Files: `coda-tui/src/local/auth.rs`, focused `app/auth.rs`,
existing form/surface modules, `app/slash/mod.rs`, `app/slash/config.rs`,
`setup.rs`, `commands.rs`, and launch preflight in the unified CLI.

- [ ] Unify ownership of the original child and restarted children. Today the
  CLI caller retains the original `Engine`, while `App::run` owns only later
  restarts; an auth transition must be able to stop and await either one
  before committing credentials, not merely send `shutdown` or start another
  child alongside it.
- [ ] An intentional auth disconnect must not trigger the normal
  inbound-EOF application exit or a busy loop on a closed receiver. Retain
  the conversation and local auth commands while logged out; disable engine
  recovery polling and prompt submission until a new connection is adopted.
- [ ] ApiOnly refuses before any local store/settings access, naming the
  engine-host command. Healthy remote startup stays quiet.
- [ ] TrustedLocal slash commands drive the same service as the CLI.
  Provider/deployment/API-key fields use shared widgets and exclusive
  ephemeral surfaces with working keyboard cancellation.
- [ ] Existing credential/store errors do not become "first run".
- [ ] First-run setup is reachable before a selected-but-unconfigured
  engine would exit; ordinary headless serve remains fail-closed.
- [ ] Successful switching stops the old owned child before commit,
  resumes the conversation through public APIs, preserves pending/unsent
  text, clears cached config and shows the actual new provider/model.
- [ ] Rebuild launch overrides deliberately. Old `--provider` arguments,
  duplicate child environment values and `env_remove` entries must not undo
  the newly selected provider. Explicit Copilot deployment choices must also
  survive child launch when inherited domain defaults disagree; never send
  the new tenant's token to the old tenant.
- [ ] Failed/canceled login does not report success or clear the current
  conversation. Logout cannot leave the owned old child spending its token.
- [ ] If credential commit succeeds but the replacement engine cannot start,
  report "credentials saved; engine startup failed", not completed switching
  or an authentication failure. Retain the session ID and recoverable drafts.
- [ ] Setup treats a catalog fallback as unverified, not completed.
- [ ] App teardown cancels outstanding auth operations without late
  persistence. Keep the established `run -> finish -> close_out` cleanup.
- [ ] Keep `app/mod.rs` within the existing size convention; put auth
  orchestration and tests in focused siblings.

Run: targeted TUI auth/surface tests and real startup tests, followed by
`cargo test -p coda-tui -p coda-client` and `coda/tests/tui_client`.

### Task 8: Documentation, schemas and final acceptance

Files: `rust/README.md`, `docs/protocol/catalog.md`,
`docs/serve-protocol.md`, `coda-serve/src/capabilities.rs`.

- [ ] Document actual setup/login/logout commands, sources, provider
  replacement, verification states and cancellation behavior.
- [ ] Explicitly advertise provider-auth RPCs unsupported; do not confuse
  provider authentication with remote control-client authentication.
- [ ] Include any new shared wire DTOs in the pending schema/catalog work;
  never generate public schemas for secret-bearing stored credentials.
- [ ] Delete stale help claiming auth is unwired, or claiming an absent
  command exists. Preserve the separate legacy C# protocol documentation.
- [ ] Run full workspace acceptance and powerful independent review of
  the entire accumulated milestone diff, including auth and packaging.
- [ ] Fix important findings before commit, release or installation.
  If mutation testing touched `coda-serve`, clean it before the final
  relevant real-binary rebuild.

## 6. Explicit exclusions

No provider-auth RPC, browser relay, remote control authorization, account
fleet management, provider-side token revocation, OpenAI/new provider
integration, MCP OAuth changes or multi-account credential vault.
Claude manual-paste mode is not required for .NET TUI parity; do not
advertise it unless actually implemented and covered.

Other running programs may retain tokens. Local logout must prevent this
application's managed child and cooperative refresh paths from restoring
deleted credentials, but must not claim revocation of unowned processes.

## 7. Acceptance evidence

The handoff must record real red/green outcomes, not just test names:
fresh-profile setup -> actual engine request -> provider switch -> actual
new-provider request -> logout -> no stored-auth request on next launch.
Repeat canceled and failed transitions without losing the existing
connection. Include Windows DPAPI compatibility, enterprise/public
selection, masked input/redaction, and ApiOnly no-local-write evidence.
