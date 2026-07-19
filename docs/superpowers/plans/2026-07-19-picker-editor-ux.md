# Picker and Composer Clipboard UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mark and preselect the active model in `/model`, and add native-style composer selection, copy, paste, and middle-click context-menu behavior.

**Architecture:** Prompt option state remains renderer-neutral and is formatted through one shared helper. `ComposerView` owns only native pointer/selection mechanics and emits semantic pointer actions; `TerminalGuiShellBase` owns clipboard I/O, Ctrl+C precedence, and transient status. Clipboard delegates remain injectable so shell behavior is deterministic in tests and independent of the OS clipboard.

**Tech Stack:** .NET 10, C# 14, Terminal.Gui 2.4.17, Spectre.Console 0.55.2, xUnit

---

## File Structure

- Create `src/Coda.Tui/Ui/Prompts/UiPromptOptionFormatter.cs` — shared plain-text option marker/detail formatting.
- Modify `src/Coda.Tui/Ui/Prompts/UiPromptModels.cs` — semantic current-option state and select default.
- Modify `src/Coda.Tui/Ui/Prompts/SpectreUiPromptService.cs` — shared formatting and default selection.
- Modify `src/Coda.Tui/Ui/Shells/PromptOverlay.cs` — shared formatting and initial selection.
- Modify `src/Coda.Tui/Commands/ModelCommand.cs` — mark/default the active model and skip unchanged persistence.
- Create `src/Coda.Tui/Ui/Input/ComposerPointerAction.cs` — semantic composer pointer-action contract.
- Modify `src/Coda.Tui/Ui/Input/ComposerView.cs` — native selection state, gesture arbitration, caret positioning, and context-menu seam.
- Create `src/Coda.Tui/Ui/Shells/ClipboardStatusText.cs` — Unicode-aware copied/pasted messages.
- Modify `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs` — clipboard orchestration and Ctrl+C precedence.
- Modify `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs` — clipboard reader seam propagation.
- Modify `src/Coda.Tui/Ui/Shells/InlineTuiShell.cs` — clipboard reader seam propagation.
- Modify `tests/Coda.Tui.Tests/RetainedShellFixture.cs` — deterministic clipboard reader seam.
- Modify `tests/Coda.Tui.Tests/UiPromptServiceTests.cs` — Spectre marker/default coverage.
- Modify `tests/Coda.Tui.Tests/InlineTuiShellTests.cs` — prompt-overlay marker/default coverage.
- Modify `tests/Coda.Tui.Tests/PromptDrivenCommandTests.cs` — current model option/default coverage.
- Modify `tests/Coda.Tui.Tests/ModelListCommandTests.cs` — unchanged-model persistence coverage.
- Create `tests/Coda.Tui.Tests/ComposerPointerActionTests.cs` — native composer gesture tests.
- Create `tests/Coda.Tui.Tests/ComposerClipboardShellTests.cs` — shell copy/paste/status/chord tests.
- Modify `README.md` — document composer mouse and active-model picker behavior.
- Modify `version.json` — bump release from 0.1.70 to 0.1.71.

### Task 1: Semantic Current Prompt Option

**Files:**
- Create: `src/Coda.Tui/Ui/Prompts/UiPromptOptionFormatter.cs`
- Modify: `src/Coda.Tui/Ui/Prompts/UiPromptModels.cs`
- Modify: `src/Coda.Tui/Ui/Prompts/SpectreUiPromptService.cs`
- Modify: `src/Coda.Tui/Ui/Shells/PromptOverlay.cs`
- Test: `tests/Coda.Tui.Tests/UiPromptServiceTests.cs`
- Test: `tests/Coda.Tui.Tests/InlineTuiShellTests.cs`

- [ ] **Step 1: Write failing renderer tests**

Add these tests:

```csharp
[Fact]
public async Task Spectre_select_marks_and_initially_selects_current_option()
{
    using var console = new TestConsole();
    console.Profile.Capabilities.Interactive = true;
    console.Input.PushKey(ConsoleKey.Enter);
    var service = new SpectreUiPromptService(console);
    var request = UiPromptRequest.Select(
        "Choose",
        [
            new UiPromptOption("a", "Alpha"),
            new UiPromptOption("b", "Bravo", "200K ctx", IsCurrent: true),
        ],
        defaultValue: "b");

    var response = await service.RequestAsync(request);

    Assert.Equal(["b"], response.SelectedIds.ToArray());
    Assert.Contains("● Bravo", console.Output);
    Assert.Contains("Current", console.Output);
}
```

