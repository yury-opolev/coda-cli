# Session fork + `/clear` fresh-id + rollback drill — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `--fork`/`/fork` (resume history into a fresh session id), make `/clear` mint a fresh session id (freezing the pre-clear session), and add a safe `-RollbackDrill` switch (+ LKG-MSIX fix) to the cortex Self-Update script.

**Architecture:** Fork reuses the existing `SessionCli.ResolveAsync` load path but leaves `SessionId` null so a fresh id is minted; `/clear` and `/fork` mint via a new shared `SessionIds.NewId()`. The Self-Update drill forces the health gate to fail so the (now correctly LKG-resolving) rollback path runs against a known-good build.

**Tech Stack:** C# / .NET 10, xUnit + NSubstitute (Spectre.Console.Testing for TUI commands), PowerShell 7.

## Global Constraints

- Follow the repo C# style (`this.` on instance members, braces always, one type per file, file-scoped namespaces, `ConfigureAwait(false)` in library code, source-gen logging where applicable). `TreatWarningsAsErrors` is on.
- Sessions live at `<workdir>/.coda/sessions/<id>.{json,audit.jsonl}`. Session id shape: `Guid.NewGuid().ToString("N")[..12]` (12-char lowercase hex).
- Fork never writes to the source session. `/clear` and `/fork` never delete the pre-change transcript/audit.
- `--fork` is mutually exclusive with `--continue` and `--resume`.
- Do not change the transcript/bundle on-disk formats.

---

### Task 1: `SessionIds.NewId()` shared helper

**Files:**
- Modify: `src/Coda.Sdk/SessionIds.cs`
- Modify: `src/Coda.Sdk/CodaSession.cs:84`
- Modify: `src/Coda.Sdk/SessionBundleService.cs:137`
- Test: `tests/Engine.Tests/SessionIdsTests.cs` (create)

**Interfaces:**
- Produces: `public static string SessionIds.NewId()` — 12-char lowercase-hex id. `SessionIds` becomes `public static`; `IsValid` stays `internal`.

- [ ] **Step 1: Write failing tests** in `tests/Engine.Tests/SessionIdsTests.cs`:

```csharp
using Coda.Sdk;

namespace Engine.Tests;

public sealed class SessionIdsTests
{
    [Fact]
    public void NewId_is_12_char_lowercase_hex()
    {
        var id = SessionIds.NewId();
        Assert.Equal(12, id.Length);
        Assert.Matches("^[0-9a-f]{12}$", id);
    }

    [Fact]
    public void NewId_is_distinct_across_calls()
    {
        Assert.NotEqual(SessionIds.NewId(), SessionIds.NewId());
    }
}
```

`SessionIds.IsValid` is `internal`; `Engine.Tests` already sees `Coda.Sdk` internals via the existing `InternalsVisibleTo` (confirm; `SessionAuditStoreTests` exercises internal types). If `NewId` is public this test compiles regardless.

- [ ] **Step 2: Run tests, verify they fail** (`SessionIds` not public / `NewId` missing):

Run: `dotnet test lib/coda-cli/tests/Engine.Tests --filter "FullyQualifiedName~SessionIdsTests"`
Expected: FAIL (compile error or missing method).

- [ ] **Step 3: Implement.** In `SessionIds.cs` change `internal static class SessionIds` → `public static class SessionIds`, keep `IsValid` as `internal static`, and add:

```csharp
/// <summary>Mints a fresh session id: a 12-char lowercase-hex token from a new GUID.</summary>
public static string NewId() => Guid.NewGuid().ToString("N")[..12];
```

Rewire `CodaSession.cs:84` `this.SessionId = sessionId ?? Guid.NewGuid().ToString("N")[..12];` → `this.SessionId = sessionId ?? SessionIds.NewId();` and `SessionBundleService.cs:137` `? Guid.NewGuid().ToString("N")[..12]` → `? SessionIds.NewId()`.

- [ ] **Step 4: Run tests, verify pass.** Run the same filter. Expected: PASS.
- [ ] **Step 5: Commit** `feat(sdk): add SessionIds.NewId() shared id factory`.

---

### Task 2: `/clear` mints a fresh session id

**Files:**
- Modify: `src/Coda.Tui/Commands/ClearCommand.cs`
- Test: `tests/Coda.Tui.Tests/ClearCommandTests.cs` (create)

**Interfaces:**
- Consumes: `SessionIds.NewId()` (Task 1); `SessionState.SessionId`.

- [ ] **Step 1: Write failing tests** in `tests/Coda.Tui.Tests/ClearCommandTests.cs`. Mirror `ResumeRewindCommandTests.BuildContext` (TestConsole + InMemoryTokenStore + ClaudeAiProvider + SessionState over a temp dir). Register `new ClearCommand()` in the test registry.

