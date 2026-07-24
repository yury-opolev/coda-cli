# CLI, TUI, and MCP Improvements Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate the exact-prompt, transcript-navigation, aggregated-summary, and interactive-MCP workstreams into one coherent delivery, resolve overlapping contracts, update documentation, run complete verification, obtain a strongest-model holistic review, fix accepted findings, and issue a final spec-compliance verdict.

**Architecture:** Each subsystem plan remains independently executable and owns its focused tests/commits. Integration adds only cross-subsystem contract tests and surgical conflict reconciliation around shared persistence, resume, event/reducer, shell composition, and documentation files; it then runs one complete Release build/test cycle before and after the holistic review.

**Tech Stack:** C# 14, .NET 10, xUnit, Terminal.Gui 2.x, JSON session/audit/serve protocols, Git, strongest available code-review model.

---

## Required subsystem inputs and dependency order

1. Complete `docs/superpowers/plans/2026-07-22-exact-system-prompt.md`.
2. Complete engine/SDK Tasks 1–9 of `docs/superpowers/plans/2026-07-22-summary-tool-display.md`; its TUI rendering tasks may proceed in parallel with transcript work.
3. Complete `docs/superpowers/plans/2026-07-22-transcript-navigation-hardening.md`.
4. Complete MCP primitive/service Tasks 1–9, then idle gate/browser Tasks 10–14 of `docs/superpowers/plans/2026-07-22-interactive-mcp-manager.md`.
5. Begin this integration plan only after every subsystem’s targeted completion checks pass.

No task in this plan changes package versions, runs `build.ps1` with bumping enabled, publishes artifacts, installs a global tool, creates a release, or pushes a branch.

## Cross-plan conflict map

- `src/Coda.Sdk/CodaSession.cs` — combine exact-prompt startup/persisted precedence with per-root tool activity creation/finalization.
- `src/Coda.Sdk/SessionTranscriptStore.cs`, `SessionBundle.cs`, `SessionBundleService.cs`, `ChatMessageJson.cs` — preserve both optional `systemPromptOverride` metadata and optional tool correlation metadata.
- `src/Coda.Sdk/Serve/ServeHost.cs` — apply resume metadata before initialization while adding correlated turn-complete fields.
- `src/Coda.Tui/InteractiveProgram.cs` — combine prompt-source resolution/state, audit-aware summary history seeding/default mode, shared MCP management/provider construction, and both-shell provider parity.
- `src/Coda.Tui/Commands/ResumeCommand.cs` — combine exact-prompt resume precedence with audit-aware activity projection.
- `src/Coda.Tui/Ui/Events/UiEvent.cs`, `UiEventMailbox.cs`, `UiActor.cs`, `src/Coda.Tui/Ui/State/UiReducer.cs`, `TranscriptBlock.cs` — preserve correlated activity events while transcript navigation observes stable block replacement.
- `src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs` — combine activity projections with timestamp trailing clearance.
- `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs` — combine activity interruptibility, modal-safe global `Ctrl+End`, task/MCP overlay focus, and shared disposal.
- `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs` and `InlineTuiShell.cs` — combine summary defaults, dedicated navigation chrome, task/MCP providers, and identical composition.
- `src/Coda.Tui/ImmediateCli.cs`, `README.md`, `docs/API.md`, `docs/serve-protocol.md` — consolidate all user-visible syntax, settings, wire fields, and compatibility statements without contradictory defaults.

## New integration-owned files

- `tests/Engine.Tests/CliTuiMcpEngineIntegrationTests.cs` — persistence, resume, scheduled-root, and serve contract coexistence.
- `tests/Coda.Tui.Tests/CliTuiMcpTuiIntegrationTests.cs` — activity replacement versus detached anchor/unread and shell overlay/navigation coexistence.
- `tests/Coda.Tui.Tests/CliTuiMcpSpecComplianceTests.cs` — high-risk approved semantics across settings, exact interception, and both shell modes.
- `tests/Engine.Tests/CliTuiMcpEndToEndTests.cs` — one holistic root-turn path used before review.
- `docs/superpowers/reviews/2026-07-22-cli-tui-mcp-holistic-review.md` — review model, findings, fixes, rerun evidence, and final verdict.

### Task 1: Reconcile session metadata, content correlation, and bundle persistence

**Files:**
- Create: `tests/Engine.Tests/CliTuiMcpEngineIntegrationTests.cs`
- Modify if required: `src/Coda.Sdk/SessionMetadata.cs`
- Modify if required: `src/Coda.Sdk/SessionTranscriptStore.cs`
- Modify if required: `src/LlmClient/ContentBlock.cs`
- Modify if required: `src/Coda.Sdk/ChatMessageJson.cs`
- Modify if required: `src/Coda.Sdk/SessionBundle.cs`
- Modify if required: `src/Coda.Sdk/SessionBundleService.cs`

- [ ] **Step 1: Write failing coexistence tests before resolving merge conflicts**

