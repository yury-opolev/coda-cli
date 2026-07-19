# Ctrl+Enter Newline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Ctrl+Enter as an intuitive composer newline shortcut while retaining Ctrl+J as the cross-terminal fallback.

**Architecture:** Extend the existing host-neutral `UiActionMap`; both shortcuts resolve to the existing `InsertNewline` action and native incremental editor path. Plain Enter submission behavior remains unchanged.

**Tech Stack:** .NET 10, C# 14, Terminal.Gui 2.4.17, xUnit

---

### Task 1: Add and Document the Binding

**Files:**
- Modify: `src/Coda.Tui/Ui/Input/UiActionMap.cs`
- Modify: `src/Coda.Tui/Ui/Input/ComposerView.cs`
- Modify: `src/Coda.Tui/ImmediateCli.cs`
- Modify: `README.md`
- Test: `tests/Coda.Tui.Tests/ComposerControllerTests.cs`
- Test: `tests/Coda.Tui.Tests/ComposerViewTests.cs`
- Test: `tests/Coda.Tui.Tests/ImmediateCliTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public void Ctrl_enter_and_ctrl_j_map_to_insert_newline()
{
    Assert.Equal(UiAction.InsertNewline, UiActionMap.Map(Key.Enter.WithCtrl, TypingMiddle));
    Assert.Equal(UiAction.InsertNewline, UiActionMap.Map(Key.J.WithCtrl, TypingMiddle));
    Assert.Equal(UiAction.Submit, UiActionMap.Map(Key.Enter, TypingMiddle));
}
```

```csharp
[Fact]
public void Ctrl_enter_inserts_newline_without_submitting()
{
    var controller = CreateController();
    using var view = new ComposerView(controller);
    var submissions = new List<string>();
    view.Submitted += (_, text) => submissions.Add(text);
    view.SetDraft("hello", 5);

    view.NewKeyDownEvent(Key.Enter.WithCtrl);

    Assert.Equal("hello\n", view.GetDraft());
    Assert.Empty(submissions);
}
```

Update the immediate-help test to require both `Ctrl+Enter` and `Ctrl+J`.

- [ ] **Step 2: Run tests RED**

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Ctrl_enter|FullyQualifiedName~Help_lists_interactive_controls"
```

Expected: Ctrl+Enter maps to `UiAction.None` or submit behavior, and help lacks the shortcut.

- [ ] **Step 3: Implement the shared mapping**

```csharp
// Ctrl+Enter is the intuitive multiline shortcut; Ctrl+J remains the broadly
// compatible terminal fallback.
if (key == Key.Enter.WithCtrl || key == Key.J.WithCtrl)
{
    return UiAction.InsertNewline;
}
```

Place this before the plain `Key.Enter` branch.

- [ ] **Step 4: Update user-facing text**

Change the composer summary and help to describe `Ctrl+Enter` first and `Ctrl+J` as the fallback:

```text
Ctrl+Enter      Insert a newline without submitting.
Ctrl+J          Insert a newline (terminal-compatible fallback).
```

Update README's key summary to: `` `Ctrl+Enter` (or `Ctrl+J`) inserts a newline ``.

- [ ] **Step 5: Run focused and full TUI tests GREEN**

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~UiActionMapTests|FullyQualifiedName~ComposerViewTests|FullyQualifiedName~ImmediateCliTests"
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj
```

Expected: all selected and full TUI tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/Coda.Tui/Ui/Input/UiActionMap.cs src/Coda.Tui/Ui/Input/ComposerView.cs src/Coda.Tui/ImmediateCli.cs README.md tests/Coda.Tui.Tests/ComposerControllerTests.cs tests/Coda.Tui.Tests/ComposerViewTests.cs tests/Coda.Tui.Tests/ImmediateCliTests.cs
git commit -m "feat(tui): insert newline with Ctrl+Enter"
```

### Task 2: Release Coda 0.1.73

**Files:**
- Modify: `version.json`

- [ ] **Step 1: Set build to 73**

```json
{
  "major": 0,
  "minor": 1,
  "build": 73
}
```

- [ ] **Step 2: Run full validation**

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj
dotnet test tests\Engine.Tests\Engine.Tests.csproj
dotnet test tests\LlmAuth.Tests\LlmAuth.Tests.csproj
dotnet build LlmAuth.slnx --no-restore
git diff --check
```

Expected: all tests pass; build reports 0 warnings and 0 errors.

- [ ] **Step 3: Commit and release**

```powershell
git add version.json
git commit -m "chore: bump version to 0.1.73"
```

After independent review, merge through a pull request, sync `main`, then:

```powershell
.\build.ps1 -NoBump -Test
.\publish.ps1 -Flavor tool
dotnet tool update --global --add-source .\publish\tool --version 0.1.73 Coda.Cli
coda --version
```