```csharp
[Fact]
public void SelectOne_marks_and_enters_default_current_option()
{
    var overlay = CreateOverlay(out var events);
    var request = UiPromptRequest.Select(
        "Pick one",
        [
            new UiPromptOption("a", "Alpha"),
            new UiPromptOption("b", "Bravo", "200K ctx", IsCurrent: true),
        ],
        defaultValue: "b");

    overlay.Update(request);

    Assert.Contains("● Bravo", overlay.BodyText);
    Assert.Contains("Current", overlay.BodyText);
    overlay.NewKeyDownEvent(Key.Enter);
    Assert.Equal(["b"], SingleResponse(events).Response.SelectedIds.ToArray());
}
```

- [ ] **Step 2: Run the tests and verify RED**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Spectre_select_marks_and_initially_selects_current_option|FullyQualifiedName~SelectOne_marks_and_enters_default_current_option"
```

Expected: compilation fails because `UiPromptOption.IsCurrent` and the `defaultValue` select argument do not exist.

- [ ] **Step 3: Add semantic state and shared formatting**

Change the prompt records to:

```csharp
public sealed record UiPromptOption(
    string Id,
    string Label,
    string? Detail = null,
    bool IsCurrent = false);

public static UiPromptRequest Select(
    string title,
    IEnumerable<UiPromptOption> options,
    string? defaultValue = null) =>
    new(Guid.NewGuid(), UiPromptKind.SelectOne, title, null, [.. options], defaultValue, true);
```

Create the formatter:

```csharp
namespace Coda.Tui.Ui.Prompts;

internal static class UiPromptOptionFormatter
{
    internal const string CurrentMarker = "●";

    internal static string Format(UiPromptOption option)
    {
        ArgumentNullException.ThrowIfNull(option);

        var prefix = option.IsCurrent ? $"{CurrentMarker} " : "  ";
        var detail = option.IsCurrent
            ? string.IsNullOrWhiteSpace(option.Detail)
                ? "Current"
                : $"{option.Detail} · Current"
            : option.Detail;

        return string.IsNullOrWhiteSpace(detail)
            ? $"{prefix}{option.Label}"
            : $"{prefix}{option.Label} — {detail}";
    }
}
```

In `PromptOverlay.RenderBody`, replace manual label/detail rendering with:

```csharp
builder.Append(cursor)
    .Append(' ')
    .Append(mark)
    .Append(UiPromptOptionFormatter.Format(option));
```

In `SpectreUiPromptService.SelectOneAsync`, use the same formatting and default:

```csharp
var prompt = new SelectionPrompt<UiPromptOption>()
    .Title(request.Title)
    .UseConverter(option => Markup.Escape(UiPromptOptionFormatter.Format(option)))
    .AddChoices(request.Options);

var defaultOption = request.Options.FirstOrDefault(
    option => string.Equals(option.Id, request.DefaultValue, StringComparison.Ordinal));
if (defaultOption is not null)
{
    prompt.DefaultValue(defaultOption);
}
```

Use the shared formatter in `SelectManyAsync` too, without assigning a default.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the command from Step 2.

Expected: 2 tests pass.

- [ ] **Step 5: Run prompt regressions**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiPromptServiceTests|FullyQualifiedName~PromptOverlayTests"
```

Expected: all selected tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src\Coda.Tui\Ui\Prompts src\Coda.Tui\Ui\Shells\PromptOverlay.cs tests\Coda.Tui.Tests\UiPromptServiceTests.cs tests\Coda.Tui.Tests\InlineTuiShellTests.cs
git commit -m "feat(tui): mark current prompt options"
```

### Task 2: Current Model Selection and No-Op Persistence

**Files:**
- Modify: `src/Coda.Tui/Commands/ModelCommand.cs`
- Modify: `tests/Coda.Tui.Tests/PromptDrivenCommandTests.cs`
- Modify: `tests/Coda.Tui.Tests/ModelListCommandTests.cs`

- [ ] **Step 1: Write failing model tests**

Add:

```csharp
[Fact]
public async Task Model_picker_marks_and_defaults_to_current_model()
{
    var prompts = new RecordingPromptService(new UiPromptResponse(false, ["model-b"], null));
    var built = TestAppBuilder.BuildApp(prompts: prompts);
    built.Context.Session.Model = "MODEL-B";
    var models = new ModelListResult(
        built.Context.ActiveProvider.Id,
        ModelSource.Catalog,
        [
            new ModelListEntry("model-a", "A", 100_000),
            new ModelListEntry("model-b", "B", 200_000),
        ]);

    await ModelCommand.ChooseModelAsync(built.Context, models);

    var request = Assert.Single(prompts.Requests);
    Assert.Equal("model-b", request.DefaultValue);
    Assert.True(Assert.Single(request.Options, option => option.Id == "model-b").IsCurrent);
    Assert.False(Assert.Single(request.Options, option => option.Id == "model-a").IsCurrent);
}
```

Add:

```csharp
[Fact]
public async Task Selecting_current_model_does_not_persist_or_publish_a_change()
{
    var prompts = new RecordingPromptService(new UiPromptResponse(false, ["model-a"], null));
    var built = TestAppBuilder.BuildApp(prompts: prompts);
    built.Context.Session.Model = "model-a";
    built.Context.Session.ModelListCache[built.Context.ActiveProvider.Id] =
        new ModelListResult(
            built.Context.ActiveProvider.Id,
            ModelSource.Live,
            [new ModelListEntry("model-a", "A", 200_000)]);
    var persistCalls = 0;
    var command = new ModelCommand((_, _) =>
    {
        persistCalls++;
        return "saved";
    });

    await command.ExecuteAsync(built.Context, []);

    Assert.Equal(0, persistCalls);
    Assert.Equal("model-a", built.Context.Session.Model);
    Assert.Contains("already using", built.Console.Output);
}
```

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Model_picker_marks_and_defaults_to_current_model|FullyQualifiedName~Selecting_current_model_does_not_persist_or_publish_a_change"
```