```csharp
[Fact]
public async Task Clear_mints_a_fresh_session_id_when_one_exists()
{
    var (_, context) = this.BuildContext();
    context.Session.SessionId = "old-session-id";
    context.Session.History.Add(new ChatMessage(ChatRole.User, [new TextBlock("hi")]));

    await new ClearCommand().ExecuteAsync(context, Array.Empty<string>());

    Assert.NotNull(context.Session.SessionId);
    Assert.NotEqual("old-session-id", context.Session.SessionId);
    Assert.Empty(context.Session.History);
}

[Fact]
public async Task Clear_assigns_a_valid_fresh_id_even_from_null()
{
    var (_, context) = this.BuildContext();
    context.Session.SessionId = null;

    await new ClearCommand().ExecuteAsync(context, Array.Empty<string>());

    Assert.NotNull(context.Session.SessionId);
    Assert.Matches("^[0-9a-f]{12}$", context.Session.SessionId);
}
```

- [ ] **Step 2: Run tests, verify they fail** (SessionId unchanged today). Run: `dotnet test lib/coda-cli/tests/Coda.Tui.Tests --filter "FullyQualifiedName~ClearCommandTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** In `ClearCommand.ExecuteAsync`, after `context.Session.SessionUsage = TokenUsage.Zero;` add `context.Session.SessionId = Coda.Sdk.SessionIds.NewId();`. Update `Summary`/`Help` text to note it starts a fresh session (pre-clear session preserved). Keep it before the console clear/banner render.
- [ ] **Step 4: Run tests, verify pass.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(tui): /clear starts a fresh session id (freeze pre-clear session)`.

---

### Task 3: REPL `/fork` command

**Files:**
- Create: `src/Coda.Tui/Commands/ForkCommand.cs`
- Modify: `src/Coda.Tui/Repl/SlashCommandCatalog.cs`
- Test: `tests/Coda.Tui.Tests/ForkCommandTests.cs` (create)

**Interfaces:**
- Consumes: `SessionIds.NewId()`; `SessionState.History`, `SessionState.SessionId`.
- Produces: `ForkCommand : ISlashCommand`, `Name => "fork"`.

- [ ] **Step 1: Write failing tests** in `tests/Coda.Tui.Tests/ForkCommandTests.cs` (same BuildContext pattern; register `new ForkCommand()`):

```csharp
[Fact]
public async Task Fork_keeps_history_and_mints_a_fresh_id()
{
    var (console, context) = this.BuildContext();
    context.Session.SessionId = "source-id";
    context.Session.History.AddRange(
    [
        new ChatMessage(ChatRole.User, [new TextBlock("q")]),
        new ChatMessage(ChatRole.Assistant, [new TextBlock("a")]),
    ]);

    var result = await new ForkCommand().ExecuteAsync(context, Array.Empty<string>());

    Assert.False(result.ShouldExit);
    Assert.Equal(2, context.Session.History.Count);          // history preserved
    Assert.NotNull(context.Session.SessionId);
    Assert.NotEqual("source-id", context.Session.SessionId); // fresh id
    Assert.Matches("^[0-9a-f]{12}$", context.Session.SessionId);
    Assert.Contains("Forked", console.Output);
}
```

- [ ] **Step 2: Run tests, verify they fail** (no ForkCommand). Run: `dotnet test lib/coda-cli/tests/Coda.Tui.Tests --filter "FullyQualifiedName~ForkCommandTests"`. Expected: FAIL (compile).
- [ ] **Step 3: Implement `ForkCommand`.** Model on `ClearCommand`/`ResumeCommand` (implement `ISlashCommand`: `Name => "fork"`, `Aliases => []`, `Summary`, `Help`). Body: mint `context.Session.SessionId = Coda.Sdk.SessionIds.NewId();` (do **not** touch `History`), print `[grey50]Forked into a new session {id} (original frozen).[/]` (escape the id via `Markup.Escape`). Return `CommandResult.Continue`. Register `new ForkCommand()` in `SlashCommandCatalog.CreateAll()` next to `ResumeCommand`.
- [ ] **Step 4: Run tests, verify pass.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(tui): add /fork to branch the live conversation into a new session`.

---

### Task 4: Headless `--fork [id]`

**Files:**
- Modify: `src/Coda.Sdk/HeadlessOptions.cs`
- Modify: `src/Coda.Tui/HeadlessRunner.cs`
- Test: `tests/Engine.Tests/HeadlessOptionsResumeTests.cs` (extend)

**Interfaces:**
- Consumes: `SessionCli.ResolveAsync`.
- Produces: `HeadlessOptions.Fork` (bool), `HeadlessOptions.ForkSessionId` (string?).

- [ ] **Step 1: Write failing tests** appended to `HeadlessOptionsResumeTests.cs`:

```csharp
[Fact]
public void Parses_fork_flag_without_id()
{
    Assert.True(HeadlessOptions.TryParse(["-p", "go", "--fork"], out var o, out _));
    Assert.True(o.Fork);
    Assert.Null(o.ForkSessionId);
}

