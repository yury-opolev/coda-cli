# Session fork + `/clear` fresh-id + rollback drill — design

**Date:** 2026-07-13
**Status:** Approved (follow-ups to the shipped session continue/resume + export/import feature, coda ff92867 / cortex 0.2.313)

## Goal

Close the three items deferred from the session continue/resume feature:

1. **`--fork`** — resume an existing session's history into a **brand-new** session id, leaving the source session untouched. Surfaced as headless `coda run --fork [id]`, TUI-startup `coda --fork [id]` / `fork [id]`, and a REPL `/fork` (branch the live conversation).
2. **`/clear` mints a fresh session id** — today `/clear` wipes history but keeps `SessionId`, so the next turn overwrites the old transcript and the append-only audit sidecar spans the clear. `/clear` should start a fresh session, freezing the pre-clear session intact and resumable.
3. **Auto-rollback drill** — add a **safe** `-RollbackDrill` switch to `scripts/Self-Update.ps1` that exercises the rollback machinery against a known-good build (no broken deploy), and fix the latent last-known-good MSIX resolution bug it exposes.

## Background / current seams

- Sessions persist per working dir at `<workdir>/.coda/sessions/<id>.json` (transcript) + `<id>.audit.jsonl` (audit sidecar). id = `Guid.NewGuid().ToString("N")[..12]`, minted in `CodaSession` ctor (`CodaSession.cs:84`) and, on import collision, `SessionBundleService.cs:137`.
- `SessionCli.ResolveAsync(workingDirectory, continueLatest, resumeId, ct)` loads a source session's messages as a `ResumeTarget(Id, Messages)`. `--continue`/`--resume` seed `history` **and** adopt the source `Id`.
- `AgentRunner` (TUI) lazily creates one `CodaSession` from `SessionState.History` + `SessionState.SessionId`; it captures a freshly-minted id back with `??=`, and on a later id change calls `AdoptSessionId(id)` (sets id without touching history).
- `HeadlessRunner` seeds `new CodaSession(..., history: seedHistory, sessionId: seedSessionId)`; a null `sessionId` mints a fresh one.
- `SessionIds` (`internal static`) centralizes the id **validity** rule but not id **creation** — the `[..12]` convention is duplicated.
- `Self-Update.ps1` rollback: after deploy, `Test-HealthAt $resolvedVersion`; on failure it restores `$lkgMsix` (snapshotted from `CortexLauncher-$fromVersion.msix`). `Get-InstalledVersion` returns a **4-part** Appx version (`0.2.313.0`) but Build-All emits the **3-part** name (`CortexLauncher-0.2.313.msix`), so the LKG snapshot misses → rollback silently unavailable.

## Design

### Fork = "seed history, don't adopt the id"