Expected: compilation fails because current option state, default selection, and the injectable persistence constructor are absent.

- [ ] **Step 3: Implement current-model projection and application**

Add a persistence delegate while preserving the public parameterless constructor:

```csharp
private readonly Func<string, string, string> persistModel;

public ModelCommand()
    : this(TryPersistModelForProvider)
{
}

internal ModelCommand(Func<string, string, string> persistModel)
{
    this.persistModel = persistModel ?? throw new ArgumentNullException(nameof(persistModel));
}
```

Create one application method and call it from both argument and picker paths:

```csharp
private void ApplyModel(CommandContext context, string model)
{
    if (string.Equals(context.Session.Model, model, StringComparison.OrdinalIgnoreCase))
    {
        context.Console.MarkupLine($"Already using {Theme.AccentMarkup(context.Session.Model)}.");
        return;
    }

    context.Session.Model = model;
    var note = this.persistModel(context.ActiveProvider.Id, model);
    context.Console.MarkupLine($"Model set to {Theme.AccentMarkup(model)} {Theme.DimMarkup(note)}");
    SessionMetadataEvents.Publish(context);
}
```

Replace both duplicated mutation/persistence blocks with `this.ApplyModel(context, chosenModel)`.

When creating model options:

```csharp
var isCurrent = string.Equals(model.Id, context.Session.Model, StringComparison.OrdinalIgnoreCase);
return new UiPromptOption(
    model.Id,
    model.Id,
    string.IsNullOrEmpty(detail) ? null : detail,
    IsCurrent: isCurrent);
```

Create the request with:

```csharp
UiPromptRequest.Select("Choose a model", options, defaultValue: context.Session.Model)
```

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the command from Step 2.

Expected: 2 tests pass.

- [ ] **Step 5: Run model regressions**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~PromptDrivenCommandTests|FullyQualifiedName~ModelListCommandTests"
```

Expected: all selected tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src\Coda.Tui\Commands\ModelCommand.cs tests\Coda.Tui.Tests\PromptDrivenCommandTests.cs tests\Coda.Tui.Tests\ModelListCommandTests.cs
git commit -m "feat(tui): identify the active model"
```

### Task 3: Composer Pointer Action Boundary

**Files:**
- Create: `src/Coda.Tui/Ui/Input/ComposerPointerAction.cs`
- Modify: `src/Coda.Tui/Ui/Input/ComposerView.cs`
- Create: `tests/Coda.Tui.Tests/ComposerPointerActionTests.cs`

- [ ] **Step 1: Write failing native gesture tests**

Create tests that:

