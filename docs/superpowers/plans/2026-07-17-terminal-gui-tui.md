# Terminal.Gui Inline and Full-Screen TUI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the line-oriented interactive console with a safe Terminal.Gui v2 inline/full-screen host while retaining Spectre and plain fallbacks during migration.

**Architecture:** A single `TuiHost` owns terminal lifetime and selects plain, Spectre, Terminal.Gui inline, or Terminal.Gui full-screen mode. Producers publish typed semantic events into a bounded/coalescing mailbox; one UI actor reduces them into immutable snapshots, while each shell owns only focus, viewport, composer, dialog, and wrapping state. Existing output-only slash commands continue writing through an offscreen `IAnsiConsole` adapter that emits typed command-output events, while every interactive prompt is routed through `IUiPromptService`.

**Tech Stack:** C# / .NET 10, Terminal.Gui stable 2.4.17 instance application model (`AppModel.Inline` and `AppModel.FullScreen`), Spectre.Console 0.55.2 compatibility rendering, .NET synchronization primitives, xUnit.

**Approved design:** `docs/superpowers/specs/2026-07-17-terminal-gui-tui-design.md`

---

## Global Constraints

- Keep `coda run`, `coda serve`, `coda models`, `coda help`, export/import, redirected input, and redirected output plain and script-safe.
- `--plain` wins over an accompanying `--tui=<mode>`. `--tui=auto` is the default. Full-screen remains opt-in.
- `--no-mouse` disables Terminal.Gui mouse reporting; every feature remains keyboard-accessible.
- Exercise `--tui=inline` and `--tui=fullscreen` throughout implementation, but do not merge the final `auto`→inline promotion until the Task 14 compatibility matrix and acceptance thresholds pass.
- The minimum interactive size is exactly 60 columns by 12 rows.
- During migration the fallback ladder is full-screen → inline → Spectre → plain. Inline falls back to Spectre → plain.
- Terminal.Gui v2 is the target architecture. `InteractiveLineEditor` and the existing Spectre REPL remain only as a compatibility fallback.
- No command, sink, prompt, setup flow, logger callback, MCP callback, or shell process writes to the active terminal outside `TuiHost` and its actor-owned adapters.
- Keep remote MCP OAuth protocol/credential design out of this work. Existing MCP connection behavior remains unchanged; only generic output, prompt, picker, and status surfaces move behind the UI boundary.
- Use Terminal.Gui's public instance APIs: `Application.Create()`, set `app.AppModel` before `app.Init()`, run a caller-owned `Window`, and dispose `IApplication` in `finally`/`using`.
- Use Terminal.Gui's public `DriverRegistry.Names.ANSI`, `ForceInlinePosition`, `Driver.SetScreenSize`, `Begin`, `LayoutAndDraw`, and input injection APIs in tests. Do not reference unpublished fake-driver packages.
- `TextView` is intentionally used for the composer. Suppress CS0618 only around the composer subclass/file; do not disable the warning project-wide.
- Preserve file-scoped namespaces, braces on control flow, `this.` for instance members, collection expressions, nullable annotations, and `ConfigureAwait(false)` on awaited library/engine operations.
- Run targeted tests first. The final verification runs both `Coda.Tui.Tests` and `Engine.Tests`, then the solution.

## File Structure

### New production files

- `src/Coda.Tui/Ui/Mode/TuiMode.cs` — requested and resolved mode enums.
- `src/Coda.Tui/Ui/TerminalGuiUsings.cs` — explicit Terminal.Gui v2 namespace imports for production.
- `src/Coda.Tui/Ui/Mode/TuiLaunchOptions.cs` — parses `--tui=auto|inline|fullscreen`, `--plain`, and `--no-mouse`, returning unconsumed session arguments.
- `src/Coda.Tui/Ui/Mode/TerminalCapabilities.cs` — immutable terminal capability/size snapshot and provider abstraction.
- `src/Coda.Tui/Ui/Mode/TuiModePolicy.cs` — initial selection, minimum-size validation, and ordered fallback candidates.
- `src/Coda.Tui/Ui/Events/UiEvent.cs` — typed semantic event hierarchy and coalescing keys.
- `src/Coda.Tui/Ui/Events/IUiEventPublisher.cs` — publisher boundary used by sinks, adapters, commands, and runtime sources.
- `src/Coda.Tui/Ui/Events/UiEventMailbox.cs` — bounded mailbox that merges assistant deltas and replaces tool progress.
- `src/Coda.Tui/Ui/Events/UiActor.cs` — single reader/reducer/frame dispatcher and prompt-response resolver.
- `src/Coda.Tui/Ui/State/TranscriptBlock.cs` — typed transcript render blocks.
- `src/Coda.Tui/Ui/State/UiSessionSnapshot.cs` — immutable semantic state with no Terminal.Gui controls or coordinates.
- `src/Coda.Tui/Ui/State/UiReducer.cs` — pure event-to-snapshot reducer.
- `src/Coda.Tui/Ui/State/SessionHistoryProjector.cs` — resumed engine history to completed semantic transcript blocks.
- `src/Coda.Tui/Ui/State/StatusProjector.cs` — width-priority status line projection.
- `src/Coda.Tui/Ui/State/ContextSnapshotCache.cs` — refresh-after-turn/on-demand context analysis cache.
- `src/Coda.Tui/Ui/State/GitStatusCache.cs` — startup/after-turn/on-demand git branch/dirty cache.
- `src/Coda.Tui/Ui/Rendering/OffscreenAnsiConsoleOutput.cs` — fixed-size non-terminal Spectre output buffer.
- `src/Coda.Tui/Ui/Rendering/UiAnsiConsoleAdapter.cs` — offscreen `IAnsiConsole` implementation that emits typed command-output events.
- `src/Coda.Tui/Ui/Rendering/PlainOutputRenderer.cs` — stable no-control-sequence event renderer.
- `src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs` — assistant Markdown and typed block-to-attributed-line formatting.
- `src/Coda.Tui/Ui/Prompts/UiPromptModels.cs` — prompt request, option, response, and kind records.
- `src/Coda.Tui/Ui/Prompts/IUiPromptService.cs` — prompt boundary shared by agent callbacks and commands.
- `src/Coda.Tui/Ui/Prompts/ActorUiPromptService.cs` — actor request/response implementation for Terminal.Gui shells.
- `src/Coda.Tui/Ui/Prompts/SpectreUiPromptService.cs` — compatibility implementation for the Spectre REPL.
- `src/Coda.Tui/Ui/Prompts/PlainUiPromptService.cs` — non-interactive safe-default implementation.
- `src/Coda.Tui/Ui/Input/UiAction.cs` — named input actions.
- `src/Coda.Tui/Ui/Input/UiActionMap.cs` — context-sensitive Terminal.Gui key-to-action mapping.
- `src/Coda.Tui/Ui/Input/ComposerState.cs` — transferable draft, cursor, history, and paste state.
- `src/Coda.Tui/Ui/Input/ComposerController.cs` — draft/history/completion/action behavior independent of Terminal.Gui.
- `src/Coda.Tui/Ui/Input/ComposerView.cs` — localized-obsolete-suppression `TextView` composer.
- `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs` — shared controller, status, composer, overlay, and frame application.
- `src/Coda.Tui/Ui/Shells/InlineTranscriptCommitter.cs` — append-once native-scrollback commit tracking.
- `src/Coda.Tui/Ui/Shells/InlineTuiShell.cs` — composer-first inline layout.
- `src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs` — width-indexed transcript row index with bounded wrap cache.
- `src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs` — auto-follow, scroll offset, and unseen-row presentation state.
- `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs` — custom view drawing only visible transcript rows.
- `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs` — transcript-focused full-screen layout without a sidebar.
- `src/Coda.Tui/Ui/Shells/PromptOverlay.cs` — keyboard-complete selection/text/confirmation overlay.
- `src/Coda.Tui/Ui/Host/TuiShellExit.cs` — exit, switch-mode, and failure result.
- `src/Coda.Tui/Ui/Host/ITuiModeRunner.cs` — host test seam.
- `src/Coda.Tui/Ui/Host/TerminalGuiModeRunner.cs` — Terminal.Gui initialization/run/disposal.
- `src/Coda.Tui/Ui/Host/TerminalProcessExitRegistration.cs` — best-effort managed process-exit stop/cleanup registration.
- `src/Coda.Tui/Ui/Host/TuiHost.cs` — capability policy, fallback ladder, diagnostics, and mode-switch loop.
- `src/Coda.Tui/Ui/TuiController.cs` — shared dispatch, active-turn cancellation, shell actions, and state transfer.
- `src/Coda.Tui/InteractiveProgram.cs` — testable interactive composition root moved out of top-level `Program.cs`.
- `src/Coda.Agent/BackgroundTasks/BackgroundTaskSnapshot.cs` — immutable background-task status record.
- `src/Coda.Agent/Lsp/LspServerSnapshot.cs` — immutable LSP status record.
- `src/Coda.Sdk/SessionRuntimeSnapshot.cs` — immutable engine snapshot consumed by the TUI.
- `src/Coda.Mcp/McpRuntimeSnapshot.cs` — immutable connected MCP snapshot.
- `samples/Coda.TerminalGuiSpike/Coda.TerminalGuiSpike.csproj` — cross-platform Terminal.Gui compatibility harness.
- `samples/Coda.TerminalGuiSpike/Program.cs` — inline/full-screen stream, Unicode, paste, resize, cancellation, mouse-off, and injected-failure scenarios.
- `scripts/terminal-gui-pty-smoke.ps1` — launches the manual PTY matrix and records pass/fail notes.
- `docs/terminal-gui-compatibility.md` — terminal matrix and manual acceptance checklist.

### New test files

- `tests/Coda.Tui.Tests/TuiLaunchOptionsTests.cs`
- `tests/Coda.Tui.Tests/TerminalGuiUsings.cs`
- `tests/Coda.Tui.Tests/TuiModePolicyTests.cs`
- `tests/Coda.Tui.Tests/UiReducerTests.cs`
- `tests/Coda.Tui.Tests/StatusProjectorTests.cs`
- `tests/Coda.Tui.Tests/ContextSnapshotCacheTests.cs`
- `tests/Coda.Tui.Tests/UiEventMailboxTests.cs`
- `tests/Coda.Tui.Tests/UiAnsiConsoleAdapterTests.cs`
- `tests/Coda.Tui.Tests/PlainOutputRendererTests.cs`
- `tests/Coda.Tui.Tests/TranscriptBlockFormatterTests.cs`
- `tests/Coda.Tui.Tests/UiPromptServiceTests.cs`
- `tests/Coda.Tui.Tests/RecordingPromptService.cs`
- `tests/Coda.Tui.Tests/PromptDrivenCommandTests.cs`
- `tests/Coda.Tui.Tests/TuiAgentSinkTests.cs`
- `tests/Coda.Tui.Tests/ComposerControllerTests.cs`
- `tests/Coda.Tui.Tests/ComposerViewTests.cs`
- `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`
- `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`
- `tests/Coda.Tui.Tests/TuiHostTests.cs`
- `tests/Coda.Tui.Tests/InteractiveProgramTests.cs`
- `tests/Engine.Tests/RuntimeSnapshotTests.cs`

### Modified files

- `src/Coda.Tui/Coda.Tui.csproj` — add Terminal.Gui 2.4.17.
- `src/Coda.Tui/Program.cs` — retain headless dispatch, parse interactive mode flags, and delegate to `InteractiveProgram`.
- `src/Coda.Tui/ImmediateCli.cs` — document `--tui` and `--plain`.
- `src/Coda.Tui/ConsoleCancellationRegistration.cs` — route Ctrl-C to active-turn interruption before process exit.
- `src/Coda.Tui/TuiApp.cs` — keep Spectre/plain loops and expose dispatch without owning Terminal.Gui input.
- `src/Coda.Tui/Repl/CommandContext.cs` — add event publisher, prompt service, context cache, and immutable status-source delegates.
- `src/Coda.Tui/Agent/AgentRunner.cs` — publish turn lifecycle, use `TuiAgentSink`, expose immutable runtime snapshots, and refresh context only after turns/on demand.
- `src/Coda.Tui/Agent/TuiAgentSink.cs` — forward every `IAgentSink` callback as a typed event.
- `src/Coda.Tui/Agent/TuiPermissionPrompt.cs` — use `IUiPromptService`.
- `src/Coda.Tui/Agent/TuiPlanApprover.cs` — use `IUiPromptService`.
- `src/Coda.Tui/Agent/TuiUserQuestionPrompt.cs` — use `IUiPromptService`.
- `src/Coda.Tui/Setup/SetupWizard.cs` — use `IUiPromptService`.
- `src/Coda.Tui/Commands/LoginCommand.cs` — use prompt service for provider/deployment/domain.
- `src/Coda.Tui/Commands/ProviderCommand.cs` — use a provider picker when interactive and no id is supplied.
- `src/Coda.Tui/Commands/ModelCommand.cs` — use a model picker when interactive and no id is supplied.
- `src/Coda.Tui/Commands/ResumeCommand.cs` — use a session picker when interactive and no id is supplied.
- `src/Coda.Tui/Commands/McpCommand.cs` — replace every Spectre wizard/confirmation/text prompt.
- `src/Coda.Tui/Commands/MarketplaceCommand.cs` — prompt for missing interactive action/marketplace/plugin/source values.
- `src/Coda.Tui/Commands/PluginCommand.cs` — prompt for missing interactive action/source/plugin values.
- `src/Coda.Tui/Commands/ContextCommand.cs` — consume/refresh `ContextSnapshotCache` rather than constructing a fresh analysis session on each display.
- `src/Coda.Tui/Commands/StatusCommand.cs` — render from the immutable semantic/status snapshot.
- `src/Coda.Tui/Commands/ClearCommand.cs` — publish semantic clear/session-boundary events instead of directly clearing an active Terminal.Gui terminal.
- `src/Coda.Tui/Commands/CostCommand.cs` — publish the same cost estimate used by `/cost`.
- `src/Coda.Tui/Commands/EffortCommand.cs` — publish requested/effective effort changes.
- `src/Coda.Tui/Commands/PermissionsCommand.cs` — publish permission mode changes.
- `src/Coda.Tui/Commands/YoloCommand.cs` — publish bypass permission mode.
- `src/Coda.Tui/Commands/ForkCommand.cs` — publish the new session identity.
- `src/Coda.Tui/Commands/DiffCommand.cs` — publish typed diff blocks in semantic UI modes.
- `src/Coda.Agent/BackgroundTasks/BackgroundTaskRunner.cs` — expose copied snapshots instead of mutable task objects.
- `src/Coda.Agent/Lsp/LspServerManager.cs` — expose copied LSP snapshots.
- `src/Coda.Sdk/CodaSession.cs` — expose one immutable runtime snapshot API.
- `src/Coda.Mcp/McpClientManager.cs` — expose one immutable MCP snapshot API.
- `tests/Coda.Tui.Tests/TestAppBuilder.cs` — inject prompt/event defaults.
- `LlmAuth.slnx` — include the compatibility spike project.
- `README.md` — update interactive TUI engine, flags, modes, keybindings, fallbacks, and plain usage.

---

### Task 1: Add Terminal.Gui and parse/select TUI modes

**Files:**
- Modify: `src/Coda.Tui/Coda.Tui.csproj`
- Create: `src/Coda.Tui/Ui/Mode/TuiMode.cs`
- Create: `src/Coda.Tui/Ui/TerminalGuiUsings.cs`
- Create: `src/Coda.Tui/Ui/Mode/TuiLaunchOptions.cs`
- Create: `src/Coda.Tui/Ui/Mode/TerminalCapabilities.cs`
- Create: `src/Coda.Tui/Ui/Mode/TuiModePolicy.cs`
- Test: `tests/Coda.Tui.Tests/TuiLaunchOptionsTests.cs`
- Create: `tests/Coda.Tui.Tests/TerminalGuiUsings.cs`
- Test: `tests/Coda.Tui.Tests/TuiModePolicyTests.cs`

- [ ] **Step 1: Write the failing option and policy tests**

```csharp
using Coda.Tui.Ui.Mode;

namespace Coda.Tui.Tests;

public sealed class TuiLaunchOptionsTests
{
    [Theory]
    [InlineData("--tui=auto", TuiPreference.Auto)]
    [InlineData("--tui=inline", TuiPreference.Inline)]
    [InlineData("--tui=fullscreen", TuiPreference.Fullscreen)]
    public void Parse_accepts_supported_tui_values(string arg, TuiPreference expected)
    {
        var parsed = TuiLaunchOptions.Parse([arg, "--continue"]);

        Assert.Null(parsed.Error);
        Assert.Equal(expected, parsed.Preference);
        Assert.False(parsed.Plain);
        Assert.Equal(["--continue"], parsed.RemainingArgs);
    }

    [Fact]
    public void Plain_overrides_tui_and_is_removed_from_session_args()
    {
        var parsed = TuiLaunchOptions.Parse(["--tui=fullscreen", "--plain", "--resume", "abc"]);

        Assert.Null(parsed.Error);
        Assert.True(parsed.Plain);
        Assert.Equal(["--resume", "abc"], parsed.RemainingArgs);
    }

    [Fact]
    public void Parse_rejects_unknown_tui_value()
    {
        var parsed = TuiLaunchOptions.Parse(["--tui=windowed"]);

        Assert.Equal("Invalid --tui value 'windowed'. Expected auto, inline, or fullscreen.", parsed.Error);
    }

    [Fact]
    public void Parse_accepts_explicit_mouse_disable()
    {
        var parsed = TuiLaunchOptions.Parse(["--no-mouse"]);

        Assert.True(parsed.MouseDisabled);
        Assert.Empty(parsed.RemainingArgs);
    }
}
```