```csharp
using Coda.Agent;
using Coda.Sdk;
using LlmClient;

namespace Engine.Tests;

public sealed class CliTuiMcpEngineIntegrationTests
{
    [Fact]
    public async Task Transcript_round_trips_prompt_metadata_and_tool_correlation_together()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new SessionTranscriptStore(root);
            var messages = CorrelatedHistory();
            await store.SaveAsync(
                "session1",
                messages,
                new SessionMetadata { SystemPromptOverride = string.Empty });

            var loaded = await store.LoadSessionAsync("session1");

            Assert.Equal(string.Empty, loaded!.Metadata.SystemPromptOverride);
            var use = Assert.IsType<ToolUseBlock>(
                loaded.Messages[1].Content.Single());
            var result = Assert.IsType<ToolResultBlock>(
                loaded.Messages[2].Content.Single());
            Assert.Equal("root-1", use.RootTurnId);
            Assert.Equal("activity-1", use.ActivityId);
            Assert.Equal("root:root-1", use.SourceId);
            Assert.Equal(nameof(ToolCallStatus.Succeeded), result.ToolStatus);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Bundle_import_preserves_override_and_activity_fields_without_schema_change()
    {
        var sourceRoot = Directory.CreateTempSubdirectory().FullName;
        var destinationRoot = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await new SessionTranscriptStore(sourceRoot).SaveAsync(
                "session1",
                CorrelatedHistory(),
                new SessionMetadata { SystemPromptOverride = "exact" });
            var source = new SessionBundleService(sourceRoot, "test");
            var bundle = await source.ExportAsync("session1", DateTime.UtcNow);
            var path = await source.WriteAsync(
                bundle!,
                Path.Combine(sourceRoot, "bundle.json"),
                pretty: false);

            var importedId = await new SessionBundleService(destinationRoot, "test")
                .ImportAsync(path);
            var imported = await new SessionTranscriptStore(destinationRoot)
                .LoadSessionAsync(importedId);

            Assert.Equal("coda.session/1", bundle!.Schema);
            Assert.Equal("exact", imported!.Metadata.SystemPromptOverride);
            Assert.Equal(
                "activity-1",
                Assert.IsType<ToolUseBlock>(
                    imported.Messages[1].Content.Single()).ActivityId);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(destinationRoot, recursive: true);
        }
    }

    private static IReadOnlyList<ChatMessage> CorrelatedHistory() =>
    [
        ChatMessage.UserText("go"),
        new ChatMessage(
            ChatRole.Assistant,
            [
                new ToolUseBlock("call-1", "read_file", """{"path":"a.txt"}""")
                {
                    RootTurnId = "root-1",
                    ActivityId = "activity-1",
                    SourceId = "root:root-1",
                },
            ]),
        new ChatMessage(
            ChatRole.User,
            [
                new ToolResultBlock("call-1", "content")
                {
                    RootTurnId = "root-1",
                    ActivityId = "activity-1",
                    SourceId = "root:root-1",
                    ToolStatus = nameof(ToolCallStatus.Succeeded),
                },
            ]),
    ];
}
```

- [ ] **Step 2: Run the engine integration tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpEngineIntegrationTests"`

Expected: FAIL if either workstream overwrote the other’s JSON fields, save/load overloads, or bundle import path.

- [ ] **Step 3: Resolve persistence conflicts with one combined model**

The final transcript root contains:

```csharp
{
    ["id"] = sessionId,
    ["createdUtc"] = createdUtc.ToString("O"),
    ["messages"] = ChatMessageJson.SerializeMessages(messages),
}
```

and conditionally adds only:

```csharp
if (metadata.SystemPromptOverride is not null)
{
    root["systemPromptOverride"] = metadata.SystemPromptOverride;
}
```

Tool identity remains on each serialized tool block, never at transcript root. Bundle `systemPromptOverride` remains separate from audited `systemPrompt`, and `Schema` remains `coda.session/1`.

Retain both compatibility wrappers:

```csharp
public Task SaveAsync(
    string sessionId,
    IReadOnlyList<ChatMessage> messages,
    CancellationToken ct = default);

public Task<IReadOnlyList<ChatMessage>?> LoadAsync(
    string sessionId,
    CancellationToken ct = default);
```

They delegate to metadata-aware APIs without dropping existing metadata.

- [ ] **Step 4: Run focused persistence suites and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpEngineIntegrationTests|FullyQualifiedName~SessionTranscriptTests|FullyQualifiedName~SessionBundleServiceTests|FullyQualifiedName~SessionAuditStoreTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add tests\Engine.Tests\CliTuiMcpEngineIntegrationTests.cs src\Coda.Sdk\SessionMetadata.cs src\Coda.Sdk\SessionTranscriptStore.cs src\LlmClient\ContentBlock.cs src\Coda.Sdk\ChatMessageJson.cs src\Coda.Sdk\SessionBundle.cs src\Coda.Sdk\SessionBundleService.cs
git commit -m "test(integration): preserve prompt and tool session metadata together" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 2: Reconcile resume precedence, root activity, scheduled roots, and serve initialization

**Files:**
- Modify: `tests/Engine.Tests/CliTuiMcpEngineIntegrationTests.cs`
- Modify if required: `src/Coda.Sdk/CodaSession.cs`
- Modify if required: `src/Coda.Sdk/AgentLoopSpec.cs`
- Modify if required: `src/Coda.Sdk/Scheduling/ScheduledAgentHost.cs`
- Modify if required: `src/Coda.Sdk/Serve/ServeHost.cs`
- Modify if required: `src/Coda.Tui/SessionCli.cs`
- Modify if required: `src/Coda.Tui/Commands/ResumeCommand.cs`
- Modify if required: `src/Coda.Tui/InteractiveProgram.cs`

- [ ] **Step 1: Add failing combined precedence/activity tests**