```csharp
[Fact]
public void Left_drag_uses_native_text_view_selection()
{
    using var view = CreateLaidOutView("alpha beta gamma", width: 6);

    view.NewMouseEvent(MouseAt(MouseFlags.LeftButtonPressed, 0, 1));
    view.NewMouseEvent(MouseAt(MouseFlags.LeftButtonPressed | MouseFlags.PositionReport, 3, 1));
    view.NewMouseEvent(MouseAt(MouseFlags.LeftButtonReleased, 3, 1));

    Assert.True(view.HasComposerSelection);
    Assert.NotEmpty(view.SelectedComposerText);
}

[Fact]
public void Fresh_left_press_with_selection_requests_copy_without_starting_a_new_drag()
{
    using var view = CreateSelectedView();
    ComposerPointerActionRequestedEventArgs? requested = null;
    view.PointerActionRequested += (_, args) => requested = args;

    var handled = view.NewMouseEvent(MouseAt(MouseFlags.LeftButtonPressed, 0, 0));

    Assert.True(handled);
    Assert.Equal(ComposerPointerActionKind.CopySelection, requested?.Kind);
    Assert.Equal(view.SelectedComposerText, requested?.SelectedText);
}

[Fact]
public void Right_click_without_selection_positions_wrapped_caret_then_requests_paste()
{
    using var view = CreateLaidOutView("alpha beta gamma", width: 6);
    ComposerPointerActionRequestedEventArgs? requested = null;
    view.PointerActionRequested += (_, args) => requested = args;

    view.NewMouseEvent(MouseAt(MouseFlags.RightButtonPressed, 2, 1));
    view.NewMouseEvent(MouseAt(MouseFlags.RightButtonReleased, 2, 1));
    view.NewMouseEvent(MouseAt(MouseFlags.RightButtonClicked, 2, 1));

    Assert.Equal(ComposerPointerActionKind.PasteClipboard, requested?.Kind);
    Assert.True(view.GetState().CursorIndex > 0);
}

[Fact]
public void Middle_click_requests_context_menu_at_screen_position()
{
    using var view = CreateLaidOutView("text", width: 20);
    ComposerPointerActionRequestedEventArgs? requested = null;
    view.PointerActionRequested += (_, args) => requested = args;

    view.NewMouseEvent(new Mouse
    {
        Flags = MouseFlags.MiddleButtonClicked,
        Position = new Point(3, 0),
        ScreenPosition = new Point(13, 8),
        View = view,
    });

    Assert.Equal(ComposerPointerActionKind.ShowContextMenu, requested?.Kind);
    Assert.Equal(new Point(13, 8), requested?.ScreenPosition);
}

[Fact]
public void Ctrl_v_remains_bound_to_terminal_gui_paste()
{
    using var view = CreateLaidOutView("text", width: 20);

    Assert.Contains(Command.Paste, view.KeyBindings.GetCommands(Key.V.WithCtrl));
}
```

The test file must include local `CreateLaidOutView`, `CreateSelectedView`, and `MouseAt` helpers that use real `TextView` mouse events, matching `ComposerMouseCaretSyncTests`.

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ComposerPointerActionTests"
```

Expected: compilation fails because the pointer action contract and composer selection API do not exist.

- [ ] **Step 3: Add the semantic action contract**

Create:

```csharp
using System.Drawing;

namespace Coda.Tui.Ui.Input;

internal enum ComposerPointerActionKind
{
    CopySelection,
    PasteClipboard,
    ShowContextMenu,
}

internal sealed record ComposerPointerActionRequestedEventArgs(
    ComposerPointerActionKind Kind,
    string? SelectedText,
    Point ScreenPosition) : EventArgs;
```

- [ ] **Step 4: Add native selection and gesture arbitration**

Expose:

```csharp
internal event EventHandler<ComposerPointerActionRequestedEventArgs>? PointerActionRequested;

internal bool HasComposerSelection => this.SelectedLength > 0;

internal string SelectedComposerText => this.SelectedText ?? string.Empty;

internal void ClearComposerSelection()
{
    this.IsSelecting = false;
    this.SelectionStartColumn = this.CurrentColumn;
    this.SelectionStartRow = this.CurrentRow;
    this.SetNeedsDraw();
}
```

Track suppressed click sequences:

```csharp
private bool suppressLeftGesture;
private bool suppressRightGesture;
private bool pendingRightPaste;
```

At the start of `OnMouseEvent`, after the input-disabled guard:

```csharp
if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) &&
    !mouse.Flags.HasFlag(MouseFlags.PositionReport) &&
    this.HasComposerSelection)
{
    this.suppressLeftGesture = true;
    this.RaisePointerAction(
        ComposerPointerActionKind.CopySelection,
        this.SelectedComposerText,
        mouse.ScreenPosition);
    return true;
}

if (this.suppressLeftGesture &&
    (mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased) ||
     mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked)))
{
    if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
    {
        this.suppressLeftGesture = false;
    }

    return true;
}

if (mouse.Flags.HasFlag(MouseFlags.RightButtonPressed))
{
    this.suppressRightGesture = true;
    if (this.HasComposerSelection)
    {
        this.pendingRightPaste = false;
        this.RaisePointerAction(
            ComposerPointerActionKind.CopySelection,
            this.SelectedComposerText,
            mouse.ScreenPosition);
    }
    else
    {
        this.pendingRightPaste = true;
        this.PositionCaretFromPointer(mouse);
    }

    return true;
}