```csharp
using Coda.Tui.Ui.Mode;

namespace Coda.Tui.Tests;

public sealed class TuiModePolicyTests
{
    [Fact]
    public void Auto_uses_plain_for_redirected_output()
    {
        var caps = new TerminalCapabilities(false, true, 120, 40, true);
        var decision = TuiModePolicy.SelectInitial(new TuiLaunchOptions(TuiPreference.Auto, false, [], null), caps);

        Assert.Equal(TuiRunMode.Plain, decision.Mode);
        Assert.Null(decision.Error);
    }

    [Theory]
    [InlineData(59, 12)]
    [InlineData(60, 11)]
    public void Auto_uses_plain_below_minimum_size(int width, int height)
    {
        var caps = new TerminalCapabilities(false, false, width, height, true);
        var decision = TuiModePolicy.SelectInitial(new TuiLaunchOptions(TuiPreference.Auto, false, [], null), caps);

        Assert.Equal(TuiRunMode.Plain, decision.Mode);
    }

    [Fact]
    public void Explicit_interactive_mode_reports_too_small()
    {
        var caps = new TerminalCapabilities(false, false, 59, 12, true);
        var decision = TuiModePolicy.SelectInitial(new TuiLaunchOptions(TuiPreference.Inline, false, [], null), caps);

        Assert.Null(decision.Mode);
        Assert.Equal("Terminal.Gui requires at least 60 columns by 12 rows; current size is 59x12.", decision.Error);
    }

    [Fact]
    public void Fullscreen_fallback_order_is_complete()
    {
        Assert.Equal(
            [TuiRunMode.Fullscreen, TuiRunMode.Inline, TuiRunMode.Spectre, TuiRunMode.Plain],
            TuiModePolicy.FallbacksFrom(TuiRunMode.Fullscreen));
    }
}
```

- [ ] **Step 2: Run the tests to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiLaunchOptionsTests|FullyQualifiedName~TuiModePolicyTests"
```

Expected: build fails with `CS0234`/`CS0246` because `Coda.Tui.Ui.Mode`, `TuiLaunchOptions`, and `TuiModePolicy` do not exist.

- [ ] **Step 3: Add the package and minimal mode types**

Add to `src/Coda.Tui/Coda.Tui.csproj`:

```xml
<PackageReference Include="Terminal.Gui" Version="2.4.17" />
```

Terminal.Gui v2 types live in focused namespaces rather than the old root namespace. Create `src/Coda.Tui/Ui/TerminalGuiUsings.cs`:

```csharp
global using Terminal.Gui.App;
global using Terminal.Gui.Drawing;
global using Terminal.Gui.Drivers;
global using Terminal.Gui.Input;
global using Terminal.Gui.ViewBase;
global using Terminal.Gui.Views;
```

Create `tests/Coda.Tui.Tests/TerminalGuiUsings.cs`:

```csharp
global using Terminal.Gui.App;
global using Terminal.Gui.Drawing;
global using Terminal.Gui.Drivers;
global using Terminal.Gui.Input;
global using Terminal.Gui.Testing;
global using Terminal.Gui.ViewBase;
global using Terminal.Gui.Views;
```

Create these exact public contracts:

```csharp
namespace Coda.Tui.Ui.Mode;

public enum TuiPreference
{
    Auto,
    Inline,
    Fullscreen,
}

public enum TuiRunMode
{
    Plain,
    Spectre,
    Inline,
    Fullscreen,
}
```

```csharp
namespace Coda.Tui.Ui.Mode;

public sealed record TuiLaunchOptions(
    TuiPreference Preference,
    bool Plain,
    IReadOnlyList<string> RemainingArgs,
    string? Error,
    bool MouseDisabled = false)
{
    public static TuiLaunchOptions Parse(IReadOnlyList<string> args)
    {
        var preference = TuiPreference.Auto;
        var plain = false;
        var mouseDisabled = false;
        var remaining = new List<string>();

        foreach (var arg in args)
        {
            if (arg == "--plain")
            {
                plain = true;
                continue;
            }

            if (arg == "--no-mouse")
            {
                mouseDisabled = true;
                continue;
            }

            if (arg.StartsWith("--tui=", StringComparison.Ordinal))
            {
                var value = arg["--tui=".Length..];
                preference = value switch
                {
                    "auto" => TuiPreference.Auto,
                    "inline" => TuiPreference.Inline,
                    "fullscreen" => TuiPreference.Fullscreen,
                    _ => preference,
                };

                if (value is not ("auto" or "inline" or "fullscreen"))
                {
                    return new(preference, plain, remaining, $"Invalid --tui value '{value}'. Expected auto, inline, or fullscreen.", mouseDisabled);
                }

                continue;
            }

            remaining.Add(arg);
        }

        return new(preference, plain, remaining, null, mouseDisabled);
    }
}
```

```csharp
namespace Coda.Tui.Ui.Mode;

public sealed record TerminalCapabilities(
    bool InputRedirected,
    bool OutputRedirected,
    int Width,
    int Height,
    bool Interactive);

public interface ITerminalCapabilitiesProvider
{
    TerminalCapabilities Get();
}

public sealed class SystemTerminalCapabilitiesProvider : ITerminalCapabilitiesProvider
{
    public TerminalCapabilities Get();
}

public sealed record TuiModeDecision(TuiRunMode? Mode, string? Error);
```

`SystemTerminalCapabilitiesProvider.Get` snapshots redirection and dimensions once, treats `TERM=dumb` as non-interactive, and catches `IOException`/`PlatformNotSupportedException` by returning a non-interactive 0x0 snapshot. It performs no capability query from a render/frame method.

```csharp
namespace Coda.Tui.Ui.Mode;

public static class TuiModePolicy
{
    public const int MinimumWidth = 60;
    public const int MinimumHeight = 12;

    public static TuiModeDecision SelectInitial(TuiLaunchOptions options, TerminalCapabilities caps)
    {
        if (options.Error is not null)
        {
            return new(null, options.Error);
        }

        if (options.Plain)
        {
            return new(TuiRunMode.Plain, null);
        }

        var unsuitable = caps.InputRedirected || caps.OutputRedirected || !caps.Interactive;
        var tooSmall = caps.Width < MinimumWidth || caps.Height < MinimumHeight;
        if (options.Preference == TuiPreference.Auto && (unsuitable || tooSmall))
        {
            return new(TuiRunMode.Plain, null);
        }

        if (tooSmall)
        {
            return new(null, $"Terminal.Gui requires at least 60 columns by 12 rows; current size is {caps.Width}x{caps.Height}.");
        }

        return new(
            options.Preference == TuiPreference.Fullscreen ? TuiRunMode.Fullscreen : TuiRunMode.Inline,
            null);
    }

    public static IReadOnlyList<TuiRunMode> FallbacksFrom(TuiRunMode mode) => mode switch
    {
        TuiRunMode.Fullscreen => [TuiRunMode.Fullscreen, TuiRunMode.Inline, TuiRunMode.Spectre, TuiRunMode.Plain],
        TuiRunMode.Inline => [TuiRunMode.Inline, TuiRunMode.Spectre, TuiRunMode.Plain],
        TuiRunMode.Spectre => [TuiRunMode.Spectre, TuiRunMode.Plain],
        _ => [TuiRunMode.Plain],
    };
}
```

- [ ] **Step 4: Run the targeted tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiLaunchOptionsTests|FullyQualifiedName~TuiModePolicyTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 5: Commit**

```powershell
git add src/Coda.Tui/Coda.Tui.csproj src/Coda.Tui/Ui/TerminalGuiUsings.cs src/Coda.Tui/Ui/Mode tests/Coda.Tui.Tests/TerminalGuiUsings.cs tests/Coda.Tui.Tests/TuiLaunchOptionsTests.cs tests/Coda.Tui.Tests/TuiModePolicyTests.cs
git commit -m "feat(tui): add Terminal.Gui mode policy" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: Define typed events, transcript blocks, immutable snapshots, and reducer

**Files:**
- Create: `src/Coda.Tui/Ui/Events/UiEvent.cs`
- Create: `src/Coda.Tui/Ui/Events/IUiEventPublisher.cs`
- Create: `src/Coda.Tui/Ui/State/TranscriptBlock.cs`
- Create: `src/Coda.Tui/Ui/State/UiSessionSnapshot.cs`
- Create: `src/Coda.Tui/Ui/State/UiReducer.cs`
- Create: `src/Coda.Tui/Ui/State/SessionHistoryProjector.cs`
- Test: `tests/Coda.Tui.Tests/UiReducerTests.cs`

- [ ] **Step 1: Write the failing reducer tests**

```csharp
using System.Collections.Immutable;
using Coda.Agent;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class UiReducerTests
{
    [Fact]
    public void Assistant_deltas_coalesce_into_one_active_block_then_complete()
    {
        var state = UiSessionSnapshot.Empty;

        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("hel"));
        state = UiReducer.Reduce(state, new AssistantTextDeltaEvent("lo"));
        state = UiReducer.Reduce(state, new AssistantTextCompletedEvent());

        var block = Assert.IsType<AssistantTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal("hello", block.Text);
        Assert.True(block.Complete);
    }

    [Fact]
    public void Tool_progress_replaces_the_active_tool_state()
    {
        var state = UiReducer.Reduce(UiSessionSnapshot.Empty, new ToolStartedEvent("grep", "{\"q\":\"x\"}"));
        state = UiReducer.Reduce(state, new ToolProgressEvent("grep", 1500));
        state = UiReducer.Reduce(state, new ToolProgressEvent("grep", 2400));

        var block = Assert.IsType<ToolTranscriptBlock>(Assert.Single(state.Transcript));
        Assert.Equal(2400, block.ElapsedMs);
        Assert.False(block.Complete);
    }

    [Fact]
    public void Usage_stop_limits_and_errors_are_preserved_semantically()
    {
        var state = UiSessionSnapshot.Empty;
        state = UiReducer.Reduce(state, new UsageEvent(new TokenUsage(100, 20)));
        state = UiReducer.Reduce(state, new StopReasonEvent("max_tokens"));
        state = UiReducer.Reduce(state, new LimitReachedEvent("max_tokens", "Continue to resume."));
        state = UiReducer.Reduce(state, new AgentErrorEvent("network unavailable"));

        Assert.Equal(new TokenUsage(100, 20), state.SessionUsage);
        Assert.Equal("max_tokens", state.StopReason);
        Assert.Equal("network unavailable", state.Notification?.Message);
        Assert.Equal(UiNotificationLevel.Error, state.Notification?.Level);
    }

    [Fact]
    public void Snapshot_contains_no_Terminal_Gui_types()
    {
        var propertyTypes = typeof(UiSessionSnapshot).GetProperties().Select(p => p.PropertyType.FullName ?? string.Empty);

        Assert.DoesNotContain(propertyTypes, name => name.StartsWith("Terminal.Gui", StringComparison.Ordinal));
    }

    [Fact]
    public void Resumed_history_projects_to_completed_user_and_assistant_blocks()
    {
        var blocks = SessionHistoryProjector.Project(
        [
            ChatMessage.UserText("question"),
            new ChatMessage(ChatRole.Assistant, [new TextBlock("answer")]),
        ]);

        Assert.IsType<UserTranscriptBlock>(blocks[0]);
        var assistant = Assert.IsType<AssistantTranscriptBlock>(blocks[1]);
        Assert.Equal("answer", assistant.Text);
        Assert.True(assistant.Complete);
    }
}
```

- [ ] **Step 2: Run the reducer tests to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiReducerTests"
```

Expected: build fails because the event, block, snapshot, and reducer types do not exist.

- [ ] **Step 3: Add the exact event and state contracts**

Use this event hierarchy in `UiEvent.cs`:

```csharp
using System.Collections.Immutable;
using Coda.Agent;
using Coda.Mcp;
using Coda.Sdk;
using Coda.Tui.Ui.State;
using LlmClient;

namespace Coda.Tui.Ui.Events;

public abstract record UiEvent;

public sealed record UserPromptSubmittedEvent(string Text) : UiEvent;
public sealed record TranscriptSeededEvent(ImmutableArray<TranscriptBlock> Blocks) : UiEvent;
public sealed record AssistantTextDeltaEvent(string Delta) : UiEvent;
public sealed record AssistantTextCompletedEvent : UiEvent;
public sealed record ToolStartedEvent(string ToolName, string InputJson) : UiEvent;
public sealed record ToolProgressEvent(string ToolName, long ElapsedMs) : UiEvent;
public sealed record ToolCompletedEvent(string ToolName, ToolResult Result) : UiEvent;
public sealed record AgentErrorEvent(string Message) : UiEvent;
public sealed record LimitReachedEvent(string Kind, string Message) : UiEvent;
public sealed record StopReasonEvent(string? StopReason) : UiEvent;
public sealed record UsageEvent(TokenUsage Usage) : UiEvent;
public sealed record CommandOutputEvent(string Text) : UiEvent;
public sealed record DiffOutputEvent(string Patch) : UiEvent;
public sealed record WarningEvent(string Message) : UiEvent;
public sealed record NotificationEvent(string Message, UiNotificationLevel Level) : UiEvent;
public sealed record DiagnosticEvent(string Source, string Message, UiNotificationLevel Level) : UiEvent;
public sealed record ConsoleClearRequestedEvent : UiEvent;
public sealed record TranscriptClearedEvent(string NewSessionId) : UiEvent;
public sealed record SessionMetadataChangedEvent(
    string? SessionId,
    string Provider,
    string Model,
    string? RequestedEffort,
    string EffectiveEffort,
    string WorkingDirectory,
    PermissionMode PermissionMode,
    bool Connected) : UiEvent;
public sealed record CostEstimateChangedEvent(decimal? EstimatedCost) : UiEvent;
public sealed record GitChangedEvent(GitStatus Git) : UiEvent;
public sealed record PermissionRequestedEvent(string ToolName, string InputPreview) : UiEvent;
public sealed record PermissionResolvedEvent(string ToolName, bool Allowed) : UiEvent;
public sealed record UserQuestionRequestedEvent(string Question, IReadOnlyList<string> Options, bool MultiSelect) : UiEvent;
public sealed record UserQuestionResolvedEvent(string Question, string Answer) : UiEvent;
public sealed record PlanApprovalRequestedEvent(string Plan) : UiEvent;
public sealed record PlanApprovalResolvedEvent(bool Approved) : UiEvent;
public sealed record SessionRuntimeChangedEvent(SessionRuntimeSnapshot Snapshot) : UiEvent;
public sealed record McpRuntimeChangedEvent(McpRuntimeSnapshot Snapshot) : UiEvent;
public sealed record ContextChangedEvent(ContextStatus Context) : UiEvent;
public sealed record ModeChangedEvent(string Mode) : UiEvent;
public sealed record TurnStartedEvent(string Prompt) : UiEvent;
public sealed record TurnCompletedEvent(bool Success) : UiEvent;
public sealed record TurnInterruptedEvent : UiEvent;
```

Use these transcript/state records:

```csharp
using System.Collections.Immutable;
using Coda.Agent;
using Coda.Mcp;
using Coda.Sdk;
using LlmClient;

namespace Coda.Tui.Ui.State;

public abstract record TranscriptBlock(Guid Id);
public sealed record UserTranscriptBlock(Guid Id, string Text) : TranscriptBlock(Id);
public sealed record AssistantTranscriptBlock(Guid Id, string Text, bool Complete) : TranscriptBlock(Id);
public sealed record ToolTranscriptBlock(
    Guid Id,
    string ToolName,
    string InputJson,
    long? ElapsedMs,
    string? Result,
    bool IsError,
    bool Complete) : TranscriptBlock(Id);
public sealed record CommandOutputTranscriptBlock(Guid Id, string Text) : TranscriptBlock(Id);
public sealed record DiffTranscriptBlock(Guid Id, string Patch) : TranscriptBlock(Id);
public sealed record PermissionTranscriptBlock(Guid Id, string ToolName, string InputPreview, bool? Allowed) : TranscriptBlock(Id);
public sealed record UserQuestionTranscriptBlock(Guid Id, string Question, string? Answer) : TranscriptBlock(Id);
public sealed record NoticeTranscriptBlock(Guid Id, string Text, UiNotificationLevel Level) : TranscriptBlock(Id);
public sealed record SessionBoundaryTranscriptBlock(Guid Id, string SessionId) : TranscriptBlock(Id);