```csharp
[Fact]
public async Task Startup_override_wins_on_resume_and_each_new_root_gets_fresh_activity_identity()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    try
    {
        var factory = new CapturingActivityLoopFactory();
        using var http = new HttpClient(
            new SseTestHandler(SseTestHandler.MessageStopOnly));
        using var session = new CodaSession(
            CredentialFixtures.SignedInClaude(),
            new SessionOptions
            {
                ProviderId = ClaudeAiProvider.Id,
                Model = "claude-sonnet-4-6",
                WorkingDirectory = root,
                PermissionMode = PermissionMode.BypassPermissions,
                SystemPromptOverride = string.Empty,
            },
            httpClient: http,
            agentLoopFactory: factory);
        session.Resume(
            "resumed",
            [ChatMessage.UserText("history")],
            new SessionMetadata { SystemPromptOverride = "persisted" });

        var first = await session.RunAsync("one");
        var second = await session.RunAsync("two");

        Assert.Equal(string.Empty, session.Options.SystemPromptOverride);
        Assert.NotEqual(first.RootTurnId, second.RootTurnId);
        Assert.All(
            factory.Specs,
            spec => Assert.Equal(string.Empty, spec.Options.SystemPrompt));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

[Fact]
public async Task Serve_resume_applies_override_before_initialization_and_emits_correlated_turn_complete()
{
    await using var fixture = await ServeIntegrationFixture.CreateAsync(
        startupOverride: null,
        persistedOverride: "persisted");

    var initialize = await fixture.InitializeAsync("session1");
    var prompt = await fixture.PromptAsync("go");
    var completed = fixture.Notifications
        .Where(item => item.Method == ServeMethods.EventTurnComplete)
        .Select(item => ServeJson.FromNode<TurnCompleteEvent>(item.Params))
        .Last()!;

    Assert.Equal("persisted", fixture.OverrideObservedAtInitialize);
    Assert.Equal(ServeMethods.ProtocolVersion, initialize.ProtocolVersion);
    Assert.Equal("session1", initialize.SessionId);
    Assert.True(prompt.Ok);
    Assert.NotNull(completed.RootTurnId);
    Assert.NotNull(completed.ActivityId);
}

private sealed class CapturingActivityLoopFactory : IAgentLoopFactory
{
    public List<AgentLoopSpec> Specs { get; } = [];

    public IAgentLoop Create(AgentLoopSpec spec)
    {
        this.Specs.Add(spec);
        return new CapturingActivityLoop(spec.ToolActivity!);
    }
}

private sealed class CapturingActivityLoop(
    ToolActivityContext root) : IAgentLoop
{
    public GoalStatus? LastGoalStatus => null;

    public Task RunAsync(
        List<ChatMessage> history,
        IAgentSink sink,
        CancellationToken cancellationToken = default)
    {
        var identity = root.EnsureActivity().ForCall("call-1");
        sink.OnToolQueued(identity, "read_file", """{"path":"a.txt"}""");
        sink.OnToolCall(identity, "read_file", """{"path":"a.txt"}""");
        sink.OnToolStatus(identity, "read_file", ToolCallStatus.Running);
        sink.OnToolResult(
            identity,
            "read_file",
            new ToolResult("content"),
            ToolCallStatus.Succeeded);
        return Task.CompletedTask;
    }
}
```

`ServeIntegrationFixture` is a typed wrapper over the existing `DuplexStreamPair`/`JsonRpcConnection` harness. It seeds `SessionTranscriptStore` with metadata, creates a `CodaSession` using `CapturingActivityLoopFactory`, supplies the internal `ServeHost` initialization delegate from the exact-prompt plan to capture `session.Options.SystemPromptOverride`, records notifications from the client connection, and exposes:

```csharp
internal static Task<ServeIntegrationFixture> CreateAsync(
    string? startupOverride,
    string? persistedOverride);

internal Task<InitializeResult> InitializeAsync(string sessionId);
internal Task<PromptResult> PromptAsync(string text);
internal string? OverrideObservedAtInitialize { get; }
internal IReadOnlyList<RecordedNotification> Notifications { get; }
```

`RecordedNotification` is a typed record:

```csharp
internal sealed record RecordedNotification(
    string Method,
    JsonNode? Params);
```

The fixture uses five-second `WaitAsync` bounds and performs no real HTTP.

- [ ] **Step 2: Run combined resume/root tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpEngineIntegrationTests|FullyQualifiedName~ServeHostResumeTests|FullyQualifiedName~ScheduledAgentHostTests|FullyQualifiedName~CodaSessionLoopFactoryTests"`

Expected: FAIL if `CodaSession` conflict resolution drops either startup precedence, root activity, scheduled exact prompt, or correlated serve completion.

- [ ] **Step 3: Apply the final combined `CodaSession` root flow**

The constructor captures:

```csharp
this.startupSystemPromptOverride = options.SystemPromptOverride;
```

Resume applies:

```csharp
this.Options = this.Options with
{
    SystemPromptOverride =
        this.startupSystemPromptOverride
        ?? metadata.SystemPromptOverride,
};
```

Every `RunAsync` snapshots resolved options, creates one fresh `ToolActivityContext.CreateRoot()`, builds a spec whose `AgentOptions.SystemPrompt` came from `EffectiveSystemPrompt.Resolve`, and completes the recording sink on success/cancel/fault. Scheduled roots snapshot the same current options but create their own root identity. Subagents replace the prompt but retain root/activity identity with a new source.

`ServeHost` loads `StoredSession` and calls metadata-aware resume before driving `InitializeAsync`; turn-complete gets IDs from `RunResult`.