if (this.suppressRightGesture &&
    mouse.Flags.HasFlag(MouseFlags.RightButtonClicked))
{
    this.suppressRightGesture = false;
    if (this.pendingRightPaste)
    {
        this.pendingRightPaste = false;
        this.RaisePointerAction(
            ComposerPointerActionKind.PasteClipboard,
            null,
            mouse.ScreenPosition);
    }

    return true;
}

if (this.suppressRightGesture && mouse.Flags.HasFlag(MouseFlags.RightButtonReleased))
{
    return true;
}

if (mouse.Flags.HasFlag(MouseFlags.MiddleButtonClicked))
{
    this.RaisePointerAction(
        ComposerPointerActionKind.ShowContextMenu,
        null,
        mouse.ScreenPosition);
    return true;
}
```

Position the right-click caret through `TextView` rather than reimplementing wrapping:

```csharp
private void PositionCaretFromPointer(Mouse mouse)
{
    var leftPress = new Mouse
    {
        Flags = MouseFlags.LeftButtonPressed,
        Position = mouse.Position,
        ScreenPosition = mouse.ScreenPosition,
        View = this,
    };

    this.RunNativeEdit(() => base.OnMouseEvent(leftPress));
}

private void RaisePointerAction(
    ComposerPointerActionKind kind,
    string? selectedText,
    Point screenPosition) =>
    this.PointerActionRequested?.Invoke(
        this,
        new ComposerPointerActionRequestedEventArgs(kind, selectedText, screenPosition));
```

All unmatched events continue through the existing `RunNativeEdit(() => base.OnMouseEvent(mouse))` path.

- [ ] **Step 5: Run pointer and caret tests GREEN**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ComposerPointerActionTests|FullyQualifiedName~ComposerMouseCaretSyncTests"
```

Expected: all selected tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src\Coda.Tui\Ui\Input\ComposerPointerAction.cs src\Coda.Tui\Ui\Input\ComposerView.cs tests\Coda.Tui.Tests\ComposerPointerActionTests.cs
git commit -m "feat(tui): expose native composer pointer actions"
```

### Task 4: Shell Clipboard Copy and Ctrl+C Precedence

**Files:**
- Create: `src/Coda.Tui/Ui/Shells/ClipboardStatusText.cs`
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify: `tests/Coda.Tui.Tests/RetainedShellFixture.cs`
- Create: `tests/Coda.Tui.Tests/ComposerClipboardShellTests.cs`

- [ ] **Step 1: Write failing copy/chord tests**

Add shell tests for:

```csharp
[Fact]
public void Ctrl_c_copies_composer_selection_before_transcript_or_exit()
{
    string? copied = null;
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        clipboardWriter: text =>
        {
            copied = text;
            return true;
        });
    SelectComposerText(fixture.Shell.Composer, "alpha", startColumn: 1, endColumn: 4);

    fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

    Assert.Equal("lph", copied);
    Assert.False(fixture.Shell.Composer.HasComposerSelection);
    Assert.Empty(fixture.Actions);
    Assert.Equal("3 symbols copied to clipboard", fixture.Shell.Operational.Status.Text);
}

[Fact]
public void Failed_composer_copy_preserves_selection()
{
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        clipboardWriter: _ => false,
        addTimeout: (_, _) => new object(),
        removeTimeout: _ => true);
    SelectComposerText(fixture.Shell.Composer, "alpha", 1, 4);

    fixture.Shell.Composer.NewKeyDownEvent(Key.C.WithCtrl);

    Assert.True(fixture.Shell.Composer.HasComposerSelection);
    Assert.Equal("Clipboard unavailable", fixture.Shell.Operational.Status.Text);
    Assert.Empty(fixture.Actions);
}

[Fact]
public void Left_click_copy_uses_same_shell_copy_path()
{
    string? copied = null;
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        clipboardWriter: text =>
        {
            copied = text;
            return true;
        });
    SelectComposerText(fixture.Shell.Composer, "alpha", 1, 4);

    fixture.Shell.Composer.NewMouseEvent(MouseAt(MouseFlags.LeftButtonPressed, 0, 0));

    Assert.Equal("lph", copied);
    Assert.False(fixture.Shell.Composer.HasComposerSelection);
    Assert.Equal("3 symbols copied to clipboard", fixture.Shell.Operational.Status.Text);
}
```

`SelectComposerText` must use native `SelectionStartColumn`, `SelectionStartRow`, `InsertionPoint`, and `IsSelecting`, then assert `SelectedLength > 0` before the action.

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ComposerClipboardShellTests"
```