Fork reuses the existing resolution path. The only difference from resume is that the source **id is not adopted** — `SessionId` is left null so a fresh `[..12]` id is minted on the first turn (headless: `seedSessionId = null`; TUI: leave `SessionState.SessionId` null → `AgentRunner`'s `??=` mints it). The source transcript + audit are never written to. The fork's audit sidecar starts fresh (a new id, new run).

- **Headless:** `HeadlessOptions` gains `Fork` (bool) + `ForkSessionId` (string?). `--fork` with no following id → fork latest; `--fork <id>` → fork that id. Mutually exclusive with `--continue`/`--resume`. `HeadlessRunner` resolves the source via `SessionCli.ResolveAsync(workingDirectory, forkLatest, forkId, ct)`, seeds `seedHistory` from the messages, and leaves `seedSessionId = null`. A missing source fails fast (exit 1) exactly like resume. Emits a `[fork] from <id> -> new session (<n> messages)` note to stderr.
- **TUI startup:** `StartupIntent` gains `bool Fork`. `ParseStartupIntent` recognizes `--fork`/`fork` (→ fork latest) and `--fork <id>`/`fork <id>` (→ fork that id), locating the source via the existing `ContinueLatest`/`ResumeId` fields. `Program.cs` seeds `session.History` from the target but leaves `session.SessionId` null when `Fork`, printing `Forked from <id> into a new session (<n> messages).`
- **REPL `/fork`:** new `ForkCommand` — keeps `History` as-is, mints a fresh id into `SessionState.SessionId` via `SessionIds.NewId()`. `AgentRunner`'s adopt-on-change path picks up the new id next turn; the original transcript is frozen at its saved state. Prints `Forked into a new session <id> (original frozen).` Registered in `SlashCommandCatalog`.

### `/clear` mints a fresh id

`ClearCommand` already clears `History` + resets usage. Add: `context.Session.SessionId = SessionIds.NewId()`. `AgentRunner`'s existing adopt-on-change path adopts the fresh id on the next turn; because a concrete new id is assigned (not null), the branch fires. The pre-clear session's transcript + audit are frozen intact (no longer overwritten). Help/summary text updated to note it starts a fresh session. This is unconditional — `/clear` means "start fresh", and a fresh id is the correct, strictly-better behavior.

### Shared `SessionIds.NewId()`

Promote `SessionIds` to `public static` (keep `IsValid` `internal`), add:

```csharp
/// <summary>Mints a fresh session id: a 12-char lowercase-hex token from a new GUID.</summary>
public static string NewId() => Guid.NewGuid().ToString("N")[..12];
```

Rewire `CodaSession.cs:84` and `SessionBundleService.cs:137` to use it. `ClearCommand`/`ForkCommand` (in `Coda.Tui`) call the now-public helper — the single home for both the id shape and its validity rule.

### `-RollbackDrill` + LKG resolution fix (`Self-Update.ps1`)

- **`Resolve-LkgMsix($fromVersion)` helper:** resolve the last-known-good MSIX by trying, in order, `CortexLauncher-$fromVersion.msix`, then the version with a trailing `.0` stripped (`$fromVersion -replace '\.0$',''`), returning the first that exists (or `$null`). Replaces the single-form lookup at line 215 so rollback finds the 3-part artifact for a 4-part installed version.
- **`-RollbackDrill` switch:** implies `-Apply`. Deploys the (verified, known-good) target normally, then **forces** the health gate to fail — `$healthy = if ($RollbackDrill) { Say 'DRILL: forcing health-gate failure to exercise rollback'; $false } else { Test-HealthAt $resolvedVersion }` — so the rollback path runs for real: restore LKG, recreate the agent container, reinstall the LKG MSIX, and re-verify `/health` on the LKG version. Status reason is `rollback-drill`.
- **Safe usage:** run with `-SkipBuild -TargetVersion <currentInstalledVersion>` so target == LKG == current: the install is bounced but never leaves its current version, and the full rollback code path executes. Nothing broken is ever shipped.

## Testing

C# (xUnit + NSubstitute, TDD red/green):

- `SessionIds.NewId()`: 12-char lowercase hex, distinct across calls, `IsValid` true for the result.
- `ClearCommand`: after `/clear`, `SessionId` is non-null and differs from the prior id; `History` empty; usage zero.
- `ForkCommand`: `History` preserved; `SessionId` changed to a fresh valid id; message printed.
- `HeadlessOptions`: `--fork` → `Fork` true, `ForkSessionId` null; `--fork <id>` → id set; `--fork` + `--continue`/`--resume` → error.
- `SessionCli.ParseStartupIntent`: `--fork`/`fork` → `Fork` + `ContinueLatest`; `--fork <id>`/`fork <id>` → `Fork` + `ResumeId`.
- Fork seeding (integration via `SessionTranscriptStore`): forking session A yields a target whose messages equal A's but resolves to a *new* id, and A's transcript is unchanged after the fork's first save.

`Self-Update.ps1` (PowerShell, no unit harness): careful manual review + a reviewer subagent on the diff, then a **live** `-RollbackDrill` run after the feature deploy to exercise rollback end-to-end (validating the LKG fix and the rollback machinery against the running install).

## Ship

Merge the coda-cli feature to `main` via PR (per-task review + whole-branch review). Then the cortex deploy ritual: bump `lib/coda-cli` submodule pin + `version.json` (own PR), `Build-All.ps1 -CertThumbprint F578A5879BE57511D40288B6DA3A0F383BD74EEE`, `Self-Update.ps1 -Schedule -SkipBuild -TargetVersion <built>`. After the install is healthy on the new version, run `Self-Update.ps1 -Apply -RollbackDrill -SkipBuild -TargetVersion <built>` to exercise rollback live.

## Out of scope

- Copying the source audit sidecar into a fork (fork's audit starts fresh — the transcript carries the conversation; the sidecar is a per-run debug trail).
- A headless `--fork`-of-the-current-live-session (headless is single-shot; fork-of-live is the REPL `/fork`).