- [ ] **Step 4: Run combined resume/root tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpEngineIntegrationTests|FullyQualifiedName~ServeHostResumeTests|FullyQualifiedName~ServeHostTests|FullyQualifiedName~ScheduledAgentHostTests|FullyQualifiedName~CodaSessionLoopFactoryTests|FullyQualifiedName~EffectiveSystemPromptTests|FullyQualifiedName~AgentToolIdentityTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add tests\Engine.Tests\CliTuiMcpEngineIntegrationTests.cs src\Coda.Sdk\CodaSession.cs src\Coda.Sdk\AgentLoopSpec.cs src\Coda.Sdk\Scheduling\ScheduledAgentHost.cs src\Coda.Sdk\Serve\ServeHost.cs src\Coda.Tui\SessionCli.cs src\Coda.Tui\Commands\ResumeCommand.cs src\Coda.Tui\InteractiveProgram.cs
git commit -m "fix(integration): combine prompt resume and root activity semantics" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 3: Reconcile immutable activity replacement with detached anchor and unread semantics

**Files:**
- Create: `tests/Coda.Tui.Tests/CliTuiMcpTuiIntegrationTests.cs`
- Modify if required: `src/Coda.Tui/Ui/State/TranscriptBlock.cs`
- Modify if required: `src/Coda.Tui/Ui/State/ToolActivityState.cs`
- Modify if required: `src/Coda.Tui/Ui/State/UiReducer.cs`
- Modify if required: `src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs`
- Modify if required: `src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs`
- Modify if required: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Modify if required: `src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs`

- [ ] **Step 1: Write a failing first-insert-versus-replacement test**

```csharp
using Coda.Agent;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class CliTuiMcpTuiIntegrationTests
{
    [Fact]
    public async Task Detached_summary_activity_counts_once_and_keeps_anchor_across_batches()
    {
        using var fixture = RetainedShellFixture.Create(
            activeWork: false,
            transcriptFormatter: (block, width) =>
                TranscriptBlockFormatter.Format(
                    block,
                    width,
                    ToolDisplayMode.Summary));
        var seed = Enumerable.Range(0, 40)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(
                Guid.NewGuid(),
                $"line {index}"))
            .ToImmutableArray();
        await fixture.Shell.ApplyAsync(
            UiSessionSnapshot.Empty with { Transcript = seed },
            CancellationToken.None);
        fixture.Shell.Transcript.ScrollBy(-10);
        var anchor = fixture.Shell.Transcript.TopAnchorForTest;

        var state = UiSessionSnapshot.Empty with { Transcript = seed };
        state = UiReducer.Reduce(
            state,
            new ToolQueuedEvent(Id("a"), "read_file", """{"path":"a.txt"}"""));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);
        state = UiReducer.Reduce(
            state,
            new ToolStateChangedEvent(Id("a"), "read_file", ToolCallStatus.Running));
        state = UiReducer.Reduce(
            state,
            new ToolQueuedEvent(Id("b"), "read_file", """{"path":"b.txt"}"""));
        await fixture.Shell.ApplyAsync(state, CancellationToken.None);

        Assert.Equal(1, fixture.Shell.Transcript.UnseenBlocks);
        Assert.Equal(anchor, fixture.Shell.Transcript.TopAnchorForTest);
        Assert.Single(state.Transcript.OfType<ToolActivityTranscriptBlock>());
        Assert.Equal(
            2,
            state.Transcript.OfType<ToolActivityTranscriptBlock>().Single().Calls.Length);
    }

    private static ToolCallIdentity Id(string callId) =>
        new("root", "activity", callId, "root:root");
}
```

- [ ] **Step 2: Run the TUI integration test and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpTuiIntegrationTests"`

Expected: FAIL if summary updates append new blocks, increment unread repeatedly, or reset absolute scroll position.

- [ ] **Step 3: Preserve stable block identity across reducer and view layers**

The reducer inserts one `ToolActivityTranscriptBlock` only for the first `(RootTurnId, ActivityId)` event and uses `ImmutableArray.SetItem` with the same `Guid Id` thereafter. `FullscreenTuiShell.ReplaceChangedPositions` routes later updates to `ReplaceAt`/`ReplaceLast`, never `Append`. `VirtualizedTranscriptView` restores the detached typed anchor after those replacements and does not call `OnVisibleBlockInserted`.

`TranscriptBlockFormatter` combines summary activity rendering and timestamp trailing cells in the same switch without changing block identity or layout separator behavior.

- [ ] **Step 4: Run focused reducer/viewport/formatter tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpTuiIntegrationTests|FullyQualifiedName~ToolActivityReducerTests|FullyQualifiedName~TranscriptLayoutAnchorTests|FullyQualifiedName~TranscriptViewportNavigationTests|FullyQualifiedName~TranscriptViewportStateUnseenTests|FullyQualifiedName~TranscriptBlockFormatterTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add tests\Coda.Tui.Tests\CliTuiMcpTuiIntegrationTests.cs src\Coda.Tui\Ui\State\TranscriptBlock.cs src\Coda.Tui\Ui\State\ToolActivityState.cs src\Coda.Tui\Ui\State\UiReducer.cs src\Coda.Tui\Ui\Shells\TranscriptLayoutIndex.cs src\Coda.Tui\Ui\Shells\TranscriptViewportState.cs src\Coda.Tui\Ui\Shells\VirtualizedTranscriptView.cs src\Coda.Tui\Ui\Rendering\TranscriptBlockFormatter.cs
git commit -m "fix(integration): preserve viewport state during tool summaries" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 4: Reconcile shell constructors, navigation chrome, activity status, and MCP overlays