Expected: tests fail because the shell does not subscribe to composer pointer actions or prioritize composer selection.

- [ ] **Step 3: Extract shared clipboard status formatting**

Create:

```csharp
using System.Globalization;

namespace Coda.Tui.Ui.Shells;

internal static class ClipboardStatusText
{
    internal static string Copied(string text) =>
        Format(text, "copied to clipboard");

    internal static string Pasted(string text) =>
        Format(text, "pasted from clipboard");

    internal static int CountSymbols(string text)
    {
        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text ?? string.Empty);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (element is not "\r" and not "\n" and not "\r\n")
            {
                count++;
            }
        }

        return count;
    }

    private static string Format(string text, string action)
    {
        var count = CountSymbols(text);
        return count == 1
            ? $"1 symbol {action}"
            : $"{count} symbols {action}";
    }
}
```

Replace the existing transcript-local `CountSymbols`/`CopySuccessMessage` implementation with `ClipboardStatusText.CountSymbols` and `ClipboardStatusText.Copied`.

- [ ] **Step 4: Wire composer copy handling**

Subscribe/unsubscribe `Composer.PointerActionRequested`.

Before transcript selection in `TryHandleShellKey`:

```csharp
if (this.TryCopyComposerSelection())
{
    return true;
}
```

Implement:

```csharp
private bool TryCopyComposerSelection()
{
    if (!this.Composer.HasComposerSelection)
    {
        return false;
    }

    this.CopyComposerSelection(this.Composer.SelectedComposerText);
    return true;
}

private void CopyComposerSelection(string text)
{
    if (ClipboardStatusText.CountSymbols(text) == 0 || this.clipboardWriter(text))
    {
        this.Composer.ClearComposerSelection();
        this.ShowTransientOperationalStatus(
            new OperationalStatus(ClipboardStatusText.Copied(text), OperationalTone.Ready, false),
            TimeSpan.FromSeconds(1.5));
        return;
    }

    this.ShowTransientOperationalStatus(
        new OperationalStatus("Clipboard unavailable", OperationalTone.Warning, false),
        TimeSpan.FromSeconds(1.5));
}
```

In the composer pointer handler, route `CopySelection` to `CopyComposerSelection` only when the prompt overlay is hidden and the composer is enabled.

- [ ] **Step 5: Run copy tests GREEN**

Run the command from Step 2.

Expected: all current `ComposerClipboardShellTests` pass.

- [ ] **Step 6: Run transcript copy/chord regressions**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Ctrl_c_with_selection|FullyQualifiedName~Clipboard_unavailable_keeps_selection|FullyQualifiedName~Copy_status_|FullyQualifiedName~ShellCommandChord"
```

Expected: all selected tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells\ClipboardStatusText.cs src\Coda.Tui\Ui\Shells\TerminalGuiShellBase.cs tests\Coda.Tui.Tests\RetainedShellFixture.cs tests\Coda.Tui.Tests\ComposerClipboardShellTests.cs
git commit -m "feat(tui): copy composer selections"
```

### Task 5: Right-Click Paste and Middle-Click Menu

**Files:**
- Modify: `src/Coda.Tui/Ui/Shells/TerminalGuiShellBase.cs`
- Modify: `src/Coda.Tui/Ui/Shells/FullscreenTuiShell.cs`
- Modify: `src/Coda.Tui/Ui/Shells/InlineTuiShell.cs`
- Modify: `tests/Coda.Tui.Tests/RetainedShellFixture.cs`
- Modify: `tests/Coda.Tui.Tests/ComposerClipboardShellTests.cs`

- [ ] **Step 1: Write failing paste/menu/error tests**

Add:

