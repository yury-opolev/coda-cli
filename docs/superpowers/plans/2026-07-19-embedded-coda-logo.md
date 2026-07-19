# Embedded Coda Logo Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the generated Figlet startup wordmark with the refreshed six-line embedded Coda logo and release it as Coda 0.1.72.

**Architecture:** `Branding.BannerLines` remains the single embedded source for the literal logo rows. `Banner` renders those rows as styled text using the existing accent color, without runtime file access or changes to the welcome panel.

**Tech Stack:** .NET 10, C# 14, Spectre.Console 0.55.2, xUnit

---

## File Structure

- Modify `src/Coda.Tui/Branding.cs` — store the refreshed literal wordmark.
- Modify `src/Coda.Tui/Rendering/Banner.cs` — render literal styled text instead of Figlet.
- Modify `tests/Coda.Tui.Tests/BannerTests.cs` — verify the supplied wordmark appears.
- Modify `tests/Coda.Tui.Tests/CoreLogicTests.cs` — verify the embedded branding rows exactly.
- Modify `version.json` — bump Coda to 0.1.72.

### Task 1: Embed and Render the Refreshed Logo

**Files:**
- Modify: `src/Coda.Tui/Branding.cs:22-28`
- Modify: `src/Coda.Tui/Rendering/Banner.cs:42-47`
- Test: `tests/Coda.Tui.Tests/BannerTests.cs`
- Test: `tests/Coda.Tui.Tests/CoreLogicTests.cs:118-127`

- [ ] **Step 1: Write failing logo tests**

Add:

```csharp
[Fact]
public void Render_shows_the_embedded_coda_wordmark()
{
    var console = NewConsole();

    Banner.Render(console, new SessionState("claude-ai", "C:\\work"));

    Assert.Contains(" ┌───┐      ┌┐", console.Output, StringComparison.Ordinal);
    Assert.Contains(" └───┘└──┘└──┘└───┘", console.Output, StringComparison.Ordinal);
}
```

Extend `BrandingTests`:

```csharp
[Fact]
public void Banner_lines_match_the_refreshed_embedded_logo()
{
    Assert.Equal(
    [
        " ┌───┐      ┌┐",
        " │┬─┐│┌──┐┌─┘│┌──┐",
        " ││ └┘│┬┐││┬┐││┬┐│",
        " ││ ┌┐││││││││││││",
        " │└─┴││└┴││└┴││└┴└┐",
        " └───┘└──┘└──┘└───┘",
    ],
    Branding.BannerLines);
}
```

- [ ] **Step 2: Run tests and verify RED**

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~Render_shows_the_embedded_coda_wordmark|FullyQualifiedName~Banner_lines_match_the_refreshed_embedded_logo"
```

Expected: both tests fail because the placeholder banner rows and Figlet output do not contain the refreshed logo.

- [ ] **Step 3: Embed the refreshed rows**

Replace `Branding.BannerLines` with:

```csharp
/// <summary>Six-line Unicode wordmark rendered above the welcome panel.</summary>
public static IReadOnlyList<string> BannerLines { get; } =
[
    " ┌───┐      ┌┐",
    " │┬─┐│┌──┐┌─┘│┌──┐",
    " ││ └┘│┬┐││┬┐││┬┐│",
    " ││ ┌┐││││││││││││",
    " │└─┴││└┴││└┴││└┴└┐",
    " └───┘└──┘└──┘└───┘",
];
```

- [ ] **Step 4: Render literal styled text**

Replace `WordmarkInto` with:

```csharp
/// <summary>Renders the embedded Unicode wordmark in the accent color.</summary>
private static void WordmarkInto(IAnsiConsole console)
{
    var wordmark = string.Join(Environment.NewLine, Branding.BannerLines);
    console.Write(new Text(wordmark, new Style(foreground: Theme.AccentColor)));
    console.WriteLine();
}
```

- [ ] **Step 5: Run focused tests GREEN**

Run the command from Step 2.

Expected: 2 tests pass.

- [ ] **Step 6: Run banner and branding regressions**

```powershell
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~BannerTests|FullyQualifiedName~BrandingTests|FullyQualifiedName~InteractiveProgramTests"
```

Expected: all selected tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src\Coda.Tui\Branding.cs src\Coda.Tui\Rendering\Banner.cs tests\Coda.Tui.Tests\BannerTests.cs tests\Coda.Tui.Tests\CoreLogicTests.cs
git commit -m "feat(tui): embed refreshed Coda logo"
```

### Task 2: Release and Verify Coda 0.1.72

**Files:**
- Modify: `version.json`

- [ ] **Step 1: Bump the version**

Set:

```json
{
  "major": 0,
  "minor": 1,
  "build": 72
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

Expected: all tests pass and the build reports 0 warnings and 0 errors.

- [ ] **Step 3: Commit**

```powershell
git add version.json
git commit -m "chore: bump version to 0.1.72"
```

- [ ] **Step 4: Review and publish**

Request independent review of `main...HEAD`. After approval, push the branch, create and merge a pull request, fast-forward local `main`, then run:

```powershell
.\build.ps1 -NoBump -Test
.\publish.ps1 -Flavor tool
dotnet tool update --global --add-source .\publish\tool --version 0.1.72 Coda.Cli
coda --version
```

Expected: `Coda v0.1.72 — an agentic coding assistant`.