**Files:**
- Modify: `tests/Coda.Tui.Tests/CliTuiMcpTuiIntegrationTests.cs`
- Modify if required: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify if required: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs`
- Modify if required: `src/Coda.Tui/Ui/Shells/InlineTuiShell.cs`
- Modify if required: `src/Coda.Tui/Ui/Shells/JumpToBottomHint.cs`
- Modify if required: `src/Coda.Tui/Ui/State/OperationalStatusProjector.cs`
- Modify if required: `src/Coda.Tui/InteractiveProgram.cs`
- Modify: `tests/Coda.Tui.Tests/RetainedShellFixture.cs`

- [ ] **Step 1: Add failing both-mode composition and modal-navigation tests**

```csharp
[Theory]
[InlineData(TuiRunMode.Fullscreen)]
[InlineData(TuiRunMode.Inline)]
public void Both_shells_host_summary_navigation_and_mcp_without_overlap(
    TuiRunMode mode)
{
    using var fixture = RetainedShellFixture.CreateIntegrated(
        mode,
        toolDisplayMode: ToolDisplayMode.Summary);
    fixture.Application.LayoutAndDraw();

    Assert.NotNull(fixture.Shell.McpOverlay);
    Assert.Equal(
        fixture.Shell.Chrome.Frame.Y - 1,
        fixture.Shell.JumpHint.Frame.Y);
    Assert.NotEqual(
        fixture.Shell.Status.Frame.Y,
        fixture.Shell.JumpHint.Frame.Y);
}

[Fact]
public void Visible_mcp_overlay_owns_ctrl_end_and_prompt_focus_round_trip()
{
    using var fixture = RetainedShellFixture.CreateIntegrated(
        TuiRunMode.Fullscreen,
        ToolDisplayMode.Summary);
    fixture.SeedAndDetachTranscript(50, scrollRows: 10);
    var top = fixture.Shell.Transcript.TopRow;
    fixture.Shell.McpOverlay!.Show();

    fixture.Shell.McpOverlay.NewKeyDownEvent(Key.End.WithCtrl);

    Assert.Equal(top, fixture.Shell.Transcript.TopRow);
    Assert.True(fixture.Shell.McpOverlay.HasFocus);
}
```

Add these exact helpers to the existing retained-shell test fixture using real task/MCP providers and no external I/O:

```csharp
internal static RetainedShellFixture CreateIntegrated(
    TuiRunMode mode,
    ToolDisplayMode toolDisplayMode);

internal void SeedAndDetachTranscript(
    int blockCount,
    int scrollRows)
{
    this.Shell.Transcript.ReplaceAll(
        Enumerable.Range(0, blockCount)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(
                Guid.NewGuid(),
                $"line {index}"))
            .ToImmutableArray());
    this.Shell.Transcript.ScrollBy(-Math.Abs(scrollRows));
}
```

`CreateIntegrated` chooses the application model from `mode`, builds one task provider, one real `McpManagementTestHarness`-backed browser provider, stores that harness for fixture disposal, and passes both providers plus `toolDisplayMode` to the selected shell.

- [ ] **Step 2: Run shell integration tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpTuiIntegrationTests|FullyQualifiedName~McpInterceptTests|FullyQualifiedName~TranscriptNavigationChromeTests"`

Expected: FAIL if constructor conflict resolution dropped a provider/default, overlay z-order is wrong, or `Ctrl+End` leaks behind a modal.

- [ ] **Step 3: Use one final shared shell contract**

Final shared constructor parameters include both providers and the display mode:

```csharp
Func<TaskBrowserProvider?>? taskBrowserProvider = null,
Func<McpBrowserProvider?>? mcpBrowserProvider = null,
ToolDisplayMode toolDisplayMode = ToolDisplayMode.Summary
```

`TerminalGuiShellBase` owns both browser controllers/overlays and one helper:

```csharp
private View? VisibleBrowserOverlay() =>
    this.PromptOverlay.Visible
        ? this.PromptOverlay
        : this.McpOverlay?.Visible == true
            ? this.McpOverlay
            : this.TaskOverlay?.Visible == true
                ? this.TaskOverlay
                : null;
```

Prompt has top z-order; MCP/task overlays are above normal/completion views; jump control occupies its dedicated row; summary activity participates in `HasInterruptibleWork`; inline and fullscreen receive identical providers/defaults from `InteractiveProgram`.

- [ ] **Step 4: Run shell integration tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpTuiIntegrationTests|FullyQualifiedName~McpInterceptTests|FullyQualifiedName~TasksInterceptTests|FullyQualifiedName~TranscriptNavigationChromeTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests|FullyQualifiedName~OperationalStatusProjectorTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add tests\Coda.Tui.Tests\CliTuiMcpTuiIntegrationTests.cs tests\Coda.Tui.Tests\RetainedShellFixture.cs src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs src\Coda.Tui\Ui\Shells\FullscreenTuiShell.cs src\Coda.Tui\Ui\Shells\InlineTuiShell.cs src\Coda.Tui\Ui\Shells\JumpToBottomHint.cs src\Coda.Tui\Ui\State\OperationalStatusProjector.cs src\Coda.Tui\InteractiveProgram.cs
git commit -m "fix(integration): compose transcript summary and mcp overlays" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 5: Consolidate CLI help and user/protocol documentation

**Files:**
- Modify: `src/Coda.Tui/ImmediateCli.cs`
- Modify: `tests/Coda.Tui.Tests/ImmediateCliTests.cs`
- Modify: `README.md`
- Modify: `docs/API.md`
- Modify: `docs/serve-protocol.md`

- [ ] **Step 1: Write a failing consolidated help test**