```csharp
[Fact]
public void Right_click_without_selection_pastes_at_clicked_wrapped_caret()
{
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        clipboardReader: () => new ClipboardReadResult(true, "X"));
    fixture.Shell.Composer.SetDraft("alpha beta gamma", 0);
    fixture.Shell.Composer.Width = 6;
    fixture.Shell.Composer.Layout(new Size(6, 4));

    RightClick(fixture.Shell.Composer, column: 2, row: 1);

    var cursor = fixture.Shell.Composer.GetState().CursorIndex;
    Assert.Equal("alpha beXta gamma", fixture.Shell.Composer.GetDraft());
    Assert.Equal(9, cursor);
    Assert.Equal("1 symbol pasted from clipboard", fixture.Shell.Operational.Status.Text);
}

[Theory]
[InlineData(false, "ignored")]
[InlineData(true, "")]
public void Unavailable_or_empty_clipboard_does_not_mutate_draft(bool available, string text)
{
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        clipboardReader: () => new ClipboardReadResult(available, text),
        addTimeout: (_, _) => new object(),
        removeTimeout: _ => true);
    fixture.Shell.Composer.SetDraft("alpha", 2);

    RightClick(fixture.Shell.Composer, 2, 0);

    Assert.Equal("alpha", fixture.Shell.Composer.GetDraft());
    Assert.Equal(
        available ? "Clipboard is empty" : "Clipboard unavailable",
        fixture.Shell.Operational.Status.Text);
}

[Fact]
public void Right_click_with_selection_copies_instead_of_pasting()
{
    var reads = 0;
    string? copied = null;
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        clipboardWriter: text =>
        {
            copied = text;
            return true;
        },
        clipboardReader: () =>
        {
            reads++;
            return new ClipboardReadResult(true, "X");
        });
    SelectComposerText(fixture.Shell.Composer, "alpha", 1, 4);

    RightClick(fixture.Shell.Composer, 0, 0);

    Assert.Equal("lph", copied);
    Assert.Equal(0, reads);
    Assert.Equal("alpha", fixture.Shell.Composer.GetDraft());
}

[Fact]
public void Middle_click_opens_existing_context_menu_at_pointer()
{
    using var fixture = RetainedShellFixture.Create(activeWork: false);

    fixture.Shell.Composer.NewMouseEvent(new Mouse
    {
        Flags = MouseFlags.MiddleButtonClicked,
        Position = new Point(3, 0),
        ScreenPosition = fixture.Shell.Composer.ViewportToScreen(new Point(3, 0)),
        View = fixture.Shell.Composer,
    });

    Assert.True(fixture.Shell.Composer.ContextMenu.Visible);
}

[Fact]
public async Task Prompt_overlay_blocks_composer_mouse_clipboard_actions()
{
    var reads = 0;
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        clipboardReader: () =>
        {
            reads++;
            return new ClipboardReadResult(true, "X");
        });
    await fixture.Shell.ApplyAsync(
        UiSessionSnapshot.Empty with { PendingPrompt = UiPromptRequest.Confirm("Proceed?", false) },
        CancellationToken.None);

    RightClick(fixture.Shell.Composer, 0, 0);

    Assert.Equal(0, reads);
}

[Fact]
public async Task Startup_state_blocks_composer_mouse_clipboard_actions()
{
    var reads = 0;
    using var fixture = RetainedShellFixture.Create(
        activeWork: false,
        clipboardReader: () =>
        {
            reads++;
            return new ClipboardReadResult(true, "X");
        });
    await fixture.Shell.ApplyAsync(
        UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("startup", "Starting…", null),
        },
        CancellationToken.None);

    RightClick(fixture.Shell.Composer, 0, 0);

    Assert.Equal(0, reads);
    Assert.Empty(fixture.Shell.Composer.GetDraft());
}
```

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ComposerClipboardShellTests"
```

Expected: compilation fails because `ClipboardReadResult` and the reader seam do not exist.

- [ ] **Step 3: Add clipboard reader seam**

Near the shell class, define:

```csharp
internal readonly record struct ClipboardReadResult(bool Available, string Text);
```

Add to `TerminalGuiShellBase`, `FullscreenTuiShell`, `InlineTuiShell`, and `RetainedShellFixture`:

```csharp
Func<ClipboardReadResult>? clipboardReader = null
```

Initialize the default in the base shell:

```csharp
this.clipboardReader = clipboardReader ?? (() =>
{
    if (this.app.Clipboard?.TryGetClipboardData(out var text) == true)
    {
        return new ClipboardReadResult(true, text ?? string.Empty);
    }

    return new ClipboardReadResult(false, string.Empty);
});
```

Keep this parameter adjacent to `clipboardWriter` in every constructor.

- [ ] **Step 4: Implement paste and context-menu actions**

Extend the composer pointer handler:

```csharp
case ComposerPointerActionKind.PasteClipboard:
    this.PasteComposerClipboard();
    break;

case ComposerPointerActionKind.ShowContextMenu:
    if (this.Composer.ContextMenu is { } menu)
    {
        menu.MakeVisible(args.ScreenPosition);
    }
    else
    {
        this.ShowTransientOperationalStatus(
            new OperationalStatus("Context menu unavailable", OperationalTone.Warning, false),
            TimeSpan.FromSeconds(1.5));
    }

    break;