public enum UiNotificationLevel
{
    Information,
    Warning,
    Error,
}

public sealed record UiNotification(string Message, UiNotificationLevel Level);
public sealed record ContextStatus(int UsedTokens, int MaxTokens, int Percentage, bool IsExact);
public sealed record GitStatus(string? Branch, bool Dirty);
public sealed record PermissionStatus(PermissionMode Mode, int PendingCount);
public sealed record ServiceStatus(int Connected, int Error);
public sealed record ActiveOperation(string Kind, string Label, long? ElapsedMs);

public sealed record UiSessionSnapshot(
    string? SessionId,
    string Provider,
    string Model,
    string? RequestedEffort,
    string EffectiveEffort,
    bool Connected,
    ContextStatus? Context,
    TokenUsage SessionUsage,
    decimal? EstimatedCost,
    string WorkingDirectory,
    GitStatus Git,
    PermissionStatus Permission,
    ServiceStatus Mcp,
    ServiceStatus Lsp,
    int RunningTasks,
    ActiveOperation? ActiveOperation,
    SessionRuntimeSnapshot? Runtime,
    McpRuntimeSnapshot? McpRuntime,
    ImmutableArray<TranscriptBlock> Transcript,
    UiNotification? Notification,
    string? StopReason,
    string Mode)
{
    public static UiSessionSnapshot Empty { get; } = new(
        null,
        string.Empty,
        string.Empty,
        null,
        "auto",
        false,
        null,
        TokenUsage.Zero,
        null,
        Directory.GetCurrentDirectory(),
        new(null, false),
        new(PermissionMode.Default, 0),
        new(0, 0),
        new(0, 0),
        0,
        null,
        null,
        null,
        [],
        null,
        null,
        "plain");
}
```

`IUiEventPublisher` is synchronous because `IAgentSink` callbacks are synchronous:

```csharp
namespace Coda.Tui.Ui.Events;

public interface IUiEventPublisher
{
    void Publish(UiEvent uiEvent);
}

public sealed class NullUiEventPublisher : IUiEventPublisher
{
    public static NullUiEventPublisher Instance { get; } = new();

    private NullUiEventPublisher()
    {
    }

    public void Publish(UiEvent uiEvent)
    {
    }
}
```

- [ ] **Step 4: Implement the reducer with stable active-block replacement**

`UiReducer.Reduce` must:

1. Replace transcript from `TranscriptSeededEvent`; append `UserTranscriptBlock` for `UserPromptSubmittedEvent`.
2. Append the first assistant delta as an incomplete `AssistantTranscriptBlock`; subsequent deltas replace the last incomplete assistant block with concatenated text.
3. Mark the last incomplete assistant block complete on `AssistantTextCompletedEvent`.
4. Append a tool block on start; replace the newest incomplete matching tool block on progress/result.
5. Sum `UsageEvent` into `SessionUsage`.
6. Append/replace typed diff, permission, and user-question blocks; increment/decrement pending permission count; plan requests are warning/information blocks with approval state.
7. Clear transcript state on `ConsoleClearRequestedEvent`; clear and append a session boundary on `TranscriptClearedEvent`.
8. Replace provider/model/requested effort/effective effort/cwd/permission/session id/connection state from `SessionMetadataChangedEvent`.
9. Store the full immutable `SessionRuntimeSnapshot`/`McpRuntimeSnapshot`; derive running-task and MCP/LSP summary counts from those copied records.
10. Preserve cost, git, stop reason, notification severity, context, mode, and session boundaries.
11. Never mutate the input snapshot or its `ImmutableArray`.

Core replacement helper:

```csharp
private static ImmutableArray<TranscriptBlock> ReplaceLast(
    ImmutableArray<TranscriptBlock> blocks,
    Func<TranscriptBlock, bool> predicate,
    Func<TranscriptBlock, TranscriptBlock> replace)
{
    for (var index = blocks.Length - 1; index >= 0; index--)
    {
        if (predicate(blocks[index]))
        {
            return blocks.SetItem(index, replace(blocks[index]));
        }
    }

    return blocks;
}
```

Use `Guid.NewGuid()` only when a new semantic block is appended; replacement retains the original block id.

`SessionHistoryProjector.Project(IReadOnlyList<ChatMessage>)` maps persisted user/assistant text, tool use/results, and errors into completed typed blocks. It does not copy Terminal.Gui state or mutate `SessionState.History`.

- [ ] **Step 5: Run the reducer tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiReducerTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 6: Commit**

```powershell
git add src/Coda.Tui/Ui/Events src/Coda.Tui/Ui/State/TranscriptBlock.cs src/Coda.Tui/Ui/State/UiSessionSnapshot.cs src/Coda.Tui/Ui/State/UiReducer.cs src/Coda.Tui/Ui/State/SessionHistoryProjector.cs tests/Coda.Tui.Tests/UiReducerTests.cs
git commit -m "feat(tui): add semantic UI state reducer" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: Add explicit runtime snapshots, context caching, and responsive status projection

**Files:**
- Create: `src/Coda.Agent/BackgroundTasks/BackgroundTaskSnapshot.cs`
- Create: `src/Coda.Agent/Lsp/LspServerSnapshot.cs`
- Create: `src/Coda.Sdk/SessionRuntimeSnapshot.cs`
- Create: `src/Coda.Mcp/McpRuntimeSnapshot.cs`
- Modify: `src/Coda.Agent/BackgroundTasks/BackgroundTaskRunner.cs`
- Modify: `src/Coda.Agent/Lsp/LspServerManager.cs`
- Modify: `src/Coda.Sdk/CodaSession.cs`
- Modify: `src/Coda.Mcp/McpClientManager.cs`
- Create: `src/Coda.Tui/Ui/State/ContextSnapshotCache.cs`
- Create: `src/Coda.Tui/Ui/State/GitStatusCache.cs`
- Create: `src/Coda.Tui/Ui/State/StatusProjector.cs`
- Test: `tests/Engine.Tests/RuntimeSnapshotTests.cs`
- Test: `tests/Coda.Tui.Tests/ContextSnapshotCacheTests.cs`
- Test: `tests/Coda.Tui.Tests/StatusProjectorTests.cs`

- [ ] **Step 1: Write failing runtime snapshot tests**

```csharp
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Lsp;
using Coda.Mcp;

namespace Engine.Tests.Lsp;

public sealed class RuntimeSnapshotTests
{
    [Fact]
    public void Background_task_snapshot_is_a_copy()
    {
        using var runner = new BackgroundTaskRunner();

        var first = runner.GetSnapshot();
        var second = runner.GetSnapshot();

        Assert.Empty(first);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Lsp_snapshot_exposes_names_states_and_extensions_without_instances()
    {
        var (manager, loop) = LspFakeServerHarness.BuildManager("csharp", ".cs", "csharp");
        await using var ownedManager = manager;
        await using var ownedLoop = loop;

        var snapshot = Assert.Single(ownedManager.GetSnapshot());

        Assert.Equal("csharp", snapshot.Name);
        Assert.Equal(LspServerState.Stopped, snapshot.State);
        Assert.Equal([".cs"], snapshot.Extensions);
        Assert.DoesNotContain(
            snapshot.GetType().GetProperties(),
            property => property.PropertyType == typeof(LspServerInstance));
    }

    [Fact]
    public void Mcp_snapshot_does_not_expose_mutable_client_or_tool_lists()
    {
        var manager = new McpClientManager();

        var snapshot = manager.GetSnapshot();

        Assert.Equal(0, snapshot.Version);
        Assert.Empty(snapshot.Servers);
        Assert.DoesNotContain(
            snapshot.GetType().GetProperties(),
            property => property.PropertyType.Name.Contains("IMcpClient", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2: Write failing context-cache and status tests**

```csharp
using Coda.Sdk;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class ContextSnapshotCacheTests
{
    [Fact]
    public async Task Get_does_not_reanalyze_until_invalidated()
    {
        var calls = 0;
        var report = new ContextReport
        {
            Model = "model",
            MaxTokens = 100,
            Categories = [],
            UsedTokens = 42,
            IsExact = true,
            MessageCount = 2,
        };
        var cache = new ContextSnapshotCache(_ =>
        {
            calls++;
            return Task.FromResult(report);
        });

        await cache.GetAsync();
        await cache.GetAsync();
        cache.InvalidateAfterTurn();
        await cache.GetAsync();

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Git_status_is_probed_only_after_startup_or_invalidation()
    {
        var calls = 0;
        var cache = new GitStatusCache((_, _) =>
        {
            calls++;
            return Task.FromResult(new GitStatus("main", true));
        });

        await cache.GetAsync(@"C:\repo");
        await cache.GetAsync(@"C:\repo");
        cache.InvalidateAfterTurn();
        var status = await cache.GetAsync(@"C:\repo");

        Assert.Equal(2, calls);
        Assert.Equal(new GitStatus("main", true), status);
    }
}
```

```csharp
using Coda.Agent;
using Coda.Tui.Ui.State;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class StatusProjectorTests
{
    private static UiSessionSnapshot Snapshot() => UiSessionSnapshot.Empty with
    {
        Model = "gpt-5.6-sol",
        EffectiveEffort = "high",
        Context = new ContextStatus(84_000, 200_000, 42, true),
        Permission = new PermissionStatus(PermissionMode.Default, 0),
        SessionUsage = new TokenUsage(18_200, 2_400),
        EstimatedCost = 0.184m,
        Mcp = new ServiceStatus(3, 0),
        Lsp = new ServiceStatus(2, 0),
        Git = new GitStatus("main", true),
        WorkingDirectory = @"C:\src\coda-cli",
    };

    [Fact]
    public void Narrow_status_keeps_only_high_priority_fields()
    {
        Assert.Equal(
            "gpt-5.6-sol | high | ctx 42% | default",
            StatusProjector.Project(Snapshot(), 44));
    }

    [Fact]
    public void Wide_status_includes_usage_services_and_git_without_newline()
    {
        var status = StatusProjector.Project(Snapshot(), 160);

        Assert.Contains("ctx 84k/200k", status);
        Assert.Contains("18.2k in / 2.4k out", status);
        Assert.Contains("$0.184", status);
        Assert.Contains("MCP 3", status);
        Assert.Contains("LSP 2", status);
        Assert.Contains("main*", status);
        Assert.DoesNotContain('\n', status);
    }

    [Fact]
    public void Medium_status_drops_services_before_usage()
    {
        var status = StatusProjector.Project(Snapshot(), 80);

        Assert.Contains("18.2k in / 2.4k out", status);
        Assert.DoesNotContain("MCP 3", status);
        Assert.DoesNotContain("main*", status);
    }
}
```

- [ ] **Step 3: Run targeted tests to verify failure**

Run:

```powershell
dotnet test tests/Engine.Tests/Engine.Tests.csproj --filter "FullyQualifiedName~RuntimeSnapshotTests"
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ContextSnapshotCacheTests|FullyQualifiedName~StatusProjectorTests"
```

Expected: build fails because snapshot, cache, and projector APIs do not exist.

- [ ] **Step 4: Add immutable snapshot APIs**

Use these exact records:

```csharp
namespace Coda.Agent.BackgroundTasks;

public sealed record BackgroundTaskSnapshot(string Id, BackgroundTaskStatus Status);
```

```csharp
namespace Coda.Agent.Lsp;

public sealed record LspServerSnapshot(
    string Name,
    LspServerState State,
    IReadOnlyList<string> Extensions);
```

```csharp
using Coda.Agent;
using Coda.Agent.BackgroundTasks;
using Coda.Agent.Goals;
using Coda.Agent.Lsp;
using Coda.Agent.Scheduling;
using Coda.Agent.Todos;
using LlmClient;

namespace Coda.Sdk;

public sealed record SessionRuntimeSnapshot(
    string SessionId,
    TokenUsage Usage,
    GoalStatus? Goal,
    IReadOnlyList<TodoItem> Todos,
    IReadOnlyList<ScheduledTask> ScheduledTasks,
    IReadOnlyList<BackgroundTaskSnapshot> BackgroundTasks,
    IReadOnlyList<LspServerSnapshot> LspServers);
```

```csharp
namespace Coda.Mcp;

public sealed record McpServerRuntimeSnapshot(
    string Name,
    McpServerInfo? Info,
    int ToolCount);

public sealed record McpRuntimeSnapshot(
    int Version,
    IReadOnlyList<McpServerRuntimeSnapshot> Servers);
```

Implement copied accessors:

```csharp
public IReadOnlyList<BackgroundTaskSnapshot> GetSnapshot() =>
    [.. this.tasks.Values
        .Select(task => new BackgroundTaskSnapshot(task.Id, task.Status))
        .OrderBy(task => task.Id, StringComparer.Ordinal)];
```

```csharp
public IReadOnlyList<LspServerSnapshot> GetSnapshot() =>
    [.. this.servers
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => new LspServerSnapshot(
            pair.Key,
            pair.Value.State,
            [.. pair.Value.Config.ExtensionToLanguage.Keys.OrderBy(value => value, StringComparer.Ordinal)]))];
```

```csharp
public McpRuntimeSnapshot GetSnapshot() => new(
    this.Version,
    [.. this.clients
        .OrderBy(client => client.ServerName, StringComparer.Ordinal)
        .Select(client => new McpServerRuntimeSnapshot(
            client.ServerName,
            client.ServerInfo,
            this.ServerTools(client.ServerName).Count))]);
```

Add to `CodaSession`:

```csharp
public SessionRuntimeSnapshot GetRuntimeSnapshot() => new(
    this.SessionId,
    this.sessionUsage,
    null,
    [.. this.todos.Items],
    [.. this.schedules.Items],
    this.backgroundTasks.GetSnapshot(),
    this.lspManager?.GetSnapshot() ?? []);
```

When the current turn returns a `GoalStatus`, store it in a private `GoalStatus? lastGoalStatus` field before constructing the snapshot; use that field instead of the `null` shown in the initial minimal body.

- [ ] **Step 5: Implement cache and status priority**

`ContextSnapshotCache` has exactly these methods:

```csharp
public sealed class ContextSnapshotCache
{
    public ContextSnapshotCache(Func<CancellationToken, Task<ContextReport>> analyze);
    public ContextReport? Current { get; }
    public void InvalidateAfterTurn();
    public Task<ContextReport> GetAsync(bool force = false, CancellationToken cancellationToken = default);
}
```

It stores one in-flight task under a lock, reuses the completed report until invalidated, and never calls `analyze` from a render/frame method.

`GitStatusCache` has:

```csharp
public GitStatusCache(Func<string, CancellationToken, Task<GitStatus>> probe);
public void InvalidateAfterTurn();
public Task<GitStatus> GetAsync(string workingDirectory, CancellationToken cancellationToken = default);
```

The production probe runs `git status --porcelain=v1 --branch` with a two-second timeout, parses branch/dirty state, and returns `new GitStatus(null, false)` when git is absent or the directory is not a repository. It is called at startup, after completed turns, and on explicit status refresh, never per frame.

`StatusProjector.Project(UiSessionSnapshot snapshot, int width)` builds fields in this exact order:

1. model
2. effective effort
3. `ctx {percentage}%` under 72 columns, otherwise `ctx {used}/{max}`; prefix token values with `~` when `IsExact` is false
4. pending permission count or permission mode
5. active operation
6. input/output usage
7. cost
8. MCP then LSP
9. git
10. working directory

Join with `" | "`. Remove fields from the end until the cell length is within `width`. If the model alone is wider than `width`, truncate it with `…`. Use invariant lowercase compact units (`84k`, `18.2k`, `1.2m`).

- [ ] **Step 6: Run targeted tests**

Run:

```powershell
dotnet test tests/Engine.Tests/Engine.Tests.csproj --filter "FullyQualifiedName~RuntimeSnapshotTests"
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ContextSnapshotCacheTests|FullyQualifiedName~StatusProjectorTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 7: Commit**