[Fact]
public void Parses_fork_with_id()
{
    Assert.True(HeadlessOptions.TryParse(["-p", "go", "--fork", "abc123"], out var o, out _));
    Assert.True(o.Fork);
    Assert.Equal("abc123", o.ForkSessionId);
}

[Fact]
public void Fork_with_continue_is_an_error()
{
    Assert.False(HeadlessOptions.TryParse(["-p", "go", "--fork", "--continue"], out _, out var err));
    Assert.NotNull(err);
}

[Fact]
public void Fork_with_resume_is_an_error()
{
    Assert.False(HeadlessOptions.TryParse(["-p", "go", "--fork", "x", "--resume", "y"], out _, out var err));
    Assert.NotNull(err);
}
```

Note: `--fork` takes an **optional** id — consume the next arg as the id only when it exists and does not start with `-`. So `["--fork", "--continue"]` parses `--fork` with no id, then `--continue`; the mutual-exclusion check then fails. `["--fork", "abc123"]` consumes `abc123` as the id.

- [ ] **Step 2: Run tests, verify they fail.** Run: `dotnet test lib/coda-cli/tests/Engine.Tests --filter "FullyQualifiedName~HeadlessOptionsResumeTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** In `HeadlessOptions`: add `public bool Fork { get; init; }` and `public string? ForkSessionId { get; init; }` (XML-documented). In `TryParse`: add locals `var fork = false; string? forkSessionId = null;`. Add a case:

```csharp
case "--fork":
    fork = true;
    if (i + 1 < args.Count && !args[i + 1].StartsWith('-')) { forkSessionId = args[++i]; }
    break;
```

Extend the mutual-exclusion validation: it is an error if more than one of {`continueSession`, `resumeSessionId is not null`, `fork`} is set. Assign `Fork = fork, ForkSessionId = forkSessionId` in the returned options. Update the `TryParse` doc-comment and the `HeadlessRunner` usage string to include `[--fork [id]]`.

In `HeadlessRunner.RunAsync`, extend the seed block:

```csharp
List<ChatMessage>? seedHistory = null;
string? seedSessionId = null;
if (options.Continue || options.ResumeSessionId is not null || options.Fork)
{
    var continueLatest = options.Continue || (options.Fork && options.ForkSessionId is null);
    var lookupId = options.Fork ? options.ForkSessionId : options.ResumeSessionId;
    var target = await SessionCli.ResolveAsync(workingDirectory, continueLatest, lookupId, cancellationToken).ConfigureAwait(false);
    if (target is null)
    {
        Console.Error.WriteLine(options.Fork
            ? "No session to fork in this directory."
            : options.Continue ? "No session to continue in this directory." : $"Session '{options.ResumeSessionId}' not found.");
        return 1;
    }

    seedHistory = [.. target.Messages];
    // Fork seeds history but NOT the id: leave seedSessionId null so a fresh id is minted.
    seedSessionId = options.Fork ? null : target.Id;
    if (options.Fork) { Console.Error.WriteLine($"[fork] from {target.Id} -> new session ({target.Messages.Count} messages)"); }
}
```

- [ ] **Step 4: Run tests, verify pass.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(headless): add coda run --fork [id] (seed history into a fresh session)`.

---

### Task 5: TUI startup `--fork [id]` / `fork [id]`

**Files:**
- Modify: `src/Coda.Tui/SessionCli.cs`
- Modify: `src/Coda.Tui/Program.cs`
- Test: `tests/Coda.Tui.Tests/SessionCliTests.cs` (extend)

**Interfaces:**
- Consumes: `SessionCli.ResolveAsync`, `SessionState`.
- Produces: `StartupIntent.Fork` (bool). `ParseStartupIntent` recognizes fork.

- [ ] **Step 1: Write failing tests** appended to `SessionCliTests.cs`:

```csharp
[Fact]
public void ParseStartupIntent_fork_no_id_forks_latest()
{
    var intent = SessionCli.ParseStartupIntent(["--fork"]);
    Assert.True(intent.Fork);
    Assert.True(intent.ContinueLatest);
    Assert.Null(intent.ResumeId);
    Assert.True(intent.HasIntent);
}

[Fact]
public void ParseStartupIntent_fork_with_id()
{
    var intent = SessionCli.ParseStartupIntent(["fork", "pick-me"]);
    Assert.True(intent.Fork);
    Assert.False(intent.ContinueLatest);
    Assert.Equal("pick-me", intent.ResumeId);
}