```

Implement:

```csharp
private void PasteComposerClipboard()
{
    var result = this.clipboardReader();
    if (!result.Available)
    {
        this.ShowTransientOperationalStatus(
            new OperationalStatus("Clipboard unavailable", OperationalTone.Warning, false),
            TimeSpan.FromSeconds(1.5));
        return;
    }

    if (string.IsNullOrEmpty(result.Text))
    {
        this.ShowTransientOperationalStatus(
            new OperationalStatus("Clipboard is empty", OperationalTone.Warning, false),
            TimeSpan.FromSeconds(1.5));
        return;
    }

    this.Composer.NewPasteEvent(result.Text);
    this.ShowTransientOperationalStatus(
        new OperationalStatus(ClipboardStatusText.Pasted(result.Text), OperationalTone.Ready, false),
        TimeSpan.FromSeconds(1.5));
}
```

The pointer handler must return immediately when `PromptOverlay.Visible`, `composerDisabled`, or `!Composer.InputEnabled`.

- [ ] **Step 5: Run paste/menu tests GREEN**

Run the command from Step 2.

Expected: all `ComposerClipboardShellTests` pass.

- [ ] **Step 6: Run native paste, wrapped caret, and mode regressions**

Run:

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Bracketed_paste|FullyQualifiedName~ComposerMouseCaretSyncTests|FullyQualifiedName~FullscreenTuiShellTests|FullyQualifiedName~InlineTuiShellTests"
```

Expected: all selected tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Tui\Ui\Shells tests\Coda.Tui.Tests\RetainedShellFixture.cs tests\Coda.Tui.Tests\ComposerClipboardShellTests.cs
git commit -m "feat(tui): paste and open composer menu with mouse"
```

### Task 6: Documentation and Version

**Files:**
- Modify: `README.md`
- Modify: `version.json`

- [ ] **Step 1: Update user-facing behavior**

Replace the README mouse paragraph with:

```markdown
**Mouse:** In the transcript, **left-drag** selects text and `Ctrl+C` copies it. In the composer,
**left-drag** selects input; `Ctrl+C`, left-click, or right-click copies and clears an existing selection.
Right-click with no selection pastes at the clicked caret, and middle-click opens the editor context menu.
`Ctrl+V` remains direct paste. **`Shift`-drag** hands native selection and copy to the terminal where
supported. `--no-mouse` leaves selection and copy native to the terminal.
```

Add to the `/model` description:

```markdown
The interactive picker marks the active model as **Current** and opens with it selected.
```

- [ ] **Step 2: Bump the release version**

Change `version.json` to:

```json
{
  "major": 0,
  "minor": 1,
  "build": 71
}
```

- [ ] **Step 3: Check documentation and version diff**

Run:

```powershell
git diff --check
git diff -- README.md version.json
```

Expected: no whitespace errors; only the documented behavior and build number change.

- [ ] **Step 4: Commit**

```powershell
git add README.md version.json
git commit -m "docs: describe picker and composer mouse UX"
```

### Task 7: Full Verification and Review Gate

**Files:**
- Review all files changed since `main`

- [ ] **Step 1: Run the full TUI suite**

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --no-restore
```

Expected: at least 1,133 tests pass, 0 fail.

- [ ] **Step 2: Run engine and auth suites**

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --no-restore
dotnet test tests\LlmAuth.Tests\LlmAuth.Tests.csproj --no-restore
```

Expected: 1,563 engine tests and 103 auth tests pass, 0 fail.

- [ ] **Step 3: Build the solution warning-free**

```powershell
dotnet build LlmAuth.slnx --no-restore
```

Expected: build succeeds with 0 warnings and 0 errors.

- [ ] **Step 4: Inspect the complete branch diff**

```powershell
git diff --check main...HEAD
git status --short
git log --oneline main..HEAD
```

Expected: no whitespace errors, no uncommitted files, and focused commits for prompt state, model behavior, pointer actions, clipboard behavior, and docs/version.

- [ ] **Step 5: Request independent code review**

Dispatch a read-only code-review agent over `main...HEAD`, explicitly asking it to check:

- wrapped-line mouse positioning and selection lifecycle;
- accidental Ctrl+C exit arming while composer or transcript text is selected;
- copy failure preserving selection;
- paste failure preserving draft/cursor;
- prompt overlay/startup input isolation;
- Terminal.Gui context-menu positioning and lifecycle;
- current-model comparison and persistence side effects;
- constructor seam propagation across fullscreen, inline, production, and test paths.

Expected: no high-confidence findings. Fix any finding with a new RED test, minimal implementation, focused GREEN run, and a separate commit; then repeat Steps 1-5.