```powershell
git add src/Coda.Agent/BackgroundTasks src/Coda.Agent/Lsp src/Coda.Sdk/SessionRuntimeSnapshot.cs src/Coda.Sdk/CodaSession.cs src/Coda.Mcp/McpRuntimeSnapshot.cs src/Coda.Mcp/McpClientManager.cs src/Coda.Tui/Ui/State/ContextSnapshotCache.cs src/Coda.Tui/Ui/State/GitStatusCache.cs src/Coda.Tui/Ui/State/StatusProjector.cs tests/Engine.Tests/RuntimeSnapshotTests.cs tests/Coda.Tui.Tests/ContextSnapshotCacheTests.cs tests/Coda.Tui.Tests/StatusProjectorTests.cs
git commit -m "feat(tui): expose immutable runtime status" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: Implement the bounded/coalescing UI mailbox and single actor

**Files:**
- Create: `src/Coda.Tui/Ui/Events/UiEventMailbox.cs`
- Create: `src/Coda.Tui/Ui/Events/UiActor.cs`
- Test: `tests/Coda.Tui.Tests/UiEventMailboxTests.cs`

- [ ] **Step 1: Write failing mailbox tests**

```csharp
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class UiEventMailboxTests
{
    [Fact]
    public async Task Assistant_deltas_merge_without_exceeding_capacity()
    {
        using var mailbox = new UiEventMailbox(capacity: 4);

        for (var index = 0; index < 100; index++)
        {
            mailbox.Publish(new AssistantTextDeltaEvent(index.ToString()));
        }

        Assert.InRange(mailbox.Count, 1, 4);
        var delta = Assert.IsType<AssistantTextDeltaEvent>(await mailbox.ReadAsync());
        Assert.Equal(string.Concat(Enumerable.Range(0, 100)), delta.Delta);
    }

    [Fact]
    public async Task Tool_progress_keeps_latest_elapsed_value()
    {
        using var mailbox = new UiEventMailbox(capacity: 4);
        mailbox.Publish(new ToolProgressEvent("grep", 100));
        mailbox.Publish(new ToolProgressEvent("grep", 200));
        mailbox.Publish(new ToolProgressEvent("grep", 300));

        var progress = Assert.IsType<ToolProgressEvent>(await mailbox.ReadAsync());

        Assert.Equal(300, progress.ElapsedMs);
    }

    [Fact]
    public async Task Completion_and_error_events_are_not_coalesced()
    {
        using var mailbox = new UiEventMailbox(capacity: 4);
        mailbox.Publish(new AssistantTextCompletedEvent());
        mailbox.Publish(new AgentErrorEvent("boom"));

        Assert.IsType<AssistantTextCompletedEvent>(await mailbox.ReadAsync());
        Assert.IsType<AgentErrorEvent>(await mailbox.ReadAsync());
    }

    [Fact]
    public async Task Critical_event_evicts_old_progress_instead_of_blocking_when_full()
    {
        using var mailbox = new UiEventMailbox(capacity: 2);
        mailbox.Publish(new ToolProgressEvent("a", 100));
        mailbox.Publish(new ToolProgressEvent("b", 100));

        mailbox.Publish(new AgentErrorEvent("boom"));

        Assert.Equal(2, mailbox.Count);
        var drained = new[] { await mailbox.ReadAsync(), await mailbox.ReadAsync() };
        Assert.Contains(drained, item => item is AgentErrorEvent);
    }

    [Fact]
    public async Task Actor_drains_streaming_burst_into_one_frame()
    {
        using var mailbox = new UiEventMailbox(capacity: 128);
        for (var index = 0; index < 100; index++)
        {
            mailbox.Publish(new AssistantTextDeltaEvent("x"));
        }

        using var cts = new CancellationTokenSource();
        var sink = new CancellingFrameSink(cts);
        var actor = new UiActor(mailbox, sink, UiSessionSnapshot.Empty);

        await actor.RunAsync(cts.Token);

        var snapshot = Assert.Single(sink.Snapshots);
        var assistant = Assert.IsType<AssistantTranscriptBlock>(Assert.Single(snapshot.Transcript));
        Assert.Equal(new string('x', 100), assistant.Text);
    }

    private sealed class CancellingFrameSink(CancellationTokenSource cts) : IUiFrameSink
    {
        public List<UiSessionSnapshot> Snapshots { get; } = [];

        public ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            this.Snapshots.Add(snapshot);
            cts.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
```

- [ ] **Step 2: Run the mailbox tests to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiEventMailboxTests"
```

Expected: build fails because `UiEventMailbox` does not exist.

- [ ] **Step 3: Implement bounded merge semantics**

`UiEventMailbox` uses a `LinkedList<UiEvent>`, a dictionary from coalescing key to linked-list node, a lock, and two `SemaphoreSlim` instances (`items`, `spaces`). It implements `IUiEventPublisher` and `IDisposable`.

Coalescing keys:

```csharp
private static string? CoalescingKey(UiEvent uiEvent) => uiEvent switch
{
    AssistantTextDeltaEvent => "assistant",
    ToolProgressEvent progress => $"tool:{progress.ToolName}",
    _ => null,
};
```

Merge:

```csharp
private static UiEvent Merge(UiEvent current, UiEvent next) => (current, next) switch
{
    (AssistantTextDeltaEvent left, AssistantTextDeltaEvent right) =>
        new AssistantTextDeltaEvent(left.Delta + right.Delta),
    (ToolProgressEvent, ToolProgressEvent right) => right,
    _ => next,
};
```

`Publish` first replaces an existing keyed node under the lock. When full, a new coalescible event or a critical non-coalescible event evicts the oldest coalescible node; only a queue containing exclusively critical events waits for `spaces`. The host cancellation token passed to the constructor releases blocked producers when rendering stops. `ReadAsync` removes the oldest node, removes its key entry, releases `spaces`, and returns it. `TryRead` performs the same removal without waiting. Count never exceeds capacity.

- [ ] **Step 4: Implement the actor**

Use these exact contracts:

```csharp
namespace Coda.Tui.Ui.Events;

public interface IUiFrameSink
{
    ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IUiEventObserver
{
    ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken);
}

public sealed class NullUiFrameSink : IUiFrameSink
{
    public static NullUiFrameSink Instance { get; } = new();
    public ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class UiActor
{
    public UiActor(
        UiEventMailbox mailbox,
        IUiFrameSink frameSink,
        UiSessionSnapshot initial,
        IUiEventObserver? eventObserver = null);

    public UiSessionSnapshot Current { get; }
    public Task RunAsync(CancellationToken cancellationToken);
}
```

The actor is the only mailbox reader. After one awaited read, it drains the currently available burst through `TryRead`, observing each event and reducing all of them before one frame application. It limits non-critical streaming frames to 30 FPS; completion, error, permission, cancellation, prompt response, and mode/session transitions apply immediately. It skips `ApplyAsync` when the snapshot value did not change and propagates renderer exceptions to `TuiHost`. Task 6 adds prompt-response resolution once prompt types exist.

- [ ] **Step 5: Run mailbox tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiEventMailboxTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 6: Commit**

```powershell
git add src/Coda.Tui/Ui/Events/UiEventMailbox.cs src/Coda.Tui/Ui/Events/UiActor.cs tests/Coda.Tui.Tests/UiEventMailboxTests.cs
git commit -m "feat(tui): add bounded UI actor mailbox" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: Add the offscreen Spectre adapter and plain event renderer

**Files:**
- Create: `src/Coda.Tui/Ui/Rendering/OffscreenAnsiConsoleOutput.cs`
- Create: `src/Coda.Tui/Ui/Rendering/UiAnsiConsoleAdapter.cs`
- Create: `src/Coda.Tui/Ui/Rendering/PlainOutputRenderer.cs`
- Test: `tests/Coda.Tui.Tests/UiAnsiConsoleAdapterTests.cs`
- Test: `tests/Coda.Tui.Tests/PlainOutputRendererTests.cs`

- [ ] **Step 1: Write failing adapter and plain-renderer tests**

```csharp
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Rendering;
using Spectre.Console;

namespace Coda.Tui.Tests;

public sealed class UiAnsiConsoleAdapterTests
{
    [Fact]
    public void Spectre_renderables_become_plain_typed_command_output()
    {
        var events = new List<UiEvent>();
        var console = new UiAnsiConsoleAdapter(new CollectingPublisher(events), 80, 24);

        console.Write(new Panel(new Markup("[red]hello[/]")));

        var output = Assert.IsType<CommandOutputEvent>(Assert.Single(events));
        Assert.Contains("hello", output.Text);
        Assert.DoesNotContain("\u001b[", output.Text);
    }

    [Fact]
    public void Clear_publishes_semantic_clear_instead_of_escape_sequences()
    {
        var events = new List<UiEvent>();
        var console = new UiAnsiConsoleAdapter(new CollectingPublisher(events), 80, 24);

        console.Clear(home: true);

        Assert.IsType<ConsoleClearRequestedEvent>(Assert.Single(events));
    }

    private sealed class CollectingPublisher(List<UiEvent> events) : IUiEventPublisher
    {
        public void Publish(UiEvent uiEvent) => events.Add(uiEvent);
    }
}
```

```csharp
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Tests;

public sealed class PlainOutputRendererTests
{
    [Fact]
    public async Task Plain_output_contains_no_cursor_or_alternate_screen_sequences()
    {
        var writer = new StringWriter();
        var renderer = new PlainOutputRenderer(writer);

        await renderer.ApplyEventAsync(new AssistantTextDeltaEvent("hello"), CancellationToken.None);
        await renderer.ApplyEventAsync(new AssistantTextCompletedEvent(), CancellationToken.None);
        await renderer.ApplyEventAsync(new ToolStartedEvent("grep", "{}"), CancellationToken.None);
        await renderer.ApplyEventAsync(new AgentErrorEvent("boom"), CancellationToken.None);

        Assert.Equal(
            "hello" + Environment.NewLine +
            "[tool] grep {}" + Environment.NewLine +
            "[error] boom" + Environment.NewLine,
            writer.ToString());
        Assert.DoesNotContain("\u001b[", writer.ToString());
    }
}
```

- [ ] **Step 2: Run targeted tests to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiAnsiConsoleAdapterTests|FullyQualifiedName~PlainOutputRendererTests"
```

Expected: build fails because the adapter/output classes do not exist.

- [ ] **Step 3: Implement the fixed offscreen output and `IAnsiConsole` adapter**

`OffscreenAnsiConsoleOutput` implements `IAnsiConsoleOutput` with:

```csharp
public TextWriter Writer { get; }
public bool IsTerminal => false;
public int Width { get; }
public int Height { get; }
public void SetEncoding(Encoding encoding) { }
```

`UiAnsiConsoleAdapter` implements every member of Spectre's `IAnsiConsole`:

```csharp
public sealed class UiAnsiConsoleAdapter : IAnsiConsole
{
    public UiAnsiConsoleAdapter(IUiEventPublisher publisher, int width, int height);

    public Profile Profile => this.inner.Profile;
    public IAnsiConsoleCursor Cursor => this.inner.Cursor;
    public IAnsiConsoleInput Input => this.inner.Input;
    public IExclusivityMode ExclusivityMode => this.inner.ExclusivityMode;
    public RenderPipeline Pipeline => this.inner.Pipeline;

    public void Clear(bool home);
    public void Write(IRenderable renderable);
    public void WriteAnsi(Action<AnsiWriter> action);
}
```

Construct the inner console with:

```csharp
AnsiConsole.Create(new AnsiConsoleSettings
{
    Ansi = AnsiSupport.No,
    ColorSystem = ColorSystemSupport.NoColors,
    Interactive = InteractionSupport.No,
    Out = output,
});
```

After each `Write`/`WriteAnsi`, drain the buffer, remove CSI/OSC escapes with a compiled regex (`\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))`), normalize CRLF/LF without trimming intentional interior whitespace, and publish one `CommandOutputEvent` when non-empty. `Clear` publishes `ConsoleClearRequestedEvent` and does not call the inner clear method.

- [ ] **Step 4: Implement stable plain rendering**

`PlainOutputRenderer` must accept individual events and write:

- assistant deltas verbatim; newline only on assistant completion,
- `[tool] {name} {input}`,
- `[tool-progress] {name} {seconds:0.0}s`,
- `[tool-result] {name}: {content}`,
- `[warning]`, `[error]`, `[limit:{kind}]`, and `[stop]`,
- `[diagnostic:{source}] {message}`,
- diff patch text verbatim with no color/control sequences,
- command output as plain text with one trailing newline,
- no output for frame-only mode/status/context events.

Expose:

```csharp
public sealed class PlainOutputRenderer : IUiEventObserver
{
    public PlainOutputRenderer(TextWriter writer);
    public ValueTask ApplyEventAsync(UiEvent uiEvent, CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Run targeted tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiAnsiConsoleAdapterTests|FullyQualifiedName~PlainOutputRendererTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 6: Commit**

```powershell
git add src/Coda.Tui/Ui/Rendering tests/Coda.Tui.Tests/UiAnsiConsoleAdapterTests.cs tests/Coda.Tui.Tests/PlainOutputRendererTests.cs
git commit -m "feat(tui): adapt Spectre output into UI events" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: Add actor, Spectre, and plain prompt services and migrate agent prompts

**Files:**
- Create: `src/Coda.Tui/Ui/Prompts/UiPromptModels.cs`
- Create: `src/Coda.Tui/Ui/Prompts/IUiPromptService.cs`
- Create: `src/Coda.Tui/Ui/Prompts/ActorUiPromptService.cs`
- Create: `src/Coda.Tui/Ui/Prompts/SpectreUiPromptService.cs`
- Create: `src/Coda.Tui/Ui/Prompts/PlainUiPromptService.cs`
- Modify: `src/Coda.Tui/Ui/Events/UiEvent.cs`
- Modify: `src/Coda.Tui/Ui/Events/UiActor.cs`
- Modify: `src/Coda.Tui/Ui/State/UiSessionSnapshot.cs`
- Modify: `src/Coda.Tui/Ui/State/UiReducer.cs`
- Modify: `src/Coda.Tui/Repl/CommandContext.cs`
- Modify: `src/Coda.Tui/Agent/TuiPermissionPrompt.cs`
- Modify: `src/Coda.Tui/Agent/TuiPlanApprover.cs`
- Modify: `src/Coda.Tui/Agent/TuiUserQuestionPrompt.cs`
- Modify: `tests/Coda.Tui.Tests/TestAppBuilder.cs`
- Create: `tests/Coda.Tui.Tests/UiPromptServiceTests.cs`
- Create: `tests/Coda.Tui.Tests/RecordingPromptService.cs`

- [ ] **Step 1: Write failing prompt-service tests**

```csharp
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class UiPromptServiceTests
{
    [Fact]
    public async Task Actor_prompt_round_trips_through_request_and_response_events()
    {
        using var mailbox = new UiEventMailbox(8);
        var service = new ActorUiPromptService(mailbox);
        var requestTask = service.RequestAsync(UiPromptRequest.Confirm("Delete?", defaultValue: false));
        var requested = Assert.IsType<UiPromptRequestedEvent>(await mailbox.ReadAsync());

        service.Complete(new UiPromptResponseSubmittedEvent(
            requested.Request.Id,
            new UiPromptResponse(false, ["yes"], null)));

        var response = await requestTask;
        Assert.Equal(["yes"], response.SelectedIds);
    }

    [Fact]
    public async Task Plain_prompt_denies_confirmation_and_cancels_selection()
    {
        var service = PlainUiPromptService.Instance;

        var confirm = await service.RequestAsync(UiPromptRequest.Confirm("Allow?", defaultValue: false));
        var select = await service.RequestAsync(UiPromptRequest.Select("Choose", [new("a", "A")]));

        Assert.Equal(["no"], confirm.SelectedIds);
        Assert.True(select.Cancelled);
    }

    [Fact]
    public async Task Spectre_prompt_remains_available_for_fallback()
    {
        using var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushKey(ConsoleKey.Enter);
        var service = new SpectreUiPromptService(console);

        var response = await service.RequestAsync(
            UiPromptRequest.Select("Choose", [new("a", "A"), new("b", "B")]));

        Assert.Equal(["a"], response.SelectedIds);
    }
}
```

Create a reusable test fake:

```csharp
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Tests;

internal sealed class RecordingPromptService : IUiPromptService
{
    private readonly Queue<UiPromptResponse> responses;

    public RecordingPromptService(params UiPromptResponse[] responses)
    {
        this.responses = new(responses);
    }

    public bool IsInteractive => true;
    public List<UiPromptRequest> Requests { get; } = [];

    public Task<UiPromptResponse> RequestAsync(UiPromptRequest request, CancellationToken cancellationToken = default)
    {
        this.Requests.Add(request);
        return Task.FromResult(this.responses.Dequeue());
    }
}
```

- [ ] **Step 2: Run prompt tests to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiPromptServiceTests"
```

Expected: build fails because prompt models/services and prompt events do not exist.

- [ ] **Step 3: Add prompt contracts and actor events**

Use:

```csharp
using System.Collections.Immutable;

namespace Coda.Tui.Ui.Prompts;

public enum UiPromptKind
{
    Confirm,
    SelectOne,
    SelectMany,
    Text,
    Secret,
}

public sealed record UiPromptOption(string Id, string Label, string? Detail = null);

public sealed record UiPromptRequest(
    Guid Id,
    UiPromptKind Kind,
    string Title,
    string? Message,
    ImmutableArray<UiPromptOption> Options,
    string? DefaultValue,
    bool Required)
{
    public static UiPromptRequest Confirm(string title, bool defaultValue) =>
        new(Guid.NewGuid(), UiPromptKind.Confirm, title, null, [new("yes", "Yes"), new("no", "No")], defaultValue ? "yes" : "no", true);

    public static UiPromptRequest Select(string title, IEnumerable<UiPromptOption> options) =>
        new(Guid.NewGuid(), UiPromptKind.SelectOne, title, null, [.. options], null, true);

    public static UiPromptRequest Text(string title, string? defaultValue = null, bool required = false, bool secret = false) =>
        new(Guid.NewGuid(), secret ? UiPromptKind.Secret : UiPromptKind.Text, title, null, [], defaultValue, required);
}

public sealed record UiPromptResponse(
    bool Cancelled,
    ImmutableArray<string> SelectedIds,
    string? Text);
```

```csharp
namespace Coda.Tui.Ui.Prompts;

public interface IUiPromptService
{
    bool IsInteractive { get; }
    Task<UiPromptResponse> RequestAsync(UiPromptRequest request, CancellationToken cancellationToken = default);
}
```

Add:

```csharp
public sealed record UiPromptRequestedEvent(UiPromptRequest Request) : UiEvent;
public sealed record UiPromptResponseSubmittedEvent(Guid RequestId, UiPromptResponse Response) : UiEvent;
```

`ActorUiPromptService` stores `TaskCompletionSource<UiPromptResponse>` by request id, publishes `UiPromptRequestedEvent`, and removes/completes it in `Complete`. Cancellation removes the entry and cancels only that prompt task. Add `UiPromptRequest? PendingPrompt` to `UiSessionSnapshot`; the reducer sets it on request and clears the matching id on response. Add optional `ActorUiPromptService? prompts = null` as the final `UiActor` constructor parameter so Task 4 callers remain source-compatible; when present, `UiPromptResponseSubmittedEvent` is passed to `Complete` before reduction.

`SpectreUiPromptService` maps:

- confirm → `console.Confirm`,
- select one → `SelectionPrompt<UiPromptOption>`,
- select many → `MultiSelectionPrompt<UiPromptOption>`,
- text → `TextPrompt<string>`,
- secret → `TextPrompt<string>().Secret()`.

`PlainUiPromptService` returns deny/cancel without reading stdin.

- [ ] **Step 4: Inject services into `CommandContext` without breaking existing tests**

Extend the constructor with optional final parameters:

```csharp
IUiPromptService? prompts = null,
IUiEventPublisher? events = null,
ContextSnapshotCache? contextSnapshots = null,
GitStatusCache? gitStatus = null,
Func<UiSessionSnapshot>? uiSnapshotProvider = null,
bool semanticUiEnabled = false
```

Expose:

```csharp
public IUiPromptService Prompts { get; }
public IUiEventPublisher Events { get; }
public ContextSnapshotCache? ContextSnapshots { get; set; }
public GitStatusCache? GitStatus { get; set; }
public Func<UiSessionSnapshot>? UiSnapshotProvider { get; set; }
public bool SemanticUiEnabled { get; }
```

Defaults are `PlainUiPromptService.Instance` and `NullUiEventPublisher.Instance`. Update `TestAppBuilder.BuildApp` to accept optional prompt/event arguments and pass them through.

- [ ] **Step 5: Refactor the three agent prompt adapters**

Required constructor/signature changes:

```csharp
public sealed class TuiPermissionPrompt(IUiPromptService prompts, IUiEventPublisher events) : IPermissionPrompt;
public sealed class TuiPlanApprover(IUiPromptService prompts, IUiEventPublisher events, SessionState session) : IPlanApprover;
public sealed class TuiUserQuestionPrompt(IUiPromptService prompts, IUiEventPublisher events) : IUserQuestionPrompt;
```

Permission publishes `PermissionRequestedEvent`/`PermissionResolvedEvent` and maps yes/no to `bool`; non-interactive plain denies. Plan approval publishes `PlanApprovalRequestedEvent`/`PlanApprovalResolvedEvent` and sets `PermissionMode.AcceptEdits` only after a yes response. User questions publish `UserQuestionRequestedEvent`/`UserQuestionResolvedEvent`, preserve option order, and join multi-select labels with `", "`.

- [ ] **Step 6: Run prompt tests and current command tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiPromptServiceTests|FullyQualifiedName~CommandDispatchTests|FullyQualifiedName~SetupAndModelTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 7: Commit**

```powershell
git add src/Coda.Tui/Ui/Prompts src/Coda.Tui/Ui/Events/UiEvent.cs src/Coda.Tui/Ui/Events/UiActor.cs src/Coda.Tui/Ui/State/UiSessionSnapshot.cs src/Coda.Tui/Ui/State/UiReducer.cs src/Coda.Tui/Repl/CommandContext.cs src/Coda.Tui/Agent/TuiPermissionPrompt.cs src/Coda.Tui/Agent/TuiPlanApprover.cs src/Coda.Tui/Agent/TuiUserQuestionPrompt.cs tests/Coda.Tui.Tests/TestAppBuilder.cs tests/Coda.Tui.Tests/UiPromptServiceTests.cs tests/Coda.Tui.Tests/RecordingPromptService.cs
git commit -m "feat(tui): add host-neutral prompt services" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: Refactor every interactive command/setup prompt away from direct Spectre prompts

**Files:**
- Modify: `src/Coda.Tui/TuiApp.cs`
- Modify: `src/Coda.Tui/Setup/SetupWizard.cs`
- Modify: `src/Coda.Tui/Commands/LoginCommand.cs`
- Modify: `src/Coda.Tui/Commands/ProviderCommand.cs`
- Modify: `src/Coda.Tui/Commands/ModelCommand.cs`
- Modify: `src/Coda.Tui/Commands/ResumeCommand.cs`
- Modify: `src/Coda.Tui/Commands/McpCommand.cs`
- Modify: `src/Coda.Tui/Commands/DiffCommand.cs`
- Modify: `src/Coda.Tui/Commands/MarketplaceCommand.cs`
- Modify: `src/Coda.Tui/Commands/PluginCommand.cs`
- Test: `tests/Coda.Tui.Tests/PromptDrivenCommandTests.cs`

- [ ] **Step 1: Write failing command prompt tests**

Use a single integration test file with these exact cases:

```csharp
using Coda.Mcp;
using Coda.Sdk;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Setup;
using Coda.Tui.Ui.Prompts;
using LlmAuth;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class PromptDrivenCommandTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_prompt_").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Bare_slash_uses_prompt_service_command_picker()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["help"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        await built.App.DispatchAsync(CommandParser.Parse("/"));

        Assert.Equal("Select a command", Assert.Single(prompts.Requests).Title);
        Assert.Contains("Commands", built.Console.Output);
    }

    [Fact]
    public async Task Setup_uses_prompt_service_provider_picker()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["anthropic-api-key"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        var provider = await SetupWizard.ChooseProviderAsync(built.Context);

        Assert.Equal("Choose a provider", Assert.Single(prompts.Requests).Title);
        Assert.Equal("anthropic-api-key", provider?.Id);
    }

    [Fact]
    public async Task Provider_without_id_uses_picker()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["anthropic-api-key"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        await new ProviderCommand().ExecuteAsync(built.Context, []);

        Assert.Equal("Choose a provider", Assert.Single(prompts.Requests).Title);
    }

    [Fact]
    public async Task Model_without_id_uses_cached_models_picker()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["model-b"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts);
        var models = new ModelListResult(
            built.Context.ActiveProvider.Id,
            ModelSource.Catalog,
            [new ModelListEntry("model-a", "A", 200_000), new ModelListEntry("model-b", "B", 200_000)]);

        var selected = await ModelCommand.ChooseModelAsync(built.Context, models);

        Assert.Equal("model-b", selected);
        Assert.Equal("Choose a model", Assert.Single(prompts.Requests).Title);
    }

    [Fact]
    public async Task Resume_without_id_prompts_with_recent_session_ids()
    {
        await new SessionTranscriptStore(this.tempDir).SaveAsync(
            "session-a",
            [new ChatMessage(ChatRole.User, [new TextBlock("hello")])]);
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["session-a"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);

        await new ResumeCommand().ExecuteAsync(built.Context, []);

        Assert.Equal("session-a", built.Context.Session.SessionId);
        Assert.Single(built.Context.Session.History);
        Assert.Equal("Choose a session", Assert.Single(prompts.Requests).Title);
    }

    [Fact]
    public async Task Login_copilot_prompts_for_deployment_and_enterprise_domain()
    {
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ["enterprise"], null),
            new UiPromptResponse(false, [], "octocorp.ghe.com"));
        var built = TestAppBuilder.BuildApp(prompts: prompts);

        var selection = await LoginCommand.PromptCopilotDeploymentAsync(
            built.Context,
            currentEnterpriseDomain: null,
            CancellationToken.None);

        Assert.False(selection.Cancelled);
        Assert.Equal("octocorp.ghe.com", selection.EnterpriseDomain);
        Assert.Equal(["Which GitHub Copilot deployment", "GitHub Enterprise domain"], prompts.Requests.Select(request => request.Title));
    }

    [Fact]
    public async Task Mcp_wizard_prompts_for_http_secret_storage()
    {
        var prompts = new RecordingPromptService(
            new UiPromptResponse(false, ["http"], null),
            new UiPromptResponse(false, [], "https://example.com/mcp"),
            new UiPromptResponse(false, [], string.Empty),
            new UiPromptResponse(false, ["bearer"], null),
            new UiPromptResponse(false, [], "super-secret"),
            new UiPromptResponse(false, ["yes"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);
        built.Context.CredentialStore = new InMemoryTokenStore();

        var config = Assert.IsType<McpHttpServerConfig>(
            await McpCommand.RunWizardAsync(built.Context, "demo", CancellationToken.None));

        Assert.Equal(new Uri("https://example.com/mcp"), config.Url);
        var token = Assert.IsType<string>(config.Auth.BearerToken);
        Assert.StartsWith(McpSecretResolver.SecretRefPrefix, token);
        Assert.DoesNotContain("super-secret", token);
    }

    [Fact]
    public async Task Marketplace_missing_action_is_collected_through_prompt_service()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["list"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);

        await new MarketplaceCommand(Path.Combine(this.tempDir, "plugins")).ExecuteAsync(built.Context, []);

        Assert.Equal("Marketplace action", Assert.Single(prompts.Requests).Title);
        Assert.Contains("No marketplaces added", built.Console.Output);
    }

    [Fact]
    public async Task Plugin_missing_action_is_collected_through_prompt_service()
    {
        var prompts = new RecordingPromptService(new UiPromptResponse(false, ["list"], null));
        var built = TestAppBuilder.BuildApp(prompts: prompts, workingDirectory: this.tempDir);

        await new PluginCommand(Path.Combine(this.tempDir, "plugins")).ExecuteAsync(built.Context, []);

        Assert.Equal("Plugin action", Assert.Single(prompts.Requests).Title);
        Assert.Contains("No plugins installed", built.Console.Output);
    }
}
```

- [ ] **Step 2: Run the prompt-driven command tests to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~PromptDrivenCommandTests"
```

Expected: tests fail because commands still call Spectre directly or list usage instead of requesting typed prompts.

- [ ] **Step 3: Replace the current direct prompt sites**

Repository-search inventory that must be empty after this task:

```powershell
rg -n "SelectionPrompt|MultiSelectionPrompt|TextPrompt|\.Confirm\(|\.Ask<|\.Prompt\(" src/Coda.Tui --glob "*.cs" --glob "!Ui/Prompts/SpectreUiPromptService.cs"
```

Expected after refactor: no matches.

Required request ids/labels:

- Bare `/`: option id is slash-command name; title `Select a command`.
- Setup/login/provider: option id is provider id; title `Choose a provider`.
- Copilot deployment: ids `public` and `enterprise`; enterprise domain uses a required text request.
- Model: option id is model id; details contain display name and context limit.
- Resume: option id is session id; details contain message count, age, and preview.
- MCP action/server/transport/auth: stable lowercase ids matching existing command tokens.
- MCP pair entry: repeated text request, empty text terminates; encryption uses a confirmation request.
- Marketplace action: `list`, `add`, `remove`, `browse`, `install`.
- Plugin action: `list`, `install`, `remove`.

Typed command arguments still bypass prompts. Plain mode still prints usage/lists and never waits for a prompt.

- [ ] **Step 4: Make picker helpers testable and cancellation-safe**

Every helper returns `null`/`CommandResult.Continue` on `response.Cancelled`; it makes no session/config mutation before a response is accepted. Add these exact internal seams:

```csharp
private async Task<string?> ShowCommandMenuAsync(CancellationToken cancellationToken)

internal static Task<ProviderDescriptor?> ChooseProviderAsync(
    CommandContext context,
    CancellationToken cancellationToken = default)

internal sealed record CopilotDeploymentSelection(bool Cancelled, string? EnterpriseDomain);

internal static Task<CopilotDeploymentSelection> PromptCopilotDeploymentAsync(
    CommandContext context,
    string? currentEnterpriseDomain,
    CancellationToken cancellationToken)

internal static Task<string?> ChooseModelAsync(
    CommandContext context,
    ModelListResult models,
    CancellationToken cancellationToken = default)

internal static Task<McpServerConfig?> RunWizardAsync(
    CommandContext context,
    string name,
    CancellationToken cancellationToken)

internal static Task<string?> ChooseActionAsync(
    CommandContext context,
    CancellationToken cancellationToken = default)
```

`SetupWizard.ChooseProviderAsync`, `LoginCommand.PromptCopilotDeploymentAsync`, `ModelCommand.ChooseModelAsync`, and `McpCommand.RunWizardAsync` own the signatures above. `MarketplaceCommand` and `PluginCommand` each define their own `ChooseActionAsync`. Extend `TestAppBuilder.BuildApp` with optional `IUiPromptService? prompts`, `IUiEventPublisher? events`, and `string? workingDirectory`; pass the directory into `SessionState`. Preserve existing validation strings and encrypted MCP secret handling.

- [ ] **Step 5: Run prompt tests plus affected regressions**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~PromptDrivenCommandTests|FullyQualifiedName~SetupAndModelTests|FullyQualifiedName~ResumeRewindCommandTests|FullyQualifiedName~McpCommandTests|FullyQualifiedName~MarketplaceCommandTests|FullyQualifiedName~PluginInstallTests|FullyQualifiedName~PluginsTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 6: Verify the prompt inventory**

Run:

```powershell
rg -n "SelectionPrompt|MultiSelectionPrompt|TextPrompt|\.Confirm\(|\.Ask<|\.Prompt\(" src/Coda.Tui --glob "*.cs" --glob "!Ui/Prompts/SpectreUiPromptService.cs"
```

Expected: no output.

- [ ] **Step 7: Commit**

```powershell
git add src/Coda.Tui/TuiApp.cs src/Coda.Tui/Setup/SetupWizard.cs src/Coda.Tui/Commands/LoginCommand.cs src/Coda.Tui/Commands/ProviderCommand.cs src/Coda.Tui/Commands/ModelCommand.cs src/Coda.Tui/Commands/ResumeCommand.cs src/Coda.Tui/Commands/McpCommand.cs src/Coda.Tui/Commands/MarketplaceCommand.cs src/Coda.Tui/Commands/PluginCommand.cs tests/Coda.Tui.Tests/PromptDrivenCommandTests.cs
git commit -m "refactor(tui): route interactive prompts through UI service" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: Forward every agent event and refresh semantic runtime/context state after turns

**Files:**
- Modify: `src/Coda.Tui/TuiApp.cs`
- Modify: `src/Coda.Tui/Agent/TuiAgentSink.cs`
- Modify: `src/Coda.Tui/Agent/AgentRunner.cs`
- Modify: `src/Coda.Tui/Commands/ContextCommand.cs`
- Modify: `src/Coda.Tui/Commands/StatusCommand.cs`
- Modify: `src/Coda.Tui/Commands/ClearCommand.cs`
- Modify: `src/Coda.Tui/Commands/CostCommand.cs`
- Modify: `src/Coda.Tui/Commands/EffortCommand.cs`
- Modify: `src/Coda.Tui/Commands/PermissionsCommand.cs`
- Modify: `src/Coda.Tui/Commands/YoloCommand.cs`
- Modify: `src/Coda.Tui/Commands/ForkCommand.cs`
- Modify: `src/Coda.Tui/Commands/ProviderCommand.cs`
- Modify: `src/Coda.Tui/Commands/ModelCommand.cs`
- Modify: `src/Coda.Tui/Commands/ResumeCommand.cs`
- Modify: `src/Coda.Tui/Commands/McpCommand.cs`
- Test: `tests/Coda.Tui.Tests/TuiAgentSinkTests.cs`

- [ ] **Step 1: Write the failing sink forwarding test**

```csharp
using Coda.Agent;
using Coda.Tui.Agent;
using Coda.Tui.Ui.Events;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class TuiAgentSinkTests
{
    [Fact]
    public void Sink_forwards_every_IAgentSink_event()
    {
        var events = new List<UiEvent>();
        var sink = new TuiAgentSink(new CollectingPublisher(events));
        var result = new ToolResult("ok", false);

        sink.OnAssistantText("a");
        sink.OnAssistantTextComplete();
        sink.OnToolCall("grep", "{}");
        sink.OnToolProgress("grep", 1234);
        sink.OnToolResult("grep", result);
        sink.OnUsage(new TokenUsage(10, 2));
        sink.OnStopReason("end_turn");
        sink.OnLimitReached("max_tokens", "limit");
        sink.OnError("boom");

        Assert.Collection(
            events,
            item => Assert.IsType<AssistantTextDeltaEvent>(item),
            item => Assert.IsType<AssistantTextCompletedEvent>(item),
            item => Assert.IsType<ToolStartedEvent>(item),
            item => Assert.IsType<ToolProgressEvent>(item),
            item => Assert.IsType<ToolCompletedEvent>(item),
            item => Assert.IsType<UsageEvent>(item),
            item => Assert.IsType<StopReasonEvent>(item),
            item => Assert.IsType<LimitReachedEvent>(item),
            item => Assert.IsType<AgentErrorEvent>(item));
    }

    private sealed class CollectingPublisher(List<UiEvent> events) : IUiEventPublisher
    {
        public void Publish(UiEvent uiEvent) => events.Add(uiEvent);
    }
}
```

- [ ] **Step 2: Run the sink test to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiAgentSinkTests"
```

Expected: test fails because the sink still writes to `IAnsiConsole` and does not forward progress, usage, or stop reason.

- [ ] **Step 3: Replace rendering with typed publication**

The constructor becomes:

```csharp
public TuiAgentSink(IUiEventPublisher publisher)
```

Each `IAgentSink` method publishes exactly one matching event. Remove `wroteText`, Spectre markup, truncation, and all direct console writes from this class.

- [ ] **Step 4: Integrate the sink and runtime updates in `AgentRunner`**

`AgentRunner.RunAsync` must:

1. Publish `UserPromptSubmittedEvent` and `TurnStartedEvent`.
2. Create `TuiAgentSink(context.Events)`.
3. Build permission/question/plan adapters from `context.Prompts`.
4. Keep a linked per-turn `CancellationTokenSource` exposed through `TryInterruptActiveTurn`.
5. On completion, copy `CodaSession.SessionUsage` to `SessionState`, publish `SessionRuntimeChangedEvent(session.GetRuntimeSnapshot())`, invalidate and refresh `ContextSnapshotCache`, then publish `ContextChangedEvent`.
6. Invalidate and refresh `GitStatusCache`, then publish `GitChangedEvent`.
7. Compute `Pricing.EstimateUsd` with the same model/catalog inputs as `/cost` and publish `CostEstimateChangedEvent`.
8. Publish `SessionMetadataChangedEvent` using `EffortSupport.ResolveAppliedEffort(model, requested) ?? "auto"`.
9. Publish `TurnCompletedEvent`; publish `TurnInterruptedEvent` for cancellation.
10. Render failure text through `context.Console`; under Terminal.Gui this flows through `UiAnsiConsoleAdapter`.

Expose:

```csharp
public bool HasActiveTurn { get; }
public bool TryInterruptActiveTurn();
public SessionRuntimeSnapshot? GetRuntimeSnapshot();
```

Change `TuiApp` construction to accept a shared runner:

```csharp
public TuiApp(
    CommandContext context,
    Func<IReadOnlyList<ITool>>? mcpToolsProvider = null,
    IShellExecutor? shellExecutor = null,
    AgentRunner? agentRunner = null)
```

Track whether `TuiApp` created the runner so it disposes only owned instances. `InteractiveProgram` creates one `AgentRunner` and passes the same instance to `TuiApp` and `TuiController`, allowing Ctrl-C and mode switches to operate on the live turn.

- [ ] **Step 5: Change context/status/clear commands**

- `/context` calls `await context.ContextSnapshots.GetAsync(force: true, cancellationToken)` and renders the returned report. It falls back to the existing one-shot analysis only when the cache is absent in tests/legacy fallback.
- `/status` reads the current immutable UI/runtime snapshot provider from `CommandContext`; it does not enumerate `McpClientManager.Clients`, `LspServerManager`, task stores, or frame controls.
- `/clear` resets session history/usage/id, publishes `TranscriptClearedEvent(newId)` and `SessionRuntimeChangedEvent`, then renders the banner through the adapter. Spectre fallback still clears through `SpectreUiPromptService`/real console because its context uses the real console.
- `/provider`, `/model`, `/effort`, `/permissions`, `/yolo`, `/resume`, `/fork`, and `/clear` publish `SessionMetadataChangedEvent` immediately after mutation.
- `/cost` publishes `CostEstimateChangedEvent` with the exact value it prints.
- MCP add/edit/remove/enable/disable/start/stop/restart publish `McpRuntimeChangedEvent(context.Mcp.GetSnapshot())` after successful mutation; startup publishes the initial MCP snapshot.
- `/diff` publishes `DiffOutputEvent(stdout)` when `CommandContext.SemanticUiEnabled`; Spectre fallback retains its existing `Console.WriteLine(stdout)` path, so output is not duplicated.

- [ ] **Step 6: Run affected tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiAgentSinkTests|FullyQualifiedName~ContextExportCommandTests|FullyQualifiedName~StatusAndHeadersTests|FullyQualifiedName~ClearCommandTests|FullyQualifiedName~SetupAndModelTests|FullyQualifiedName~ResumeRewindCommandTests|FullyQualifiedName~McpCommandTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 7: Commit**

```powershell
git add src/Coda.Tui/TuiApp.cs src/Coda.Tui/Agent/TuiAgentSink.cs src/Coda.Tui/Agent/AgentRunner.cs src/Coda.Tui/Commands/ContextCommand.cs src/Coda.Tui/Commands/StatusCommand.cs src/Coda.Tui/Commands/ClearCommand.cs src/Coda.Tui/Commands/CostCommand.cs src/Coda.Tui/Commands/EffortCommand.cs src/Coda.Tui/Commands/PermissionsCommand.cs src/Coda.Tui/Commands/YoloCommand.cs src/Coda.Tui/Commands/ForkCommand.cs src/Coda.Tui/Commands/ProviderCommand.cs src/Coda.Tui/Commands/ModelCommand.cs src/Coda.Tui/Commands/ResumeCommand.cs src/Coda.Tui/Commands/McpCommand.cs src/Coda.Tui/Commands/DiffCommand.cs tests/Coda.Tui.Tests/TuiAgentSinkTests.cs
git commit -m "feat(tui): publish complete agent runtime events" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 9: Implement named actions and the TextView composer

**Files:**
- Create: `src/Coda.Tui/Ui/Input/UiAction.cs`
- Create: `src/Coda.Tui/Ui/Input/UiActionMap.cs`
- Create: `src/Coda.Tui/Ui/Input/ComposerState.cs`
- Create: `src/Coda.Tui/Ui/Input/ComposerController.cs`
- Create: `src/Coda.Tui/Ui/Input/ComposerView.cs`
- Test: `tests/Coda.Tui.Tests/ComposerControllerTests.cs`
- Test: `tests/Coda.Tui.Tests/ComposerViewTests.cs`

- [ ] **Step 1: Write failing composer behavior tests**

```csharp
using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

public sealed class ComposerControllerTests
{
    [Fact]
    public void Submit_records_history_and_clears_draft()
    {
        var controller = new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([])));
        controller.ReplaceDraft("hello", 5);

        var submitted = controller.Apply(UiAction.Submit);

        Assert.Equal("hello", submitted.SubmittedText);
        Assert.Equal(string.Empty, controller.State.Draft);
        Assert.Equal(["hello"], controller.State.History);
    }

    [Fact]
    public void Paste_with_newlines_never_submits()
    {
        var controller = new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([])));

        controller.BeginPaste();
        controller.InsertText("one\ntwo");
        var action = controller.Apply(UiAction.Submit);
        controller.EndPaste();

        Assert.Null(action.SubmittedText);
        Assert.Equal("one\ntwo", controller.State.Draft);
    }

    [Fact]
    public void Export_import_preserves_draft_cursor_and_history()
    {
        var controller = new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([])));
        controller.ReplaceDraft("draft", 3);
        controller.SeedHistory(["one", "two"]);

        var restored = new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([])));
        restored.Restore(controller.Export());

        Assert.Equal("draft", restored.State.Draft);
        Assert.Equal(3, restored.State.CursorIndex);
        Assert.Equal(["one", "two"], restored.State.History);
    }

    [Fact]
    public void Tab_completes_visible_slash_command()
    {
        var registry = new SlashCommandRegistry([new TestCommand("help", "Show help")]);
        var controller = new ComposerController(new SlashCommandCompletion(registry));
        controller.ReplaceDraft("/he", 3);

        controller.Apply(UiAction.CompleteSuggestion);

        Assert.Equal("/help ", controller.State.Draft);
    }

    private sealed class TestCommand(string name, string summary) : ISlashCommand
    {
        public string Name { get; } = name;
        public IReadOnlyList<string> Aliases => [];
        public string Summary { get; } = summary;
        public CommandHelp Help => new($"/{this.Name}");

        public Task<CommandResult> ExecuteAsync(
            CommandContext context,
            IReadOnlyList<string> args,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CommandResult.Continue);
    }
}
```

```csharp
using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;

namespace Coda.Tui.Tests;

public sealed class ComposerViewTests
{
    [Fact]
    public void Enter_submits_but_ctrl_j_inserts_newline()
    {
        var controller = new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([])));
        using var view = new ComposerView(controller);
        string? submitted = null;
        view.Submitted += (_, text) => submitted = text;
        view.SetDraft("hello", 5);

        view.NewKeyDownEvent(Key.J.WithCtrl);
        Assert.Equal("hello\n", view.GetDraft());

        view.NewKeyDownEvent(Key.Enter);
        Assert.Equal("hello\n", submitted);
    }
}
```

- [ ] **Step 2: Run composer tests to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ComposerControllerTests|FullyQualifiedName~ComposerViewTests"
```

Expected: build fails because composer/action classes do not exist.

- [ ] **Step 3: Define named actions and mapping**

`UiAction` includes:

```csharp
public enum UiAction
{
    None,
    Submit,
    InsertNewline,
    Interrupt,
    Exit,
    CursorLeft,
    CursorRight,
    WordLeft,
    WordRight,
    LineStart,
    LineEnd,
    HistoryPrevious,
    HistoryNext,
    CompletionPrevious,
    CompletionNext,
    CompleteSuggestion,
    DismissCompletion,
    OpenCommandPalette,
    OpenModelPicker,
    OpenSessionPicker,
    OpenMcpStatus,
    TranscriptUp,
    TranscriptDown,
    JumpToNewest,
    ToggleMode,
    ForceRedraw,
}
```

`UiActionMap.Map(Key key, UiInputContext context)` maps Enter→Submit, Ctrl+J→InsertNewline, Ctrl+C→Interrupt, Ctrl+D→Exit when composer is empty, Up/Down→completion selection while completion is open and history otherwise, Tab→complete suggestion, Escape→dismiss completion, PageUp/PageDown→transcript, F2→toggle mode, Ctrl+L→redraw, and leaves ordinary Unicode keys to `TextView`.

- [ ] **Step 4: Implement transferable composer state/controller**

```csharp
using System.Collections.Immutable;

namespace Coda.Tui.Ui.Input;

public sealed record ComposerState(
    string Draft,
    int CursorIndex,
    ImmutableArray<string> History,
    int HistoryIndex,
    bool PasteActive)
{
    public static ComposerState Empty { get; } = new(string.Empty, 0, [], 0, false);
}

public sealed record ComposerActionResult(string? SubmittedText, bool RequestRedraw);
```

`ComposerController` clamps cursor indexes, deduplicates consecutive history entries, preserves drafts while navigating history, delegates slash suggestions to existing `SlashCommandCompletion`, and exposes:

```csharp
public ComposerState State { get; }
public IReadOnlyList<ISlashCommand> Suggestions { get; }
public void ReplaceDraft(string text, int cursorIndex);
public void InsertText(string text);
public void SeedHistory(IEnumerable<string> history);
public void BeginPaste();
public void EndPaste();
public ComposerActionResult Apply(UiAction action);
public ComposerState Export();
public void Restore(ComposerState state);
```

- [ ] **Step 5: Implement `ComposerView` with localized obsolete suppression**

At the top and bottom of `ComposerView.cs`:

```csharp
#pragma warning disable CS0618
```

and:

```csharp
#pragma warning restore CS0618
```

Only this file suppresses the warning. Subclass `TextView`, set `Multiline = true`, `WordWrap = true`, and `TabKeyAddsTab = false`. Normalize `TextView.Text` line endings to `\n` when exporting draft state and convert back to `Environment.NewLine` when restoring it. Render slash suggestions in a transient `ListView`/overlay owned by `ComposerView`; it is visible only while `ComposerController.Suggestions` is non-empty and never changes the persistent status row. Override `OnKeyDown(Key key)`:

- Enter calls controller submit and raises `Submitted`.
- Ctrl+J invokes `Command.NewLine`.
- Ctrl+C raises `ActionRequested(Interrupt)`.
- F2 raises `ActionRequested(ToggleMode)`.
- other keys call base, then synchronize text/cursor into `ComposerController`.

Subscribe to `Pasting`/`Pasted` so bracketed paste is inserted literally and Enter characters in the payload cannot trigger submission.

- [ ] **Step 6: Run composer tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ComposerControllerTests|FullyQualifiedName~ComposerViewTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 7: Commit**

```powershell
git add src/Coda.Tui/Ui/Input tests/Coda.Tui.Tests/ComposerControllerTests.cs tests/Coda.Tui.Tests/ComposerViewTests.cs
git commit -m "feat(tui): add multiline Terminal.Gui composer" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 10: Build the shared shell base and composer-first inline shell

**Files:**
- Create: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Create: `src/Coda.Tui/Ui/Shells/PromptOverlay.cs`
- Create: `src/Coda.Tui/Ui/Shells/InlineTranscriptCommitter.cs`
- Create: `src/Coda.Tui/Ui/Shells/InlineTuiShell.cs`
- Test: `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`

- [ ] **Step 1: Write failing inline layout and append-once tests**

```csharp
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class InlineTuiShellTests
{
    [Theory]
    [InlineData(60, 12)]
    [InlineData(80, 24)]
    [InlineData(140, 40)]
    public void Inline_layout_keeps_composer_above_one_line_status(int width, int height)
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        app.Driver.InlinePosition = new Point(0, 0);
        using var shell = ShellTestFactory.CreateInline();

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.True(shell.Composer.Frame.Height >= 3);
        Assert.Equal(1, shell.Status.Frame.Height);
        Assert.Equal(shell.Composer.Frame.Bottom, shell.Status.Frame.Y);
        Assert.True(shell.Frame.Height <= height);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Completed_blocks_are_committed_once()
    {
        var committer = new InlineTranscriptCommitter();
        var block = new CommandOutputTranscriptBlock(Guid.NewGuid(), "hello");

        Assert.True(committer.TryQueue(block));
        Assert.False(committer.TryQueue(block));
        Assert.Equal([block], committer.Drain());
        Assert.Empty(committer.Drain());
    }

    [Fact]
    public async Task Closing_prompt_overlay_restores_composer_focus()
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.Inline;
        app.ForceInlinePosition = new Point(0, 0);
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);
        using var shell = ShellTestFactory.CreateInline();
        var token = app.Begin(shell);
        app.LayoutAndDraw();
        shell.Composer.SetFocus();
        var prompt = UiPromptRequest.Confirm("Allow?", defaultValue: false);

        await shell.ApplyAsync(UiSessionSnapshot.Empty with { PendingPrompt = prompt }, CancellationToken.None);
        Assert.True(shell.PromptOverlay.Visible);

        await shell.ApplyAsync(UiSessionSnapshot.Empty, CancellationToken.None);
        Assert.False(shell.PromptOverlay.Visible);
        Assert.True(shell.Composer.HasFocus);

        if (token is not null)
        {
            app.End(token);
        }
    }
}
```

`ShellTestFactory.CreateInline` constructs a shell with `UiSessionSnapshot.Empty`, a no-op controller callback, a real `ComposerController`, and a fixed status projector. Keep the factory internal to the test file.

- [ ] **Step 2: Run inline tests to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~InlineTuiShellTests"
```

Expected: build fails because shell classes do not exist.

- [ ] **Step 3: Implement the shared base**

`TerminalGuiShellBase : Window, IUiFrameSink` owns:

```csharp
internal ComposerView Composer { get; }
internal Label Status { get; }
internal PromptOverlay PromptOverlay { get; }
internal UiSessionSnapshot Snapshot { get; private set; }

public event EventHandler<string>? PromptSubmitted;
public event EventHandler<UiAction>? ActionRequested;
public ComposerState ExportComposerState();
public void RestoreComposerState(ComposerState state);
public ValueTask ApplyAsync(UiSessionSnapshot snapshot, CancellationToken cancellationToken);
```

Keep `ShellPresentationState` private to the shell layer: focused view id, selected picker row, transcript viewport, open prompt id, hovered block id, and expanded tool/diff block ids. Only `ComposerState` is explicitly exported during a mode switch; presentation coordinates and Terminal.Gui controls never enter `UiSessionSnapshot`.

`ApplyAsync` uses `App.Invoke` when called off the UI thread, updates status only when text changes, opens/closes prompt overlays from prompt events/snapshot state, and calls an abstract `ApplyTranscriptChanges`.

`PromptOverlay` supports all five prompt kinds with keyboard-only completion: arrows/tab move, Space marks multi-select, Enter accepts, Escape cancels. It publishes `UiPromptResponseSubmittedEvent`; it never invokes Spectre.

- [ ] **Step 4: Implement inline layout and native-scrollback commits**

`InlineTuiShell`:

- uses `Height = Dim.Auto(minimumContentDim: 4)`,
- has no retained transcript viewport,
- gives the bordered composer all rows except the final status row,
- fixes status to one line,
- queues only newly completed transcript blocks,
- on the UI actor thread temporarily inserts wrapped labels above the composer, calls `App.LayoutAndDraw()`, then removes committed labels after the inline region has scrolled them into native scrollback,
- never requeues a block id,
- keeps active streaming text in a bounded temporary row until completion.

At 60x12 the composer has at least one content row inside its border and the status remains one row.

- [ ] **Step 5: Run inline tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~InlineTuiShellTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 6: Commit**

```powershell
git add src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs src/Coda.Tui/Ui/Shells/PromptOverlay.cs src/Coda.Tui/Ui/Shells/InlineTranscriptCommitter.cs src/Coda.Tui/Ui/Shells/InlineTuiShell.cs tests/Coda.Tui.Tests/InlineTuiShellTests.cs
git commit -m "feat(tui): add composer-first inline shell" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 11: Add the full-screen shell and virtualized transcript view

**Files:**
- Create: `src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs`
- Create: `src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs`
- Create: `src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs`
- Create: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs`
- Create: `src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs`
- Modify: `src/Coda.Tui/Ui/Shells/InlineTuiShell.cs`
- Test: `tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs`
- Test: `tests/Coda.Tui.Tests/TranscriptBlockFormatterTests.cs`

- [ ] **Step 1: Write failing virtualization and layout tests**

```csharp
using System.Collections.Immutable;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class FullscreenTuiShellTests
{
    [Fact]
    public void Virtualized_view_formats_only_visible_rows()
    {
        var calls = 0;
        var blocks = Enumerable.Range(0, 10_000)
            .Select(index => (TranscriptBlock)new CommandOutputTranscriptBlock(Guid.NewGuid(), $"line {index}"))
            .ToImmutableArray();
        var indexer = new TranscriptLayoutIndex((block, width) =>
        {
            calls++;
            return [((CommandOutputTranscriptBlock)block).Text];
        });
        indexer.ReplaceAll(blocks, width: 80);
        calls = 0;

        var visible = indexer.GetVisibleRows(firstRow: 9_990, height: 20, overscan: 2);

        Assert.InRange(visible.Count, 20, 24);
        Assert.InRange(calls, 20, 26);
    }

    [Theory]
    [InlineData(60, 12)]
    [InlineData(80, 24)]
    [InlineData(140, 40)]
    public void Fullscreen_layout_is_header_transcript_composer_status_without_sidebar(int width, int height)
    {
        using IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(width, height);
        using var shell = ShellTestFactory.CreateFullscreen();

        var token = app.Begin(shell);
        app.LayoutAndDraw();

        Assert.Equal(1, shell.Header.Frame.Height);
        Assert.True(shell.Transcript.Frame.Height >= 3);
        Assert.Equal(Math.Min(width, FullscreenTuiShell.MaximumTranscriptWidth), shell.Transcript.Frame.Width);
        Assert.True(shell.Composer.Frame.Height >= 3);
        Assert.Equal(1, shell.Status.Frame.Height);
        Assert.DoesNotContain(shell.SubViews, view => view.Id?.Contains("sidebar", StringComparison.OrdinalIgnoreCase) == true);

        if (token is not null)
        {
            app.End(token);
        }
    }

    [Fact]
    public void Scrolling_away_disables_auto_follow_and_counts_new_rows()
    {
        var state = new TranscriptViewportState();
        state.ScrollBy(-10);
        state.OnRowsAppended(3);

        Assert.False(state.AutoFollow);
        Assert.Equal(3, state.UnseenRows);

        state.JumpToNewest();
        Assert.True(state.AutoFollow);
        Assert.Equal(0, state.UnseenRows);
    }
}
```

Create `TranscriptBlockFormatterTests.cs`:

```csharp
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class TranscriptBlockFormatterTests
{
    [Fact]
    public void Assistant_markdown_becomes_attributed_lines_without_ansi()
    {
        var block = new AssistantTranscriptBlock(Guid.NewGuid(), "# Heading\n\n**bold** text", true);

        var lines = TranscriptBlockFormatter.Format(block, width: 40);

        Assert.Equal(["Heading", string.Empty, "bold text"], lines.Select(line => line.Text));
        Assert.Equal(TranscriptRole.Heading, lines[0].Role);
        Assert.Equal(TranscriptRole.Assistant, lines[2].Role);
        Assert.DoesNotContain(lines, line => line.Text.Contains("\u001b[", StringComparison.Ordinal));
    }
}
```

- [ ] **Step 2: Run full-screen tests to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~FullscreenTuiShellTests"
```

Expected: build fails because transcript index/view/full-screen shell types do not exist.

- [ ] **Step 3: Implement the bounded transcript layout index**

`TranscriptLayoutIndex` keeps:

- the active width,
- block id → wrapped row count,
- prefix row offsets for binary search,
- an LRU cache of at most 256 fully wrapped blocks,
- no `TextView`.

On width change, clear the wrap cache and rebuild row counts once. `GetVisibleRows` binary-searches the first block and formats only viewport rows plus overscan. The test callback count must remain bounded by viewport height, not transcript length.

`ReplaceAll` is used for initial/resumed transcripts and resize reflow. Normal streaming uses `Append`/`ReplaceLast`, updating only the active or newly completed block so a growing conversation does not trigger an O(n) rebuild per event.

`TranscriptBlockFormatter` uses Markdig's parsed block/inline tree (available through Terminal.Gui 2.4.17 dependencies) and returns `TranscriptRenderLine(string Text, TranscriptRole Role)` values for user, assistant, heading, code, tool, diff, permission, question, warning, and notification roles. It never emits ANSI text. Inline and full-screen shells share this formatter.

- [ ] **Step 4: Implement `VirtualizedTranscriptView`**

Subclass `View`; override:

```csharp
protected override bool OnDrawingContent(DrawContext? context)
```

Call `ClearViewport()`, request visible rows from `TranscriptLayoutIndex`, then draw each row with `Move(0, row)` and `AddStr`. Update `ContentSize` and viewport offsets without materializing one giant string. Expose `ScrollBy`, `JumpToNewest`, `AutoFollow`, and `UnseenRows`.

Bind `MouseFlags.WheeledUp/WheeledDown` to transcript scrolling and left-click on a tool/diff row to its shell-local expanded state. The focused transcript uses Enter/Space for the same expand/collapse action. `ComposerView` and picker `ListView` retain Terminal.Gui's click/focus behavior. All of these remain optional and are bypassed when `--no-mouse` sets `app.Mouse.IsMouseDisabled`.

- [ ] **Step 5: Implement the full-screen layout**

`FullscreenTuiShell` uses:

1. one-row session header,
2. `VirtualizedTranscriptView` filling remaining space,
3. bordered composer,
4. one-row status.

Set `public const int MaximumTranscriptWidth = 120`; center the transcript when the terminal is wider, while the header/composer/status remain full-width. There is no permanent sidebar. Context, model/session/MCP pickers, permissions, help, diffs, and command palette use `PromptOverlay`/modal cards. New completed rows auto-follow only when already at the bottom; otherwise show `"{n} new — Ctrl+End"` in the transcript footer/header.

- [ ] **Step 6: Run full-screen tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~TranscriptBlockFormatterTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 7: Commit**

```powershell
git add src/Coda.Tui/Ui/Rendering/TranscriptBlockFormatter.cs src/Coda.Tui/Ui/Shells/InlineTuiShell.cs src/Coda.Tui/Ui/Shells/TranscriptLayoutIndex.cs src/Coda.Tui/Ui/Shells/TranscriptViewportState.cs src/Coda.Tui/Ui/Shells/VirtualizedTranscriptView.cs src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs tests/Coda.Tui.Tests/FullscreenTuiShellTests.cs tests/Coda.Tui.Tests/TranscriptBlockFormatterTests.cs
git commit -m "feat(tui): add virtualized full-screen transcript" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 12: Add the shared controller, interruption, mode switching, lifecycle cleanup, and fallback ladder

**Files:**
- Create: `src/Coda.Tui/Ui/TuiController.cs`
- Create: `src/Coda.Tui/Ui/Host/TuiShellExit.cs`
- Create: `src/Coda.Tui/Ui/Host/ITuiModeRunner.cs`
- Create: `src/Coda.Tui/Ui/Host/TerminalGuiModeRunner.cs`
- Create: `src/Coda.Tui/Ui/Host/TerminalProcessExitRegistration.cs`
- Create: `src/Coda.Tui/Ui/Host/TuiHost.cs`
- Modify: `src/Coda.Tui/ConsoleCancellationRegistration.cs`
- Test: `tests/Coda.Tui.Tests/TuiHostTests.cs`

- [ ] **Step 1: Write failing host/controller tests**

```csharp
using Coda.Tui.Ui.Host;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Mode;

namespace Coda.Tui.Tests;

public sealed class TuiHostTests
{
    [Fact]
    public async Task Fullscreen_failure_falls_back_in_order_and_reports_diagnostic()
    {
        var runner = new ScriptedRunner(
            new Dictionary<TuiRunMode, Queue<TuiShellExit>>
            {
                [TuiRunMode.Fullscreen] = new([TuiShellExit.Failed(new InvalidOperationException("full"))]),
                [TuiRunMode.Inline] = new([TuiShellExit.Failed(new InvalidOperationException("inline"))]),
                [TuiRunMode.Spectre] = new([TuiShellExit.Exited]),
            });
        var error = new StringWriter();
        var host = new TuiHost(runner, error);

        await host.RunAsync(TuiRunMode.Fullscreen, ComposerState.Empty);

        Assert.Equal([TuiRunMode.Fullscreen, TuiRunMode.Inline, TuiRunMode.Spectre], runner.Attempts);
        Assert.Contains("full-screen failed", error.ToString());
        Assert.Contains("inline failed", error.ToString());
    }

    [Fact]
    public async Task Mode_switch_preserves_composer_state()
    {
        var draft = new ComposerState("draft", 2, ["one"], 1, false);
        var runner = new ScriptedRunner(
            new Dictionary<TuiRunMode, Queue<TuiShellExit>>
            {
                [TuiRunMode.Inline] = new([TuiShellExit.SwitchTo(TuiRunMode.Fullscreen, draft)]),
                [TuiRunMode.Fullscreen] = new([TuiShellExit.Exited]),
            });
        var host = new TuiHost(runner, new StringWriter());

        await host.RunAsync(TuiRunMode.Inline, ComposerState.Empty);

        Assert.Equal(draft, runner.States[1]);
        Assert.Same(runner.SessionIdentities[0], runner.SessionIdentities[1]);
    }

    [Fact]
    public async Task Terminal_gui_runner_disposes_application_after_shell_factory_failure()
    {
        var disposed = false;
        EventHandler<EventArgs<IApplication>> handler = (_, _) => disposed = true;
        Application.InstanceDisposed += handler;
        try
        {
            var runner = new TerminalGuiModeRunner(
                shellFactory: (_, _, _) => throw new InvalidOperationException("render setup failed"),
                spectreRunner: (_, _) => Task.FromResult(TuiShellExit.Exited),
                plainRunner: (_, _) => Task.FromResult(TuiShellExit.Exited),
                applicationFactory: () =>
                {
                    var app = Application.Create();
                    app.ForceInlinePosition = new Point(0, 0);
                    return app;
                },
                driverName: DriverRegistry.Names.ANSI);

            var result = await runner.RunAsync(TuiRunMode.Inline, ComposerState.Empty, CancellationToken.None);

            Assert.Equal(TuiShellExitKind.Failed, result.Kind);
            Assert.True(disposed);
        }
        finally
        {
            Application.InstanceDisposed -= handler;
        }
    }

    [Fact]
    public void Managed_process_exit_requests_terminal_stop()
    {
        var requested = false;
        using var registration = new TerminalProcessExitRegistration(() => requested = true);

        registration.InvokeForTest();

        Assert.True(requested);
    }

    [Fact]
    public void Ctrl_c_interrupts_active_turn_before_exit()
    {
        var active = true;
        var exited = false;
        using var registration = new ConsoleCancellationRegistration(
            () =>
            {
                if (!active)
                {
                    return false;
                }

                active = false;
                return true;
            },
            () => exited = true);

        Assert.True(registration.HandleForTest());
        Assert.False(active);
        Assert.False(exited);
    }
}
```

`ScriptedRunner` implements `ITuiModeRunner`, records modes/states, records the same injected session-identity object on every call, and dequeues scripted exits. Include it in the test file.

- [ ] **Step 2: Run host tests to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiHostTests"
```

Expected: build fails because host/controller contracts and new cancellation constructor do not exist.

- [ ] **Step 3: Implement shell exit and runner contracts**

```csharp
public sealed record TuiShellExit(
    TuiShellExitKind Kind,
    TuiRunMode? NextMode,
    ComposerState Composer,
    Exception? Error)
{
    public static TuiShellExit Exited { get; } = new(TuiShellExitKind.Exit, null, ComposerState.Empty, null);
    public static TuiShellExit SwitchTo(TuiRunMode mode, ComposerState state) => new(TuiShellExitKind.SwitchMode, mode, state, null);
    public static TuiShellExit Failed(Exception error) => new(TuiShellExitKind.Failed, null, ComposerState.Empty, error);
}
```

```csharp
public interface ITuiModeRunner
{
    Task<TuiShellExit> RunAsync(TuiRunMode mode, ComposerState composer, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement `TerminalGuiModeRunner` cleanup**

For inline/full-screen:

```csharp
IApplication? app = null;
TerminalGuiShellBase? shell = null;
Exception? primary = null;
var cleanup = new List<Exception>();
try
{
    app = this.applicationFactory();
    app.AppModel = mode == TuiRunMode.Inline ? AppModel.Inline : AppModel.FullScreen;
    app.Mouse.IsMouseDisabled = this.mouseDisabled;
    app.Init(this.driverName);
    shell = this.shellFactory(mode, app, composer);
    await app.RunAsync(shell, cancellationToken);
}
catch (Exception ex)
{
    primary = ex;
}
finally
{
    try
    {
        shell?.Dispose();
    }
    catch (Exception ex)
    {
        cleanup.Add(ex);
    }

    try
    {
        app?.Dispose();
    }
    catch (Exception ex)
    {
        cleanup.Add(ex);
    }
}
```

Return `Failed(primary)` when cleanup succeeds. When cleanup also fails, return `Failed(new AggregateException(primary is null ? cleanup : [primary, .. cleanup]))`, preserving the primary exception first. Terminal.Gui disposal restores alternate screen, input mode, cursor, mouse, bracketed paste, focus reporting, synchronized output, and scroll regions. Never write diagnostics until after all cleanup attempts.

Register `TerminalProcessExitRegistration` after `Init` and dispose it before application disposal. Its `AppDomain.CurrentDomain.ProcessExit` handler performs only a bounded, idempotent `RequestStop`/cleanup callback and catches secondary exceptions. Terminal.Gui owns resize and non-Windows suspend/resume signals through its driver/application model; shells respond to the resulting layout changes without direct console APIs.

When actor/frame rendering fails, atomically disable composer submission, cancel the actor/mailbox shutdown token so producers stop accepting interactive updates, interrupt any active turn, then perform shell/application cleanup before `TuiHost` writes the diagnostic and starts the next safer mode.

Use this constructor so lifecycle behavior is directly testable:

```csharp
public TerminalGuiModeRunner(
    Func<TuiRunMode, IApplication, ComposerState, TerminalGuiShellBase> shellFactory,
    Func<ComposerState, CancellationToken, Task<TuiShellExit>> spectreRunner,
    Func<ComposerState, CancellationToken, Task<TuiShellExit>> plainRunner,
    Func<IApplication>? applicationFactory = null,
    string? driverName = null)
```

Spectre mode calls the existing Spectre REPL runner. Plain mode calls the plain loop/renderer. This class is the only place allowed to create/dispose Terminal.Gui applications.

- [ ] **Step 5: Implement `TuiController`**

The controller persists across shell instances and owns:

- `TuiApp` dispatch,
- `AgentRunner`,
- active turn interruption,
- current semantic snapshot,
- composer state transfer,
- mode-switch request,
- separate exit request.

`Ctrl+C` calls `AgentRunner.TryInterruptActiveTurn`; when idle it emits a short `Nothing is running; use /exit or Ctrl+D to exit.` notification. `Ctrl+D` on an empty draft and `/exit` request application exit. F2 exports composer state and returns `SwitchMode`. `TerminalGuiModeRunner` receives `TuiLaunchOptions.MouseDisabled` and assigns it to `app.Mouse.IsMouseDisabled` before `Init`.

Composer submission schedules `TuiApp.DispatchAsync` on a controller-owned async task, disables only submit while the turn/command is active, and returns immediately to the Terminal.Gui UI loop. Prompt requests therefore suspend the dispatch task through `ActorUiPromptService` while the UI thread remains free to render and accept the overlay response.

- [ ] **Step 6: Implement `TuiHost` fallback and diagnostics**

`TuiHost.RunAsync` iterates `TuiModePolicy.FallbacksFrom(initial)`. On `Failed`:

1. wait for runner cleanup,
2. write one concise line to stderr,
3. carry controller/session/composer state to the next mode.

On switch, run exactly the requested inline/full-screen mode without rebuilding credentials, MCP manager, `SessionState`, `TuiApp`, or `AgentRunner`.
Before each successful mode start, publish `ModeChangedEvent(mode.ToString().ToLowerInvariant())` so the shared status/header reflects the active shell.
Persist `UiActor.Current` in `TuiController` when a shell stops; construct the next shell actor with that snapshot, the same mailbox, and the same `ActorUiPromptService`. A pending prompt therefore reopens in the new mode instead of being cancelled or rerun.

- [ ] **Step 7: Refactor cancellation registration**

Constructor:

```csharp
public ConsoleCancellationRegistration(Func<bool> tryInterrupt, Action requestExit)
```

The event handler sets `e.Cancel = true`; it calls `tryInterrupt()`. If false, it publishes the idle notification through the controller and does not exit. `requestExit` is used by the explicit exit action, not the first Ctrl-C.

- [ ] **Step 8: Run host tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiHostTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 9: Commit**

```powershell
git add src/Coda.Tui/Ui/TuiController.cs src/Coda.Tui/Ui/Host src/Coda.Tui/ConsoleCancellationRegistration.cs tests/Coda.Tui.Tests/TuiHostTests.cs
git commit -m "feat(tui): own lifecycle switching and fallback" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 13: Integrate the host into Program while preserving Spectre/plain behavior

**Files:**
- Create: `src/Coda.Tui/InteractiveProgram.cs`
- Modify: `src/Coda.Tui/Program.cs`
- Modify: `src/Coda.Tui/TuiApp.cs`
- Modify: `src/Coda.Tui/ImmediateCli.cs`
- Modify: `src/Coda.Tui/Agent/AgentRunner.cs`
- Test: `tests/Coda.Tui.Tests/InteractiveProgramTests.cs`
- Modify: `tests/Coda.Tui.Tests/ImmediateCliTests.cs`

- [ ] **Step 1: Write failing program integration tests**

```csharp
using Coda.Tui.Ui.Mode;

namespace Coda.Tui.Tests;

public sealed class InteractiveProgramTests
{
    [Fact]
    public async Task Redirected_output_uses_plain_and_preserves_script_text()
    {
        var input = new StringReader("hello\n");
        var output = new StringWriter();
        var error = new StringWriter();
        var caps = new TerminalCapabilities(false, true, 120, 40, true);
        var runner = new RecordingInteractiveSessionRunner(output);

        var code = await InteractiveProgram.RunAsync(
            [],
            input,
            output,
            error,
            new FixedCapabilitiesProvider(caps),
            CancellationToken.None,
            runner);

        Assert.Equal(0, code);
        Assert.Equal(TuiRunMode.Plain, runner.Mode);
        Assert.Equal("plain hello" + Environment.NewLine, output.ToString());
        Assert.DoesNotContain("\u001b[", output.ToString());
    }

    [Fact]
    public async Task Explicit_mode_too_small_returns_usage_error_without_starting_terminal()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await InteractiveProgram.RunAsync(
            ["--tui=fullscreen"],
            new StringReader(string.Empty),
            output,
            error,
            new FixedCapabilitiesProvider(new(false, false, 50, 10, true)),
            CancellationToken.None,
            new RecordingInteractiveSessionRunner(output));

        Assert.Equal(2, code);
        Assert.Contains("at least 60 columns by 12 rows", error.ToString());
    }

    private sealed class FixedCapabilitiesProvider(TerminalCapabilities capabilities)
        : ITerminalCapabilitiesProvider
    {
        public TerminalCapabilities Get() => capabilities;
    }

    private sealed class RecordingInteractiveSessionRunner(TextWriter output)
        : IInteractiveSessionRunner
    {
        public TuiRunMode? Mode { get; private set; }

        public async Task<int> RunAsync(
            TuiRunMode mode,
            TuiLaunchOptions options,
            TextReader input,
            TextWriter error,
            CancellationToken cancellationToken)
        {
            this.Mode = mode;
            var line = await input.ReadLineAsync(cancellationToken);
            output.WriteLine($"{mode.ToString().ToLowerInvariant()} {line}");
            return 0;
        }
    }
}
```

Add to `ImmediateCliTests`:

```csharp
[Fact]
public void Help_documents_tui_and_plain_flags()
{
    var (_, output) = Run("--help");

    Assert.Contains("--tui=auto|inline|fullscreen", output);
    Assert.Contains("--plain", output);
    Assert.Contains("--no-mouse", output);
}
```

- [ ] **Step 2: Run integration tests to verify failure**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~InteractiveProgramTests|FullyQualifiedName~ImmediateCliTests"
```

Expected: build fails because `InteractiveProgram`/capability provider do not exist and help lacks the flags.

- [ ] **Step 3: Move interactive composition from `Program.cs`**

Keep the existing top-level dispatch for `run`, `serve`, `models`, `help`, export/import, and immediate `--version`/`--help`. Replace lines that build the interactive graph with:

```csharp
return await InteractiveProgram.RunAsync(
    args,
    Console.In,
    Console.Out,
    Console.Error,
    new SystemTerminalCapabilitiesProvider(),
    CancellationToken.None);
```

`InteractiveProgram` performs:

1. `TuiLaunchOptions.Parse`.
2. `TuiModePolicy.SelectInitial`.
3. credential/provider/session/registry creation.
4. mode-specific event publisher, output console, and prompt service creation.
5. `TuiApp`, `AgentRunner`, `TuiController`, and `TuiHost` creation with an `InteractiveStartup` callback.
6. Terminal.Gui shell and actor startup before any potentially chatty MCP/setup output.
7. inside `InteractiveStartup`, resume/fork state seeding, `TranscriptSeededEvent(SessionHistoryProjector.Project(session.History))`, MCP connection, immutable initial metadata/MCP/git/context publication, and first-run setup.
8. the same startup callback synchronously before the Spectre/plain fallback loops.
9. deterministic async disposal of MCP, app/controller, providers, and cancellation resources.

Disable composer submission until `InteractiveStartup` completes; display `Starting…` as the active operation. This prevents a bounded mailbox from filling before its actor starts and prevents a turn from racing MCP/setup initialization.

Route `DefaultMcpHttpClientFactory`, secret-resolution, and `ConnectAllAsync` status callbacks to `DiagnosticEvent("MCP", message, level)` rather than direct terminal writes. This changes no MCP OAuth protocol or credential behavior.

Expose this test seam in `InteractiveProgram.cs`:

```csharp
public interface IInteractiveSessionRunner
{
    Task<int> RunAsync(
        TuiRunMode mode,
        TuiLaunchOptions options,
        TextReader input,
        TextWriter error,
        CancellationToken cancellationToken);
}
```

`InteractiveProgram.RunAsync` accepts an optional final `IInteractiveSessionRunner? runner = null`; production uses `DefaultInteractiveSessionRunner`, while option/mode tests inject the recording runner above.

- [ ] **Step 4: Separate `TuiApp` loops from dispatch**

Keep `DispatchAsync` as the shared command/bash/prompt path. Rename the existing loop to:

```csharp
public Task RunSpectreAsync(CancellationToken cancellationToken = default)
```

Add:

```csharp
public Task RunPlainAsync(TextReader input, CancellationToken cancellationToken = default)
```

Terminal.Gui shells do not call either loop; composer submission calls `DispatchAsync`. This leaves `InteractiveLineEditor` reachable only from `RunSpectreAsync`.

The plain runner starts the same bounded `UiEventMailbox`/`UiActor` with `NullUiFrameSink.Instance` and `PlainOutputRenderer` as the event observer before it calls `RunPlainAsync`. Thus redirected output remains serialized by one owner even when agent/tool callbacks arrive concurrently.

- [ ] **Step 5: Update help text**

Document:

```text
--tui=auto|inline|fullscreen
--plain
--no-mouse
```

State that auto selects plain for redirected/non-interactive/smaller-than-60x12 terminals, otherwise inline; full-screen is opt-in; `--no-mouse` leaves all functionality available through the keyboard.

- [ ] **Step 6: Run integration and regression tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj --filter "FullyQualifiedName~InteractiveProgramTests|FullyQualifiedName~ImmediateCliTests|FullyQualifiedName~CommandDispatchTests|FullyQualifiedName~BashModeTests|FullyQualifiedName~CodaSessionResumeTests"
```

Expected: PASS, 0 failed.

- [ ] **Step 7: Verify no Terminal.Gui path writes directly to `System.Console`**

Run:

```powershell
rg -n "Console\.(Write|WriteLine|ReadLine|SetCursorPosition|CursorLeft|CursorTop)" src/Coda.Tui --glob "*.cs"
```

Expected matches are limited to `InteractiveLineEditor.cs`, `Program.cs` stream injection, and the system capability provider. Any command/sink/shell match is a failure and must be routed through the host.

- [ ] **Step 8: Commit**

```powershell
git add src/Coda.Tui/InteractiveProgram.cs src/Coda.Tui/Program.cs src/Coda.Tui/TuiApp.cs src/Coda.Tui/ImmediateCli.cs src/Coda.Tui/Agent/AgentRunner.cs tests/Coda.Tui.Tests/InteractiveProgramTests.cs tests/Coda.Tui.Tests/ImmediateCliTests.cs
git commit -m "feat(tui): integrate Terminal.Gui host modes" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 14: Add the compatibility spike, PTY checklist, and user documentation

**Files:**
- Create: `samples/Coda.TerminalGuiSpike/Coda.TerminalGuiSpike.csproj`
- Create: `samples/Coda.TerminalGuiSpike/Program.cs`
- Modify: `LlmAuth.slnx`
- Create: `scripts/terminal-gui-pty-smoke.ps1`
- Create: `docs/terminal-gui-compatibility.md`
- Modify: `README.md`

- [ ] **Step 1: Create the spike project**

`Coda.TerminalGuiSpike.csproj` targets `net10.0`, has an executable output type, and references `../../src/Coda.Tui/Coda.Tui.csproj`.

The harness accepts:

```text
--mode inline|fullscreen
--scenario stream|unicode|paste|resize|cancel|mouse-off|managed-crash
```

It uses `Application.Create()`, sets `app.AppModel`, optionally sets `app.Mouse.IsMouseDisabled = true`, initializes, runs a small `Window` with `TextView`, streams 100 events/second for ten seconds, records key-injection-to-paint latency, and prints p50/p95 plus lost/reordered action counts. Full-screen `stream` preloads 10,000 transcript blocks and prints visible rows formatted per frame. `managed-crash` throws from an iteration callback after three frames so cleanup/restoration can be observed without hard-killing the terminal.

- [ ] **Step 2: Add the manual smoke script**

`scripts/terminal-gui-pty-smoke.ps1`:

- accepts `-TerminalName`, `-Mode`, and `-Scenario`,
- validates values against the harness options,
- runs `dotnet run --project samples/Coda.TerminalGuiSpike -- --mode $Mode --scenario $Scenario`,
- prints the exact checklist item and asks the operator to record pass/fail in a supplied output CSV path,
- never stores credentials or terminal contents.

- [ ] **Step 3: Write the compatibility checklist**

`docs/terminal-gui-compatibility.md` contains a table with rows for:

- Windows Terminal,
- VS Code integrated terminal,
- Cursor integrated terminal,
- iTerm2,
- Apple Terminal,
- one common Linux terminal,
- tmux,
- screen,
- local SSH client/server session.

Each row runs both inline and full-screen where supported and records:

- startup/exit restoration,
- streaming while typing,
- p95 key-to-paint latency below 100 ms at 100 coalescible events/second with zero lost/reordered actions,
- no transcript overwrite/corruption of composer or status,
- resize while streaming,
- resize while prompt overlay is open,
- Unicode wide/combining characters,
- IME composition,
- multiline bracketed paste without submit,
- native selection/copy in inline mode,
- keyboard-only picker completion,
- mouse disabled,
- low-color/`TERM=dumb` fallback to plain,
- Ctrl-C interrupt then explicit exit,
- managed renderer crash restoration,
- full-screen visible-row formatting remains bounded by viewport height with 10,000 blocks,
- redirected input and redirected output plain behavior.

Include the minimum-size checks at 60x12, 59x12, and 60x11.

- [ ] **Step 4: Update README user usage**

Replace the statement that the TUI is built on Spectre with:

- Terminal.Gui v2 inline is the target/default interactive engine after acceptance.
- `--tui=inline`, `--tui=fullscreen`, `--tui=auto`, `--plain`, and `--no-mouse` examples.
- Spectre is a migration fallback.
- Enter submits; Ctrl+J inserts a newline; Ctrl-C interrupts the active turn; Ctrl+D or `/exit` exits; F2 switches inline/full-screen.
- Full-screen has no permanent sidebar and uses a virtualized transcript.
- Plain mode is recommended for screen readers, CI, redirection, and unsupported terminals.
- Link to `docs/terminal-gui-compatibility.md`.

- [ ] **Step 5: Build the spike and verify help text**

Run:

```powershell
dotnet build samples/Coda.TerminalGuiSpike/Coda.TerminalGuiSpike.csproj
dotnet run --project samples/Coda.TerminalGuiSpike/Coda.TerminalGuiSpike.csproj -- --help
```

Expected: build succeeds; help lists both modes and all seven scenarios.

- [ ] **Step 6: Commit**

```powershell
git add samples/Coda.TerminalGuiSpike LlmAuth.slnx scripts/terminal-gui-pty-smoke.ps1 docs/terminal-gui-compatibility.md README.md
git commit -m "docs(tui): add Terminal.Gui compatibility matrix" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 15: Run complete TUI and engine regressions

**Files:**
- Verify: `tests/Coda.Tui.Tests`
- Verify: `tests/Engine.Tests`
- Verify: `LlmAuth.slnx`

- [ ] **Step 1: Run all TUI tests**

Run:

```powershell
dotnet test tests/Coda.Tui.Tests/Coda.Tui.Tests.csproj
```

Expected: PASS, 0 failed, including mode policy, actor bounds/coalescing, reducer/transcript/status, sink forwarding, prompt adapters, composer actions, inline/full-screen widths, mode switching, lifecycle/fallback, Program parsing, plain output, and existing TUI regressions.

- [ ] **Step 2: Run all engine tests**

Run:

```powershell
dotnet test tests/Engine.Tests/Engine.Tests.csproj
```

Expected: PASS, 0 failed, including immutable MCP/LSP/task/runtime snapshot tests and existing engine regressions.

- [ ] **Step 3: Run the solution**

Run:

```powershell
dotnet test LlmAuth.slnx
```

Expected: PASS, 0 failed.

- [ ] **Step 4: Run final architecture searches**

Run:

```powershell
rg -n "SelectionPrompt|MultiSelectionPrompt|TextPrompt|\.Confirm\(|\.Ask<|\.Prompt\(" src/Coda.Tui --glob "*.cs" --glob "!Ui/Prompts/SpectreUiPromptService.cs"
rg -n "Profile\.Capabilities\.Interactive" src/Coda.Tui --glob "*.cs" --glob "!TuiApp.cs" --glob "!Ui/Prompts/SpectreUiPromptService.cs"
rg -n "new TextView|: TextView" src/Coda.Tui --glob "*.cs" --glob "!Ui/Input/ComposerView.cs"
rg -n "Console\.(Write|WriteLine|SetCursorPosition|CursorLeft|CursorTop)" src/Coda.Tui/Agent src/Coda.Tui/Commands src/Coda.Tui/Ui --glob "*.cs"
```

Expected: all four searches produce no output.

- [ ] **Step 5: Inspect final diff**

Run:

```powershell
git diff --check
git status --short
git --no-pager diff --stat
```

Expected: `git diff --check` produces no output; status contains only intentional implementation/documentation changes if the final verification itself required a fix.