[Fact]
public void ParseStartupIntent_resume_is_not_fork()
{
    var intent = SessionCli.ParseStartupIntent(["--resume", "abc"]);
    Assert.False(intent.Fork);
    Assert.Equal("abc", intent.ResumeId);
}
```

- [ ] **Step 2: Run tests, verify they fail.** Run: `dotnet test lib/coda-cli/tests/Coda.Tui.Tests --filter "FullyQualifiedName~SessionCliTests"`. Expected: FAIL.
- [ ] **Step 3: Implement.** In `SessionCli.cs`:
  - `StartupIntent` record → add `bool Fork` as a trailing param with default: `public sealed record StartupIntent(bool ContinueLatest, string? ResumeId, bool Fork = false)`. `HasIntent` unchanged (fork always sets `ContinueLatest` or `ResumeId`).
  - In `ParseStartupIntent`, add cases before `default`:

```csharp
case "-f" or "--fork" or "fork":
    var forkHasId = args.Count > 1 && !args[1].StartsWith('-');
    return forkHasId ? new StartupIntent(false, args[1], Fork: true) : new StartupIntent(true, null, Fork: true);
```

  In `Program.cs`, in the `startupIntent.HasIntent` block, branch on `Fork`:

```csharp
if (target is not null)
{
    session.History.AddRange(target.Messages);
    if (startupIntent.Fork)
    {
        // Fork: seed history but leave SessionId null so a fresh id is minted on the first turn.
        console.MarkupLine($"[grey50]Forked from {Spectre.Console.Markup.Escape(target.Id)} into a new session ({target.Messages.Count} messages).[/]");
    }
    else
    {
        session.SessionId = target.Id;
        console.MarkupLine($"[grey50]Resumed session {Spectre.Console.Markup.Escape(target.Id)} ({target.Messages.Count} messages).[/]");
    }
}
else
{
    console.MarkupLine(startupIntent.Fork ? "[grey50]No session to fork.[/]" : "[grey50]No session to continue.[/]");
}
```

- [ ] **Step 4: Run tests, verify pass.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(tui): add coda --fork [id] / fork [id] startup (fork into a fresh session)`.

---

### Task 6: `-RollbackDrill` switch + LKG-MSIX fix (cortex-agent)

> This task modifies the **parent** cortex-agent repo (`scripts/Self-Update.ps1`), not coda-cli. PowerShell has no unit harness here — verify by careful review + a live drill after deploy. Handle it directly (not via a C# TDD subagent).

**Files:**
- Modify: `scripts/Self-Update.ps1`

- [ ] **Step 1: Add a `Resolve-LkgMsix` helper** that, given `$fromVersion`, returns the first existing of `CortexLauncher-$fromVersion.msix` and `CortexLauncher-$($fromVersion -replace '\.0$','').msix` under `$artifacts`, else `$null`.
- [ ] **Step 2: Use it in the snapshot step** (replace the `$prevMsix = Join-Path ...` line): `$prevMsix = Resolve-LkgMsix $fromVersion`; keep the `if ($prevMsix -and (Test-Path $prevMsix))` guard and the existing "MSIX rollback unavailable" fallback.
- [ ] **Step 3: Add the `-RollbackDrill` switch** to `param(...)`; near the top set `if ($RollbackDrill) { $Apply = $true }`. Document it in the comment-based help.
- [ ] **Step 4: Force the health gate under drill** at the runtime-verify step: `$healthy = if ($RollbackDrill) { Say 'DRILL: forcing health-gate failure to exercise rollback' 'Yellow'; $false } else { Test-HealthAt $resolvedVersion }` and branch on `$healthy` instead of calling `Test-HealthAt` inline. Set the rollback status reason to `rollback-drill` when `$RollbackDrill`.
- [ ] **Step 5: Review.** Dispatch a reviewer subagent on the `Self-Update.ps1` diff (ops/rollback safety: no accidental version change under the recommended `target==current` drill; LKG resolution correct for 3- and 4-part versions; drill implies Apply; concurrency lock intact).
- [ ] **Step 6: Commit** on the cortex-agent side during the deploy phase (parent repo, via PR).

## Self-Review

- Spec coverage: fork (headless T4, TUI-startup T5, REPL T3), `/clear` fresh id (T2), shared id (T1), rollback drill + LKG fix (T6). ✓
- Type consistency: `SessionIds.NewId()` (T1) consumed by T2/T3; `StartupIntent.Fork` (T5); `HeadlessOptions.Fork`/`ForkSessionId` (T4). ✓
- No placeholders; every code step shows the code. ✓