```csharp
[Fact]
public void Help_describes_all_integrated_user_visible_contracts()
{
    var writer = new StringWriter();

    Assert.Equal(0, ImmediateCli.TryHandle(["--help"], writer));

    var help = writer.ToString();
    Assert.Contains("--system-prompt <text>", help, StringComparison.Ordinal);
    Assert.Contains("--system-prompt-file <path>", help, StringComparison.Ordinal);
    Assert.Contains("verbose | compact | summary | tiny", help, StringComparison.Ordinal);
    Assert.Contains("default: summary", help, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("/mcp", help, StringComparison.Ordinal);
    Assert.Contains("Ctrl+End", help, StringComparison.Ordinal);
    Assert.DoesNotContain("default: tiny", help, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run help tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ImmediateCliTests"`

Expected: FAIL until overlapping help edits are consolidated.

- [ ] **Step 3: Update documentation once, with no contradictory semantics**

Consolidate the immediate help around these exact lines:

```csharp
writer.WriteLine($"Usage: {Branding.CliName} [options] [--system-prompt <text> | --system-prompt-file <path>] [--tui=auto|inline|fullscreen] [--plain] [--no-mouse] [--continue] [--resume <id>] [--fork [<id>]]");
writer.WriteLine($"       {Branding.CliName} serve [--system-prompt <text> | --system-prompt-file <path>] [--provider <id>] [--model <id>] [--cwd <path>] [--permission-mode <mode>] [--no-mcp] [--no-project-mcp] [--api-key <key>] [--endpoint <name>]");
writer.WriteLine("Tool display: user-only ~/.coda/settings.json \"toolDisplayMode\" accepts verbose | compact | summary | tiny (default: summary).");
writer.WriteLine("  /mcp            Open the interactive MCP manager in Terminal.Gui; plain modes list textually.");
writer.WriteLine("  Ctrl+End        Jump to the newest transcript output when no modal overlay is active.");
```

`README.md` and `docs/API.md` cover:

- exact prompt syntax, replacement, UTF-8/path contract, warning, resume/fork/export behavior;
- Following/Detached, stable anchor, unseen semantics, jump labels, scrollbar, timestamp gap;
- `summary` default, four modes, all-batch root aggregation, child cap, plain final-only behavior, complete data retention;
- bare Terminal.Gui `/mcp` overlay versus textual fallback, both scopes, list/detail/editor keys, rename, confirmation, busy read-only state, secret ownership, and immediate effective runtime reconciliation.

`docs/serve-protocol.md` covers prompt startup options/initialization ordering and optional tool identity/status/turn-complete fields while keeping protocol version `1`.

- [ ] **Step 4: Run help plus focused documentation-adjacent tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ImmediateCliTests|FullyQualifiedName~ToolDisplayModeResolverTests|FullyQualifiedName~McpCommandTests|FullyQualifiedName~InteractiveProgramTests|FullyQualifiedName~ServeRunnerTests"`

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ServeProtocolTests|FullyQualifiedName~SessionBundleServiceTests|FullyQualifiedName~EffectiveSystemPromptTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\ImmediateCli.cs tests\Coda.Tui.Tests\ImmediateCliTests.cs README.md docs\API.md docs\serve-protocol.md
git commit -m "docs: integrate cli tui and mcp improvements" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 6: Add a high-risk spec-compliance regression matrix

**Files:**
- Create: `tests/Coda.Tui.Tests/CliTuiMcpSpecComplianceTests.cs`
- Modify only when a new test exposes a real integration defect: shared files from Tasks 1–4

- [ ] **Step 1: Write failing high-risk compliance tests**

```csharp
public sealed class CliTuiMcpSpecComplianceTests
{
    [Fact]
    public void Explicit_old_tool_modes_remain_unchanged_while_missing_defaults_to_summary()
    {
        Assert.Equal(ToolDisplayMode.Verbose, ToolDisplayModeResolver.Resolve("verbose").Mode);
        Assert.Equal(ToolDisplayMode.Compact, ToolDisplayModeResolver.Resolve("compact").Mode);
        Assert.Equal(ToolDisplayMode.Tiny, ToolDisplayModeResolver.Resolve("tiny").Mode);
        Assert.Equal(ToolDisplayMode.Summary, ToolDisplayModeResolver.Resolve(null).Mode);
    }

    [Theory]
    [InlineData("/mcp", true)]
    [InlineData(" /mcp ", true)]
    [InlineData("/MCP", false)]
    [InlineData("/mcp list", false)]
    public void Mcp_interception_is_exact(string text, bool expected) =>
        Assert.Equal(expected, McpBrowserController.IsOpenRequest(text));

    [Fact]
    public async Task Disabled_project_definition_shadows_user_after_integrated_toggle()
    {
        await using var harness = await McpManagementTestHarness.CreateAsync();
        harness.WriteUser(
            """{"mcpServers":{"shared":{"type":"http","url":"https://user.test/mcp"}}}""");
        harness.WriteProject(
            """{"mcpServers":{"shared":{"type":"http","url":"https://project.test/mcp"}}}""");

        await harness.Service.SetEnabledAsync(
            new McpServerKey(McpConfigScope.Project, "shared"),
            enabled: false,
            CancellationToken.None);
        var snapshot = await harness.Service.RefreshAsync(CancellationToken.None);

        Assert.False(snapshot.Servers.Single(
            server => server.Key.Scope == McpConfigScope.Project).Enabled);
        Assert.Equal(
            McpConnectionState.Overridden,
            snapshot.Servers.Single(
                server => server.Key.Scope == McpConfigScope.User).Connection);
    }

    [Fact]
    public void Summary_completion_reports_failure_and_cancellation_concisely()
    {
        var text = ToolActivityPreview.CompletedText(
            new ToolActivitySummary(
                "root", "activity", 4, 1, 1, 0, null));
        Assert.Equal("Ran 4 tools - 1 failed, cancelled", text);
    }
}
```

- [ ] **Step 2: Run the compliance matrix and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpSpecComplianceTests"`

Expected: FAIL if any integrated default, interception, disabled-precedence, or wording contract drifted.

- [ ] **Step 3: Apply only minimal corrections exposed by the matrix**

Do not broaden architecture. Correct the owning resolver, exact predicate, merge-then-filter path, or shared wording helper so each test passes. Keep the contract names and signatures defined by the subsystem plans.

- [ ] **Step 4: Run the complete targeted integration matrix and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpEngineIntegrationTests|FullyQualifiedName~EffectiveSystemPromptTests|FullyQualifiedName~AgentToolIdentityTests|FullyQualifiedName~SessionTranscriptTests|FullyQualifiedName~SessionBundleServiceTests|FullyQualifiedName~ServeHostResumeTests|FullyQualifiedName~ServeProtocolTests|FullyQualifiedName~McpReadModelTests|FullyQualifiedName~McpConfigWriterTests|FullyQualifiedName~McpSecretStoreTests|FullyQualifiedName~McpOAuthTokenLifecycleTests|FullyQualifiedName~McpClientIdResolutionTests|FullyQualifiedName~McpUnauthorizedResolutionFlowTests|FullyQualifiedName~McpLifecycleTests"`

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpTuiIntegrationTests|FullyQualifiedName~CliTuiMcpSpecComplianceTests|FullyQualifiedName~SystemPromptSourceResolverTests|FullyQualifiedName~ToolActivityReducerTests|FullyQualifiedName~ToolActivityHistoryProjectorTests|FullyQualifiedName~TranscriptNavigationChromeTests|FullyQualifiedName~TranscriptLayoutAnchorTests|FullyQualifiedName~McpManagementReadTests|FullyQualifiedName~McpManagementEditTests|FullyQualifiedName~McpManagementRuntimeTests|FullyQualifiedName~McpManagementDeleteTests|FullyQualifiedName~McpManagementAuthenticationTests|FullyQualifiedName~McpBrowserControllerTests|FullyQualifiedName~McpBrowserOverlayTests|FullyQualifiedName~McpInterceptTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add tests\Coda.Tui.Tests\CliTuiMcpSpecComplianceTests.cs src\Coda.Tui src\Coda.Sdk src\Coda.Mcp src\Coda.Agent src\LlmClient
git commit -m "test(integration): pin cli tui and mcp spec compliance" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

Before committing, inspect `git diff --cached --name-only` and unstage any production file not changed to satisfy a failing integration test.

### Task 7: Run complete verification, strongest-model review, fixes, reruns, and final verdict

**Files:**
- Create: `tests/Engine.Tests/CliTuiMcpEndToEndTests.cs`
- Create: `docs/superpowers/reviews/2026-07-22-cli-tui-mcp-holistic-review.md`
- Modify only for accepted review findings: the owning production and test files

- [ ] **Step 1: Write one failing holistic root-turn regression**

```csharp
[Fact]
public async Task Exact_prompt_multi_batch_activity_and_post_turn_mcp_refresh_coexist()
{
    await using var fixture = await CliTuiMcpEndToEndFixture.CreateAsync(
        systemPromptOverride: string.Empty,
        toolBatches:
        [
            [new ToolUseBlock("a", "read_file", """{"path":"a.txt"}""")],
            [new ToolUseBlock("b", "read_file", """{"path":"b.txt"}""")],
        ]);

    var result = await fixture.Session.RunAsync("go", fixture.Sink);
    var mcpSnapshot = await fixture.McpManagement.RefreshAsync(
        CancellationToken.None);

    Assert.True(result.Success);
    Assert.Equal(string.Empty, fixture.LastProviderRequest.System);
    Assert.Equal(2, result.ToolActivity!.TotalCalls);
    Assert.Single(fixture.UiSnapshot.Transcript.OfType<ToolActivityTranscriptBlock>());
    Assert.NotNull(mcpSnapshot);
}
```

Build `CliTuiMcpEndToEndFixture` entirely from scripted in-process provider/tool/MCP collaborators; it performs no network, browser, process, or credential-store I/O:

```csharp
internal sealed class CliTuiMcpEndToEndFixture : IAsyncDisposable
{
    private readonly McpManagementTestHarness mcpHarness;

    private CliTuiMcpEndToEndFixture(
        CodaSession session,
        TuiAgentSink sink,
        ReducingPublisher publisher,
        ScriptedCapturingClient client,
        McpManagementTestHarness mcpHarness)
    {
        this.Session = session;
        this.Sink = sink;
        this.publisher = publisher;
        this.client = client;
        this.mcpHarness = mcpHarness;
    }

    private readonly ReducingPublisher publisher;
    private readonly ScriptedCapturingClient client;
    public CodaSession Session { get; }
    public TuiAgentSink Sink { get; }
    public ChatRequest LastProviderRequest => this.client.LastRequest!;
    public UiSessionSnapshot UiSnapshot => this.publisher.State;
    public IMcpManagementService McpManagement => this.mcpHarness.Service;

    public static Task<CliTuiMcpEndToEndFixture> CreateAsync(
        string? systemPromptOverride,
        IReadOnlyList<IReadOnlyList<ToolUseBlock>> toolBatches);

    public async ValueTask DisposeAsync()
    {
        await this.Session.DisposeAsync();
        await this.mcpHarness.DisposeAsync();
    }
}

internal sealed class ReducingPublisher : IUiEventPublisher
{
    public UiSessionSnapshot State { get; private set; } =
        UiSessionSnapshot.Empty;

    public void Publish(UiEvent uiEvent) =>
        this.State = UiReducer.Reduce(this.State, uiEvent);
}
```

`CreateAsync` builds a `ScriptedCapturingClient` that emits each supplied batch followed by a terminal text turn, a registry containing deterministic read-only tools for every supplied tool name, a real `AgentLoop` through the normal `CodaSession` pipeline, a `TuiAgentSink` over `ReducingPublisher`, and a fresh `McpManagementTestHarness`.

- [ ] **Step 2: Run the holistic test and verify RED, then make the minimal integration correction**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~CliTuiMcpEndToEndTests"`

Expected: FAIL if a final boundary still drops exact emptiness, splits batches, loses the UI aggregate, or prevents safe post-turn MCP refresh. Correct only that boundary.

- [ ] **Step 3: Run the complete Release build and test suite**

Run:

```powershell
dotnet build LlmAuth.slnx -c Release
dotnet test tests\Engine.Tests\Engine.Tests.csproj -c Release --no-build
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj -c Release --no-build
dotnet test tests\LlmAuth.Tests\LlmAuth.Tests.csproj -c Release --no-build
```

Expected: build succeeds with zero errors; all three complete test projects pass. Record exact test counts in the review document.

- [ ] **Step 4: Request one strongest-model holistic code review**

Use the strongest available model in the execution environment at maximum supported reasoning effort. Prefer `claude-opus-4.8` when it is listed; otherwise choose the highest-capability available model. Use a read-only code-review specialist with this exact review brief:

```text
Review the complete diff against:
- docs/superpowers/specs/2026-07-22-cli-tui-mcp-improvements-design.md
- docs/superpowers/plans/2026-07-22-exact-system-prompt.md
- docs/superpowers/plans/2026-07-22-transcript-navigation-hardening.md
- docs/superpowers/plans/2026-07-22-summary-tool-display.md
- docs/superpowers/plans/2026-07-22-interactive-mcp-manager.md
- docs/superpowers/plans/2026-07-22-cli-tui-mcp-integration.md

Report only high-confidence correctness, compatibility, concurrency, lifecycle,
secret-handling, persistence, or specification-compliance defects. Verify:
exact prompt persistence/resume precedence; all-batch root activity correlation;
stable detached anchors/unread replacement semantics; MCP scoped precedence,
rename/secret transactions, confirmations, idle-turn safety, runtime
reconciliation, overlay focus/disposal; additive serve/session compatibility.
Include file:line evidence and a concrete regression test for each finding.
```

Write the model/version, reviewed commit range, findings, and evidence into `docs/superpowers/reviews/2026-07-22-cli-tui-mcp-holistic-review.md`.

- [ ] **Step 5: Convert every accepted finding to RED, fix it, and rerun affected tests**

For each accepted finding:

1. add the reviewer’s concrete regression test to the owning test file;
2. run the smallest owning filter and record the expected failure in the review document;
3. apply the minimal production correction;
4. rerun the same filter and record PASS;
5. list rejected findings with technical evidence.

If the review reports no accepted findings, record `Accepted findings: none`; do not invent a code change.

- [ ] **Step 6: Rerun the complete Release verification after review fixes**

Run:

```powershell
dotnet build LlmAuth.slnx -c Release
dotnet test tests\Engine.Tests\Engine.Tests.csproj -c Release --no-build
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj -c Release --no-build
dotnet test tests\LlmAuth.Tests\LlmAuth.Tests.csproj -c Release --no-build
```

Expected: build and all complete test projects pass again. Record post-fix counts.

Expected: PASS — zero build errors and zero failed tests after all accepted review fixes.

- [ ] **Step 7: Write the final spec-compliance and maintainability verdict**

The review document ends with a requirement-by-requirement verdict covering:

- exact inline/file source behavior, replacement, root-only propagation, persistence/resume/fork/bundle;
- Following/Detached transitions, stable anchor, unseen rules, exact jump hit target, global/modal navigation, scrollbar teardown, timestamp gap;
- summary default, stable identity, one all-batch root block, forwarded source, status/finalization, four modes, final-only plain output, resume/wire compatibility;
- exact bare `/mcp`, list/detail/editor navigation, both physical scopes, rename, secret choices/migration, confirmations, disabled shadowing, busy lease, immediate effective runtime reconciliation, focus/disposal;
- compatibility, maintainability, and any residual risk.

The only passing verdict wording is:

```text
Verdict: COMPLIANT — all approved semantics are implemented, the complete Release
build/test suite passes after holistic review, and no unresolved high-confidence
correctness or maintainability finding remains.
```

If any item is unresolved, use `Verdict: NOT COMPLIANT`, list it, and do not mark the plan complete.

- [ ] **Step 8: Commit review fixes, evidence, and verdict**

```powershell
git add tests\Engine.Tests\CliTuiMcpEndToEndTests.cs docs\superpowers\reviews\2026-07-22-cli-tui-mcp-holistic-review.md
git add -u
git commit -m "test(integration): verify cli tui and mcp delivery" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

Inspect `git diff --cached --name-only` before commit; it must contain only the holistic test, review evidence, and accepted review-fix files. Do not publish or bump versions.
