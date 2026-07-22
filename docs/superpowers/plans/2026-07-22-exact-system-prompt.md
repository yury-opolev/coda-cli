# Exact Startup System Prompt Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add byte-faithful `--system-prompt` and `--system-prompt-file` startup overrides for interactive and serve sessions, with exact replacement semantics and durable resume/fork/export behavior.

**Architecture:** CLI-only source descriptors resolve inline text or one UTF-8 file before any session, MCP runtime, or serve transport becomes visible. The SDK receives only resolved text through `SessionOptions.SystemPromptOverride`, uses one effective-prompt component for root turns and `/context`, and persists the optional override separately from per-turn audited effective prompts.

**Tech Stack:** C# 14, .NET 10, xUnit, `System.Text.Json.Nodes`, strict `UTF8Encoding`, existing Coda SDK/TUI/serve/session persistence.

---

## File responsibility map

### New production files

- `src/Coda.Tui/SystemPromptSourceResolver.cs` — CLI source records, strict flag extraction, startup-directory path resolution, one-time strict UTF-8 decoding, and source-specific exceptions.
- `src/Coda.Tui/SystemPromptCompatibilityWarning.cs` — pure Claude.ai compatibility-warning policy that never mutates the override.
- `src/Coda.Sdk/EffectiveSystemPrompt.cs` — the single SDK authority for exact override versus normal layered prompt construction.
- `src/Coda.Sdk/SessionMetadata.cs` — typed persisted metadata (`SessionMetadata`) and typed transcript load result (`StoredSession`).

### Existing production files

- `src/Coda.Tui/Ui/Mode/TuiLaunchOptions.cs` — consume prompt-source flags without disturbing resume/fork/display arguments.
- `src/Coda.Tui/InteractiveProgram.cs` — resolve the source before selecting/starting a session, seed resume metadata, and emit the compatibility warning.
- `src/Coda.Tui/ServeOptions.cs` — strict parsing only for the two new prompt-source flags while retaining forward-compatible parsing for unrelated serve flags.
- `src/Coda.Tui/ServeRunner.cs` — resolve before credential, MCP, transport, or host creation and map resolved text into `SessionOptions`.
- `src/Coda.Sdk/SessionOptions.cs` — carry nullable resolved override text; `null` means absent while empty/whitespace strings remain valid.
- `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs` — use `EffectiveSystemPrompt.Resolve` for normal and scheduled root loop specs.
- `src/Coda.Sdk/CodaSession.cs` — use the same prompt for `/context`, persist metadata, and enforce startup-over-persisted resume precedence.
- `src/Coda.Tui/Repl/SessionState.cs` — retain immutable startup override authority and mutable current effective override.
- `src/Coda.Tui/Agent/AgentRunner.cs` — project the current override into every SDK options snapshot.
- `src/Coda.Tui/Commands/ContextCommand.cs` — pass the override into the temporary analysis session.
- `src/Coda.Tui/Commands/ResumeCommand.cs` — load typed metadata and apply startup precedence during in-process resume.
- `src/Coda.Tui/Setup/SetupWizard.cs` — use the exact override for first-run verification.
- `src/Coda.Tui/SessionCli.cs` — return messages plus metadata for startup resume/fork.
- `src/Coda.Tui/HeadlessRunner.cs` — preserve metadata through existing resume/fork paths without adding a new `coda run` flag.
- `src/Coda.Sdk/SessionTranscriptStore.cs` — additive optional metadata storage with legacy overload compatibility.
- `src/Coda.Sdk/Serve/ServeHost.cs` — restore metadata before initialization can start scheduled roots.
- `src/Coda.Sdk/SessionForking.cs` — copy resolved session metadata to new session IDs.
- `src/Coda.Sdk/SessionBundle.cs` and `src/Coda.Sdk/SessionBundleService.cs` — round-trip `systemPromptOverride` separately from audited `systemPrompt` without changing `coda.session/1`.
- `src/LlmClient/AnthropicMessagesClient.cs`, `src/LlmClient/OpenAiRequest.cs`, `src/LlmClient/OpenAiResponsesRequest.cs` — preserve explicit-empty override semantics while using valid provider wire shapes: Anthropic omits its optional `system` field because empty text blocks are invalid; OpenAI serializes explicit empty values.
- `src/Coda.Tui/ImmediateCli.cs`, `README.md`, `docs/API.md`, `docs/serve-protocol.md` — document flags, exact semantics, encoding/path behavior, warnings, and persistence.

### Test files

- `tests/Coda.Tui.Tests/SystemPromptSourceResolverTests.cs` — exact source parsing and file-decoding contract.
- `tests/Engine.Tests/EffectiveSystemPromptTests.cs` — replacement/fallback semantics.
- `tests/Engine.Tests/ExactSystemPromptWireTests.cs` — explicit-empty system wire behavior: Anthropic omits its invalid empty block, while OpenAI request shapes serialize empty values.
- Existing parser, runner, context, setup, resume, fork, transcript, bundle, audit, scheduled-root, and serve-host test files listed in the tasks below.

## Invariants used by every task

- `SystemPromptOverride == null` means “construct the normal Coda prompt.”
- `SystemPromptOverride == ""` and whitespace-only values are present exact overrides.
- An explicit empty override suppresses every normal prompt layer; Anthropic omits its optional `system` field for that invalid-empty-block wire case, while OpenAI shapes serialize explicit empty values.
- A source file is resolved from the process startup working directory, never from `--cwd`.
- The source file is read once; only a leading UTF-8 BOM is removed.
- The exact override replaces built-in, project, output-style, and provider-prefix layers for root turns only.
- Subagents continue using their own role prompt.
- Persisted override metadata is not reconstructed from audit `systemPrompt`.
- Resume precedence is `startup override ?? persisted override ?? normal construction`.

### Task 1: Strict shared source parser and UTF-8 resolver

**Files:**
- Create: `src/Coda.Tui/SystemPromptSourceResolver.cs`
- Create: `tests/Coda.Tui.Tests/SystemPromptSourceResolverTests.cs`

- [ ] **Step 1: Write the failing parser and decoding tests**

```csharp
using System.Text;

namespace Coda.Tui.Tests;

public sealed class SystemPromptSourceResolverTests
{
    [Fact]
    public void Extract_preserves_inline_text_and_remaining_argument_order()
    {
        var ok = SystemPromptSourceResolver.TryExtract(
            ["--plain", "--system-prompt", " \r\nexact\n", "--resume", "abc"],
            out var remaining,
            out var source,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(["--plain", "--resume", "abc"], remaining);
        Assert.Equal(" \r\nexact\n", Assert.IsType<SystemPromptSource.Inline>(source).Text);
    }

    [Theory]
    [InlineData("--system-prompt")]
    [InlineData("--system-prompt-file")]
    public void Extract_rejects_a_missing_value(string flag)
    {
        Assert.False(SystemPromptSourceResolver.TryExtract(
            [flag], out _, out _, out var error));
        Assert.Equal($"{flag} requires a value.", error);
    }

    [Fact]
    public void Extract_rejects_every_second_source()
    {
        Assert.False(SystemPromptSourceResolver.TryExtract(
            ["--system-prompt", "one", "--system-prompt-file", "prompt.txt"],
            out _, out _, out var error));
        Assert.Equal(
            "Specify only one of --system-prompt or --system-prompt-file, once.",
            error);
    }

    [Fact]
    public void Extract_rejects_a_prompt_flag_where_the_previous_source_value_is_missing()
    {
        Assert.False(SystemPromptSourceResolver.TryExtract(
            ["--system-prompt", "--system-prompt-file", "prompt.txt"],
            out _, out _, out var error));
        Assert.Equal("--system-prompt requires a value.", error);
    }

    [Theory]
    [InlineData("--system-prompt=exact")]
    [InlineData("--system-prompt-file=prompt.txt")]
    public void Extract_rejects_unsupported_equals_forms(string argument)
    {
        Assert.False(SystemPromptSourceResolver.TryExtract(
            [argument], out _, out _, out var error));
        Assert.Contains("separate value", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task File_source_preserves_bom_unicode_mixed_line_endings_and_trailing_newline()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            const string expected = "  α\r\nβ\n";
            await File.WriteAllBytesAsync(
                Path.Combine(root, "prompt.txt"),
                [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(expected)]);

            var actual = await SystemPromptSourceResolver.ResolveAsync(
                new SystemPromptSource.FilePath("prompt.txt"), root);

            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_utf8_is_rejected_without_replacement_characters()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "bad.txt"), [0xC3, 0x28]);

            var error = await Assert.ThrowsAsync<SystemPromptSourceException>(
                () => SystemPromptSourceResolver.ResolveAsync(
                    new SystemPromptSource.FilePath("bad.txt"), root));

            Assert.Contains("valid UTF-8", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resolver_reads_the_file_exactly_once()
    {
        var reads = 0;
        Task<byte[]> Read(string path, CancellationToken ct)
        {
            reads++;
            return Task.FromResult(Encoding.UTF8.GetBytes("exact"));
        }

        var result = await SystemPromptSourceResolver.ResolveAsync(
            new SystemPromptSource.FilePath("prompt.txt"),
            @"C:\startup",
            Read,
            CancellationToken.None);

        Assert.Equal("exact", result);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    public async Task Empty_and_whitespace_files_are_exact_overrides(string expected)
    {
        var bytes = Encoding.UTF8.GetBytes(expected);
        var result = await SystemPromptSourceResolver.ResolveAsync(
            new SystemPromptSource.FilePath("prompt.txt"),
            @"C:\startup",
            (path, ct) => Task.FromResult(bytes),
            CancellationToken.None);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Missing_file_and_directory_are_reported()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await Assert.ThrowsAsync<SystemPromptSourceException>(
                () => SystemPromptSourceResolver.ResolveAsync(
                    new SystemPromptSource.FilePath("missing.txt"), root));
            Directory.CreateDirectory(Path.Combine(root, "folder"));
            var directoryError = await Assert.ThrowsAsync<SystemPromptSourceException>(
                () => SystemPromptSourceResolver.ResolveAsync(
                    new SystemPromptSource.FilePath("folder"), root));
            Assert.Contains("directory", directoryError.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Malformed_path_and_access_failure_are_wrapped()
    {
        await Assert.ThrowsAsync<SystemPromptSourceException>(
            () => SystemPromptSourceResolver.ResolveAsync(
                new SystemPromptSource.FilePath("\0"), @"C:\startup"));

        var access = await Assert.ThrowsAsync<SystemPromptSourceException>(
            () => SystemPromptSourceResolver.ResolveAsync(
                new SystemPromptSource.FilePath("prompt.txt"),
                @"C:\startup",
                (path, ct) => Task.FromException<byte[]>(
                    new UnauthorizedAccessException("denied")),
                CancellationToken.None));
        Assert.Contains("denied", access.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Large_file_is_not_truncated()
    {
        var expected = new string('x', 1_100_000) + "\n";
        var result = await SystemPromptSourceResolver.ResolveAsync(
            new SystemPromptSource.FilePath("large.txt"),
            @"C:\startup",
            (path, ct) => Task.FromResult(Encoding.UTF8.GetBytes(expected)),
            CancellationToken.None);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Relative_file_uses_the_captured_startup_directory()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var startup = Path.Combine(root, "startup");
            var sessionCwd = Path.Combine(root, "session");
            Directory.CreateDirectory(startup);
            Directory.CreateDirectory(sessionCwd);
            await File.WriteAllTextAsync(
                Path.Combine(startup, "prompt.txt"),
                "startup");
            await File.WriteAllTextAsync(
                Path.Combine(sessionCwd, "prompt.txt"),
                "session");

            var result = await SystemPromptSourceResolver.ResolveAsync(
                new SystemPromptSource.FilePath("prompt.txt"),
                startup);

            Assert.Equal("startup", result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~SystemPromptSourceResolverTests"`

Expected: FAIL with compiler errors that `SystemPromptSourceResolver`, `SystemPromptSource`, and `SystemPromptSourceException` do not exist.

- [ ] **Step 3: Implement the source records, extraction, and strict resolver**

```csharp
using System.Text;

namespace Coda.Tui;

public abstract record SystemPromptSource
{
    public sealed record Inline(string Text) : SystemPromptSource;
    public sealed record FilePath(string Path) : SystemPromptSource;
}

public sealed class SystemPromptSourceException : Exception
{
    public SystemPromptSourceException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public static class SystemPromptSourceResolver
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryExtract(
        IReadOnlyList<string> args,
        out IReadOnlyList<string> remainingArgs,
        out SystemPromptSource? source,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        var remaining = new List<string>(args.Count);
        source = null;
        error = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith("--system-prompt=", StringComparison.Ordinal) ||
                argument.StartsWith("--system-prompt-file=", StringComparison.Ordinal))
            {
                remainingArgs = remaining;
                error = $"Malformed '{argument}'. Pass the option and value as separate arguments.";
                return false;
            }

            if (argument is not ("--system-prompt" or "--system-prompt-file"))
            {
                remaining.Add(argument);
                continue;
            }

            if (source is not null)
            {
                remainingArgs = remaining;
                error = "Specify only one of --system-prompt or --system-prompt-file, once.";
                return false;
            }

            if (++index >= args.Count ||
                args[index] is "--system-prompt" or "--system-prompt-file")
            {
                remainingArgs = remaining;
                error = $"{argument} requires a value.";
                return false;
            }

            source = argument == "--system-prompt"
                ? new SystemPromptSource.Inline(args[index])
                : new SystemPromptSource.FilePath(args[index]);
        }

        remainingArgs = remaining;
        return true;
    }

    public static Task<string?> ResolveAsync(
        SystemPromptSource? source,
        string startupWorkingDirectory,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(source, startupWorkingDirectory, File.ReadAllBytesAsync, cancellationToken);

    internal static async Task<string?> ResolveAsync(
        SystemPromptSource? source,
        string startupWorkingDirectory,
        Func<string, CancellationToken, Task<byte[]>> readAllBytesAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startupWorkingDirectory);
        ArgumentNullException.ThrowIfNull(readAllBytesAsync);

        if (source is null)
        {
            return null;
        }

        if (source is SystemPromptSource.Inline inline)
        {
            return inline.Text;
        }

        var fileSource = (SystemPromptSource.FilePath)source;
        try
        {
            var path = Path.IsPathRooted(fileSource.Path)
                ? Path.GetFullPath(fileSource.Path)
                : Path.GetFullPath(fileSource.Path, startupWorkingDirectory);
            if (Directory.Exists(path))
            {
                throw new SystemPromptSourceException(
                    $"System prompt path '{path}' is a directory, not a file.");
            }

            var bytes = await readAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var offset = bytes.AsSpan().StartsWith([0xEF, 0xBB, 0xBF]) ? 3 : 0;
            return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SystemPromptSourceException)
        {
            throw;
        }
        catch (DecoderFallbackException ex)
        {
            throw new SystemPromptSourceException(
                $"System prompt file '{fileSource.Path}' is not valid UTF-8.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new SystemPromptSourceException(
                $"Could not read system prompt file '{fileSource.Path}': {ex.Message}", ex);
        }
    }
}
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~SystemPromptSourceResolverTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\SystemPromptSourceResolver.cs tests\Coda.Tui.Tests\SystemPromptSourceResolverTests.cs
git commit -m "feat(cli): add strict exact system prompt resolver" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 2: Parse and preflight interactive and serve startup

**Files:**
- Modify: `src/Coda.Tui/Ui/Mode/TuiLaunchOptions.cs`
- Modify: `src/Coda.Tui/InteractiveProgram.cs`
- Modify: `src/Coda.Tui/ServeOptions.cs`
- Modify: `src/Coda.Tui/ServeRunner.cs`
- Modify: `tests/Coda.Tui.Tests/TuiLaunchOptionsTests.cs`
- Modify: `tests/Coda.Tui.Tests/InteractiveProgramTests.cs`
- Modify: `tests/Coda.Tui.Tests/ServeRunnerTests.cs`

- [ ] **Step 1: Write failing startup-order and compatibility tests**

```csharp
[Fact]
public async Task Interactive_resolves_before_runner_and_preserves_resume_arguments()
{
    var runner = new RecordingInteractiveSessionRunner(TextWriter.Null);
    var error = new StringWriter();

    var exitCode = await InteractiveProgram.RunAsync(
        ["--system-prompt", " \r\nexact\n", "--resume", "abc", "--plain"],
        TextReader.Null,
        TextWriter.Null,
        error,
        new FixedCapabilitiesProvider(
            new TerminalCapabilities(false, true, 120, 40, true)),
        CancellationToken.None,
        runner);

    Assert.Equal(0, exitCode);
    Assert.Equal(" \r\nexact\n", runner.Options!.SystemPromptOverride);
    Assert.Equal(["--resume", "abc"], runner.Options.RemainingArgs);
    Assert.Equal(string.Empty, error.ToString());
}

[Fact]
public async Task Interactive_source_failure_never_invokes_the_runner()
{
    var runner = new RecordingInteractiveSessionRunner(TextWriter.Null);
    var error = new StringWriter();

    var exitCode = await InteractiveProgram.RunAsync(
        ["--system-prompt-file", "missing.txt"],
        TextReader.Null,
        TextWriter.Null,
        error,
        new FixedCapabilitiesProvider(
            new TerminalCapabilities(false, true, 120, 40, true)),
        CancellationToken.None,
        runner);

    Assert.Equal(2, exitCode);
    Assert.Null(runner.Options);
    Assert.Contains("missing.txt", error.ToString(), StringComparison.Ordinal);
}

[Fact]
public void Serve_prompt_flags_are_strict_while_unrelated_unknown_flags_remain_ignored()
{
    var parsed = ServeOptions.Parse(
        ["--future-flag", "future-value", "--system-prompt", "exact"]);

    Assert.Null(parsed.Error);
    Assert.Equal("exact", Assert.IsType<SystemPromptSource.Inline>(parsed.SystemPromptSource).Text);
}

[Fact]
public void Serve_rejects_duplicate_prompt_sources()
{
    var parsed = ServeOptions.Parse(
        ["--system-prompt", "one", "--system-prompt", "two"]);

    Assert.Equal(
        "Specify only one of --system-prompt or --system-prompt-file, once.",
        parsed.Error);
}
```

`RecordingInteractiveSessionRunner` already exists in `InteractiveProgramTests`; add:

```csharp
public TuiLaunchOptions? Options { get; private set; }
```

and assign `this.Options = options;` at the start of its `RunAsync` implementation.

- [ ] **Step 2: Run the parser/runner tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiLaunchOptionsTests|FullyQualifiedName~InteractiveProgramTests|FullyQualifiedName~ServeRunnerTests"`

Expected: FAIL because launch/serve options have no prompt-source properties and startup does not resolve before invoking the runner.

- [ ] **Step 3: Add non-positional option properties and preflight resolution**

Keep existing positional constructors source-compatible:

```csharp
public sealed record TuiLaunchOptions(
    TuiPreference Preference,
    bool Plain,
    IReadOnlyList<string> RemainingArgs,
    string? Error,
    bool MouseDisabled = false)
{
    public SystemPromptSource? SystemPromptSource { get; init; }
    public string? SystemPromptOverride { get; init; }
}
```

At the beginning of `TuiLaunchOptions.Parse`, call `SystemPromptSourceResolver.TryExtract`, parse display flags from the returned arguments, and set `SystemPromptSource` on the returned record. Preserve the returned argument order.

Add equivalent init-only properties to `ServeOptions`:

```csharp
public SystemPromptSource? SystemPromptSource { get; init; }
public string? SystemPromptOverride { get; init; }
public string? Error { get; init; }
```

At the start of `ServeOptions.Parse`, extract prompt flags once. If extraction fails, return `new ServeOptions { Error = error }`; otherwise run the existing permissive switch over `remainingArgs`.

Resolve before any runner/transport work:

```csharp
var startupWorkingDirectory = Directory.GetCurrentDirectory();
var options = TuiLaunchOptions.Parse(args);
if (options.Error is not null)
{
    error.WriteLine(options.Error);
    return 2;
}

try
{
    options = options with
    {
        SystemPromptOverride = await SystemPromptSourceResolver.ResolveAsync(
            options.SystemPromptSource,
            startupWorkingDirectory,
            cancellationToken).ConfigureAwait(false),
    };
}
catch (SystemPromptSourceException ex)
{
    error.WriteLine(ex.Message);
    return 2;
}
```

Use the same sequence at the top of `ServeRunner.RunAsync`, before settings, credentials, MCP, or `IServeTransport` construction. Serve returns exit code `1` and prefixes the concise error with `coda serve:`.

- [ ] **Step 4: Run the parser/runner tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~TuiLaunchOptionsTests|FullyQualifiedName~InteractiveProgramTests|FullyQualifiedName~ServeRunnerTests"`

Expected: PASS, including existing resume/fork/display and forward-compatible serve parsing cases.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\Ui\Mode\TuiLaunchOptions.cs src\Coda.Tui\InteractiveProgram.cs src\Coda.Tui\ServeOptions.cs src\Coda.Tui\ServeRunner.cs tests\Coda.Tui.Tests\TuiLaunchOptionsTests.cs tests\Coda.Tui.Tests\InteractiveProgramTests.cs tests\Coda.Tui.Tests\ServeRunnerTests.cs
git commit -m "feat(cli): parse exact prompts for interactive and serve startup" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 3: Centralize effective root prompt selection and preserve exact override semantics on valid provider wires

**Files:**
- Create: `src/Coda.Sdk/EffectiveSystemPrompt.cs`
- Modify: `src/Coda.Sdk/SessionOptions.cs`
- Modify: `src/Coda.Sdk/Turns/TurnPipelineBuilder.cs`
- Modify: `src/Coda.Sdk/CodaSession.cs`
- Modify: `src/LlmClient/AnthropicMessagesClient.cs`
- Modify: `src/LlmClient/OpenAiRequest.cs`
- Modify: `src/LlmClient/OpenAiResponsesRequest.cs`
- Create: `tests/Engine.Tests/EffectiveSystemPromptTests.cs`
- Create: `tests/Engine.Tests/ExactSystemPromptWireTests.cs`
- Modify: `tests/Engine.Tests/Sdk/Turns/TurnPipelineBuilderTests.cs`
- Modify: `tests/Engine.Tests/EffortAndContextTests.cs`
- Modify: `tests/Engine.Tests/SubagentTypeTests.cs`
- Modify: `tests/Engine.Tests/CodaSessionAuditIntegrationTests.cs`
- Modify: `tests/Engine.Tests/TestSupport/FakeSession.cs`

- [ ] **Step 1: Write failing exact-equality, fallback, scheduled-root, subagent, audit, and wire tests**

```csharp
public sealed class EffectiveSystemPromptTests
{
    [Fact]
    public void Override_excludes_every_normal_layer()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "PROJECT-MARKER");
            var options = new SessionOptions
            {
                ProviderId = LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id,
                Model = "model",
                WorkingDirectory = root,
                OutputStyle = "concise",
                SystemPromptOverride = " EXACT\r\n",
            };

            Assert.Equal(" EXACT\r\n", EffectiveSystemPrompt.Resolve(options));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Empty_override_is_present()
    {
        var options = new SessionOptions
        {
            ProviderId = "api-key",
            Model = "model",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            SystemPromptOverride = string.Empty,
        };

        Assert.Equal(string.Empty, EffectiveSystemPrompt.Resolve(options));
    }

    [Fact]
    public void Null_override_keeps_normal_prompt_construction()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "PROJECT-MARKER");
            var prompt = EffectiveSystemPrompt.Resolve(new SessionOptions
            {
                ProviderId = "api-key",
                Model = "model",
                WorkingDirectory = root,
                OutputStyle = "concise",
            });

            Assert.Contains("PROJECT-MARKER", prompt, StringComparison.Ordinal);
            Assert.Contains("software engineering", prompt, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class ExactSystemPromptWireTests
{
    [Fact]
    public void Anthropic_request_omits_an_explicit_empty_system_field()
    {
        var body = AnthropicMessagesClient.BuildBody(new ChatRequest
        {
            Model = "model",
            System = string.Empty,
            Messages = [ChatMessage.UserText("hello")],
        });

        Assert.False(body.ContainsKey("system"));
    }

    [Fact]
    public void Anthropic_request_preserves_a_non_empty_cache_controlled_system_text_block()
    {
        var body = AnthropicMessagesClient.BuildBody(new ChatRequest
        {
            Model = "model",
            System = "exact system prompt",
            Messages = [ChatMessage.UserText("hello")],
        });

        Assert.Equal("text", body["system"]!.AsArray()[0]!["type"]!.GetValue<string>());
        Assert.Equal("exact system prompt", body["system"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", body["system"]!.AsArray()[0]!["cache_control"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Chat_completions_serializes_an_explicit_empty_system_message()
    {
        var body = OpenAiRequest.Build(new ChatRequest
        {
            Model = "model",
            System = string.Empty,
            Messages = [ChatMessage.UserText("hello")],
        });

        Assert.Equal(
            string.Empty,
            body["messages"]!.AsArray()[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Responses_api_serializes_explicit_empty_instructions()
    {
        var body = OpenAiResponsesRequest.Build(new ChatRequest
        {
            Model = "model",
            System = string.Empty,
            Messages = [ChatMessage.UserText("hello")],
        });

        Assert.Equal(string.Empty, body["instructions"]!.GetValue<string>());
    }
}
```

- [ ] **Step 2: Run the focused engine tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~EffectiveSystemPromptTests|FullyQualifiedName~ExactSystemPromptWireTests|FullyQualifiedName~TurnPipelineBuilderTests|FullyQualifiedName~EffortAndContextTests|FullyQualifiedName~SubagentTypeTests|FullyQualifiedName~CodaSessionAuditIntegrationTests"`

Expected: FAIL because `SessionOptions.SystemPromptOverride` and `EffectiveSystemPrompt` do not exist, Anthropic emits an invalid empty text block, and OpenAI shapes omit explicit empty values.

- [ ] **Step 3: Implement one effective-prompt authority and use provider-valid wire checks**

Add to `SessionOptions`:

```csharp
/// <summary>
/// Complete exact root system prompt. Null uses normal Coda construction; empty and whitespace are exact values.
/// </summary>
public string? SystemPromptOverride { get; init; }
```

Create:

```csharp
using Coda.Agent;
using Coda.Agent.OutputStyles;
using LlmAuth.Providers.GitHubCopilot;

namespace Coda.Sdk;

public static class EffectiveSystemPrompt
{
    public static string Resolve(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SystemPromptOverride is not null)
        {
            return options.SystemPromptOverride;
        }

        var outputStyle = BuiltInOutputStyles.Resolve(options.OutputStyle);
        return AgentSystemPrompt.Build(
            options.WorkingDirectory,
            includeAnthropicSystemPrefix: options.ProviderId != GitHubCopilotProvider.Id,
            ProjectContext.Load(options.WorkingDirectory),
            outputStyle.SystemPromptSuffix);
    }
}
```

Replace prompt construction in `TurnPipelineBuilder.BuildAgentOptions` and `CodaSession.AnalyzeContextAsync` with `EffectiveSystemPrompt.Resolve(options)`. Keep the existing provider-prefix boolean for subagent prompt construction; subagents must continue replacing the parent root prompt with their role prompt.

In `AnthropicMessagesClient.BuildBody`, use:

```csharp
if (!string.IsNullOrEmpty(request.System))
{
    body["system"] = new JsonArray
    {
        new JsonObject
        {
            ["type"] = "text",
            ["text"] = request.System,
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
        },
    };
}
```

This intentionally maps null and an explicit empty exact override to the same valid Anthropic wire shape. The SDK
retains `SystemPromptOverride == ""` before serialization, so the empty override still suppresses all normal Coda
prompt layers. Non-empty values, including whitespace-only values, retain the cache-controlled text block unchanged.

In `OpenAiRequest.Build`, use:

```csharp
if (request.System is not null)
{
    messages.Add(new JsonObject
    {
        ["role"] = "system",
        ["content"] = request.System,
    });
}
```

In `OpenAiResponsesRequest.Build`, use:

```csharp
if (request.System is not null)
{
    body["instructions"] = request.System;
}
```

Add these focused assertions:

```csharp
// TurnPipelineBuilderTests
[Fact]
public void Root_and_scheduled_specs_use_the_same_exact_override()
{
    var options = this.Options() with
    {
        SystemPromptOverride = " EXACT\r\n",
        OutputStyle = "concise",
    };
    File.WriteAllText(Path.Combine(this.root, "CLAUDE.md"), "PROJECT-MARKER");
    var builder = this.NewBuilder();

    var rootSpec = builder.BuildSpec(options, this.Client(), CodaSettings.Empty);
    var scheduledSpec = builder.BuildScheduledSpec(
        options,
        this.Client(),
        CodaSettings.Empty,
        taskId: "scheduled-1",
        depth: 1);

    Assert.Equal(" EXACT\r\n", rootSpec.Options.SystemPrompt);
    Assert.Equal(" EXACT\r\n", scheduledSpec.Options.SystemPrompt);
    Assert.DoesNotContain("PROJECT-MARKER", rootSpec.Options.SystemPrompt);
}

// EffortAndContextTests: add `using System.Text.Json.Nodes;`,
// add Bodies to CountSeqHandler, and capture request content.
[Fact]
public async Task AnalyzeContextAsync_sends_the_exact_override_to_count_tokens()
{
    var handler = new CountSeqHandler(10, 20, 30);
    using var http = new HttpClient(handler);
    using var session = new CodaSession(
        SignedInClaude(),
        new SessionOptions
        {
            ProviderId = ClaudeAiProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = this.root,
            SystemPromptOverride = " EXACT\r\n",
        },
        httpClient: http);

    await session.AnalyzeContextAsync();

    var systemBody = JsonNode.Parse(handler.Bodies[1])!.AsObject();
    Assert.Equal(
        " EXACT\r\n",
        systemBody["system"]!.AsArray()[0]!["text"]!.GetValue<string>());
}

// CodaSessionAuditIntegrationTests
[Fact]
public async Task Audit_records_the_exact_effective_prompt()
{
    using var session = FakeSession.New(
        this.tempDir,
        systemPromptOverride: " EXACT\r\n");
    await session.RunAsync("hello");

    var turn = Assert.Single(
        await new SessionAuditStore(this.tempDir).LoadAsync(session.SessionId));
    Assert.Equal(" EXACT\r\n", turn.SystemPrompt);
}

// SubagentTypeTests
[Fact]
public async Task Subagent_role_prompt_does_not_inherit_root_exact_override()
{
    var client = new CapturingScriptedClient(
        [AssistantStreamEvent.Finished("end_turn")]);
    var host = new SubagentHost(
        client,
        new ToolRegistry([]),
        new AllowAllPermissionPrompt(),
        Options() with { SystemPrompt = "ROOT-EXACT" },
        new TaskManager(sessionId: "prompt-isolation", logRoot: null),
        includeAnthropicSystemPrefix: false);

    await host.RunSubagentAsync(
        "general-purpose",
        "hello",
        new NullSink(),
        new SteeringInbox(),
        "task-1",
        1,
        CancellationToken.None);

    Assert.DoesNotContain("ROOT-EXACT", client.LastSystem, StringComparison.Ordinal);
    Assert.Contains("# Environment", client.LastSystem, StringComparison.Ordinal);
}
```

Change `CountSeqHandler.SendAsync` to append `await request.Content!.ReadAsStringAsync(cancellationToken)` to `Bodies` before returning its canned count. Extend `FakeSession.New` with optional `string? systemPromptOverride = null` and assign it to `SessionOptions.SystemPromptOverride`.

- [ ] **Step 4: Run the focused engine tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~EffectiveSystemPromptTests|FullyQualifiedName~ExactSystemPromptWireTests|FullyQualifiedName~TurnPipelineBuilderTests|FullyQualifiedName~EffortAndContextTests|FullyQualifiedName~SubagentTypeTests|FullyQualifiedName~CodaSessionAuditIntegrationTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Sdk\EffectiveSystemPrompt.cs src\Coda.Sdk\SessionOptions.cs src\Coda.Sdk\Turns\TurnPipelineBuilder.cs src\Coda.Sdk\CodaSession.cs src\LlmClient\AnthropicMessagesClient.cs src\LlmClient\OpenAiRequest.cs src\LlmClient\OpenAiResponsesRequest.cs tests\Engine.Tests\EffectiveSystemPromptTests.cs tests\Engine.Tests\ExactSystemPromptWireTests.cs tests\Engine.Tests\Sdk\Turns\TurnPipelineBuilderTests.cs tests\Engine.Tests\EffortAndContextTests.cs tests\Engine.Tests\SubagentTypeTests.cs tests\Engine.Tests\CodaSessionAuditIntegrationTests.cs tests\Engine.Tests\TestSupport\FakeSession.cs
git commit -m "feat(sdk): centralize exact effective root prompts" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 4: Propagate resolved text through TUI, serve, context, setup, and warnings

**Files:**
- Create: `src/Coda.Tui/SystemPromptCompatibilityWarning.cs`
- Modify: `src/Coda.Tui/Repl/SessionState.cs`
- Modify: `src/Coda.Tui/InteractiveProgram.cs`
- Modify: `src/Coda.Tui/Agent/AgentRunner.cs`
- Modify: `src/Coda.Tui/Commands/ContextCommand.cs`
- Modify: `src/Coda.Tui/ServeRunner.cs`
- Modify: `src/Coda.Tui/Setup/SetupWizard.cs`
- Modify: `tests/Coda.Tui.Tests/AgentRunnerTests.cs`
- Modify: `tests/Coda.Tui.Tests/ContextExportCommandTests.cs`
- Modify: `tests/Coda.Tui.Tests/ServeRunnerTests.cs`
- Modify: `tests/Coda.Tui.Tests/SetupAndModelTests.cs`
- Modify: `tests/Coda.Tui.Tests/InteractiveProgramTests.cs`

- [ ] **Step 1: Write failing propagation and warning tests**

```csharp
[Fact]
public async Task AgentRunner_builds_options_with_the_current_exact_override()
{
    var events = new RecordingUiEvents();
    var context = this.BuildContext(events, out _);
    SessionOptions? captured = null;
    using var runner = new AgentRunner(
        extraToolsProvider: null,
        (context, options, current) =>
        {
            captured = options;
            return new CodaSession(
                context.Credentials,
                options,
                httpClient: this.http,
                history: context.Session.History,
                sessionId: context.Session.SessionId,
                llmClientFactory: new StubClientFactory(new StubClient()),
                agentLoopFactory: new SingleLoopFactory(new ScriptedLoop()),
                currentOptionsProvider: current);
        });
    context.Session.SystemPromptOverride = "exact";

    await runner.InitializeSessionAsync(context, CancellationToken.None);

    Assert.Equal("exact", captured!.SystemPromptOverride);
}

[Fact]
public void Serve_mapping_preserves_an_empty_override()
{
    var parsed = new ServeOptions
    {
        ProviderId = "api-key",
        Model = "model",
        WorkingDirectory = @"C:\repo",
        SystemPromptOverride = string.Empty,
    };

    Assert.Equal(
        string.Empty,
        ServeRunner.BuildSessionOptions(parsed).SystemPromptOverride);
}

[Fact]
public void Claude_ai_warning_is_non_blocking_and_does_not_rewrite_text()
{
    const string exact = "exact without compatibility prefix";
    var session = new SessionState(
        LlmAuth.Providers.ClaudeAi.ClaudeAiProvider.Id)
    {
        SystemPromptOverride = exact,
    };

    var warning = SystemPromptCompatibilityWarning.For(
        session.ActiveProviderId,
        session.SystemPromptOverride);

    Assert.Contains("may require", warning, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(exact, session.SystemPromptOverride);
}

[Fact]
public void Context_analysis_options_carry_the_live_exact_override()
{
    var built = TestAppBuilder.BuildApp();
    built.Context.Session.SystemPromptOverride = "exact";

    var options = ContextCommand.BuildSessionOptions(built.Context);

    Assert.Equal("exact", options.SystemPromptOverride);
}

public sealed class SetupWizardSystemPromptTests
{
    [Fact]
    public void Verification_uses_an_explicit_empty_override()
    {
        var session = new SessionState("api-key")
        {
            SystemPromptOverride = string.Empty,
        };

        Assert.Equal(
            string.Empty,
            SetupWizard.ResolveVerificationSystemPrompt(session));
    }
}
```

- [ ] **Step 2: Run the focused TUI tests and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~AgentRunnerTests|FullyQualifiedName~ContextExportCommandTests|FullyQualifiedName~ServeRunnerTests|FullyQualifiedName~SetupWizardSystemPromptTests|FullyQualifiedName~InteractiveProgramTests"`

Expected: FAIL because the session/TUI mappings and compatibility-warning helper do not exist.

- [ ] **Step 3: Add exact override state and propagation**

Add to `SessionState`:

```csharp
/// <summary>The CLI override supplied when this process started; authoritative over resumed metadata.</summary>
public string? StartupSystemPromptOverride { get; init; }

/// <summary>The exact override currently applied to root turns; null uses normal construction.</summary>
public string? SystemPromptOverride { get; set; }
```

Initialize both from `TuiLaunchOptions.SystemPromptOverride` before startup resume seeding. Add `SystemPromptOverride = context.Session.SystemPromptOverride` to `AgentRunner.BuildOptions`, `ContextCommand.BuildSessionOptions`, and `ServeRunner.BuildSessionOptions`.

Extract these pure seams:

```csharp
// Add to ContextCommand.
internal static SessionOptions BuildSessionOptions(CommandContext context) =>
    new()
    {
        ProviderId = context.Session.ActiveProviderId,
        Model = context.Session.Model,
        WorkingDirectory = context.Session.WorkingDirectory,
        OutputStyle = context.Session.OutputStyle,
        ExtraTools = context.ExtraTools,
        SystemPromptOverride = context.Session.SystemPromptOverride,
    };

// Add to SetupWizard.
internal static string ResolveVerificationSystemPrompt(SessionState session) =>
    session.SystemPromptOverride
    ?? AgentSystemPrompt.Build(
        session.WorkingDirectory,
        includeAnthropicSystemPrefix: session.ActiveProviderId != GitHubCopilotProvider.Id,
        ProjectContext.Load(session.WorkingDirectory),
        BuiltInOutputStyles.Resolve(session.OutputStyle).SystemPromptSuffix);
```

Create:

```csharp
using LlmAuth.Providers.ClaudeAi;

namespace Coda.Tui;

internal static class SystemPromptCompatibilityWarning
{
    public static string? For(string providerId, string? systemPromptOverride) =>
        systemPromptOverride is not null &&
        string.Equals(providerId, ClaudeAiProvider.Id, StringComparison.Ordinal)
            ? "Claude.ai OAuth may require its compatibility system prefix; the exact supplied prompt will be sent unchanged."
            : null;
}
```

After resume/setup has selected the effective provider, publish this warning through the existing diagnostic UI path. Do not append a prefix and do not block startup.

- [ ] **Step 4: Run the focused TUI tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~AgentRunnerTests|FullyQualifiedName~ContextExportCommandTests|FullyQualifiedName~ServeRunnerTests|FullyQualifiedName~SetupWizardSystemPromptTests|FullyQualifiedName~InteractiveProgramTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\SystemPromptCompatibilityWarning.cs src\Coda.Tui\Repl\SessionState.cs src\Coda.Tui\InteractiveProgram.cs src\Coda.Tui\Agent\AgentRunner.cs src\Coda.Tui\Commands\ContextCommand.cs src\Coda.Tui\ServeRunner.cs src\Coda.Tui\Setup\SetupWizard.cs tests\Coda.Tui.Tests\AgentRunnerTests.cs tests\Coda.Tui.Tests\ContextExportCommandTests.cs tests\Coda.Tui.Tests\ServeRunnerTests.cs tests\Coda.Tui.Tests\SetupAndModelTests.cs tests\Coda.Tui.Tests\InteractiveProgramTests.cs
git commit -m "feat(tui): propagate exact prompts through interactive and serve sessions" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 5: Persist optional live-session override metadata compatibly

**Files:**
- Create: `src/Coda.Sdk/SessionMetadata.cs`
- Modify: `src/Coda.Sdk/SessionTranscriptStore.cs`
- Modify: `src/Coda.Sdk/CodaSession.cs`
- Modify: `tests/Engine.Tests/SessionTranscriptTests.cs`
- Modify: `tests/Engine.Tests/CodaSessionAuditIntegrationTests.cs`

- [ ] **Step 1: Write failing metadata round-trip and legacy-overload tests**

```csharp
[Fact]
public async Task Metadata_round_trips_without_freezing_the_default_prompt()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    try
    {
        var store = new SessionTranscriptStore(root);
        await store.SaveAsync(
            "abc123",
            [ChatMessage.UserText("hello")],
            new SessionMetadata { SystemPromptOverride = "exact\n" });

        var loaded = await store.LoadSessionAsync("abc123");
        Assert.Equal("exact\n", loaded!.Metadata.SystemPromptOverride);

        await store.SaveAsync(
            "normal",
            [ChatMessage.UserText("hello")],
            SessionMetadata.Empty);
        var json = await File.ReadAllTextAsync(
            Path.Combine(root, ".coda", "sessions", "normal.json"));
        Assert.DoesNotContain("systemPromptOverride", json, StringComparison.Ordinal);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

[Fact]
public async Task Legacy_save_preserves_an_existing_empty_override()
{
    var store = NewStore(out var root);
    try
    {
        await store.SaveAsync(
            "abc123",
            [ChatMessage.UserText("one")],
            new SessionMetadata { SystemPromptOverride = string.Empty });

        await store.SaveAsync("abc123", [ChatMessage.UserText("two")]);

        var loaded = await store.LoadSessionAsync("abc123");
        Assert.Equal(string.Empty, loaded!.Metadata.SystemPromptOverride);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

[Fact]
public async Task Legacy_and_invalid_optional_metadata_load_without_freezing_a_prompt()
{
    var store = NewStore(out var root);
    try
    {
        var sessions = Path.Combine(root, ".coda", "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "legacy.json"),
            """{"id":"legacy","createdUtc":"2026-07-22T00:00:00Z","unknown":true,"messages":[{"role":"user","blocks":[{"type":"text","text":"hello"}]}]}""");
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "invalid.json"),
            """{"id":"invalid","createdUtc":"2026-07-22T00:00:00Z","systemPromptOverride":42,"messages":[{"role":"user","blocks":[{"type":"text","text":"hello"}]}]}""");

        Assert.Null((await store.LoadSessionAsync("legacy"))!.Metadata.SystemPromptOverride);
        Assert.Null((await store.LoadSessionAsync("invalid"))!.Metadata.SystemPromptOverride);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

[Fact]
public async Task Created_time_survives_metadata_and_legacy_saves()
{
    var store = NewStore(out var root);
    try
    {
        await store.SaveAsync(
            "abc123",
            [ChatMessage.UserText("one")],
            new SessionMetadata { SystemPromptOverride = "exact" });
        var path = Path.Combine(root, ".coda", "sessions", "abc123.json");
        var first = JsonNode.Parse(await File.ReadAllTextAsync(path))!["createdUtc"]!.GetValue<string>();

        await store.SaveAsync("abc123", [ChatMessage.UserText("two")]);
        var second = JsonNode.Parse(await File.ReadAllTextAsync(path))!["createdUtc"]!.GetValue<string>();

        Assert.Equal(first, second);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

[Fact]
public async Task Audit_system_prompt_is_never_used_as_resume_metadata()
{
    var store = NewStore(out var root);
    try
    {
        await store.SaveAsync(
            "abc123",
            [ChatMessage.UserText("hello")],
            SessionMetadata.Empty);
        await new SessionAuditStore(root).AppendTurnAsync(
            "abc123",
            new SessionAuditTurn
            {
                TurnIndex = 0,
                TsUtc = DateTime.UtcNow,
                Provider = "fake",
                Model = "model",
                InputTokens = 1,
                OutputTokens = 1,
                SystemPrompt = "AUDITED-EFFECTIVE",
            });

        Assert.Null((await store.LoadSessionAsync("abc123"))!.Metadata.SystemPromptOverride);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}
```

Use the existing transcript-test temporary-directory helper for `NewStore`; if none is shared in that file, define it as:

```csharp
private static SessionTranscriptStore NewStore(out string root)
{
    root = Directory.CreateTempSubdirectory().FullName;
    return new SessionTranscriptStore(root);
}
```

- [ ] **Step 2: Run transcript/audit tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SessionTranscriptTests|FullyQualifiedName~CodaSessionAuditIntegrationTests"`

Expected: FAIL because typed metadata save/load APIs are absent.

- [ ] **Step 3: Add typed metadata and compatible store overloads**

```csharp
using LlmClient;

namespace Coda.Sdk;

public sealed record SessionMetadata
{
    public static SessionMetadata Empty { get; } = new();
    public string? SystemPromptOverride { get; init; }
}

public sealed record StoredSession(
    IReadOnlyList<ChatMessage> Messages,
    SessionMetadata Metadata);
```

Retain the old methods and add:

```csharp
public Task SaveAsync(
    string sessionId,
    IReadOnlyList<ChatMessage> messages,
    SessionMetadata metadata,
    CancellationToken ct = default);

public Task<StoredSession?> LoadSessionAsync(
    string sessionId,
    CancellationToken ct = default);
```

The metadata-aware save writes:

```csharp
var root = new JsonObject
{
    ["id"] = sessionId,
    ["createdUtc"] = createdUtc.ToString("O"),
    ["messages"] = ChatMessageJson.SerializeMessages(messages),
};
if (metadata.SystemPromptOverride is not null)
{
    root["systemPromptOverride"] = metadata.SystemPromptOverride;
}
```

The legacy save resolves/caches existing metadata and delegates to the new overload, so incremental persistence cannot erase it. `LoadSessionAsync` treats a missing, non-string, or unknown optional field as `null` while still loading valid messages; `LoadAsync` remains a wrapper returning `stored?.Messages`.

Change `CodaSession.PersistTranscriptAsync` to pass:

```csharp
new SessionMetadata
{
    SystemPromptOverride = this.Options.SystemPromptOverride,
}
```

- [ ] **Step 4: Run transcript/audit tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SessionTranscriptTests|FullyQualifiedName~CodaSessionAuditIntegrationTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Sdk\SessionMetadata.cs src\Coda.Sdk\SessionTranscriptStore.cs src\Coda.Sdk\CodaSession.cs tests\Engine.Tests\SessionTranscriptTests.cs tests\Engine.Tests\CodaSessionAuditIntegrationTests.cs
git commit -m "feat(sdk): persist optional system prompt override metadata" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 6: Restore resume precedence before initialization and scheduled execution

**Files:**
- Modify: `src/Coda.Sdk/CodaSession.cs`
- Modify: `src/Coda.Tui/SessionCli.cs`
- Modify: `src/Coda.Tui/InteractiveProgram.cs`
- Modify: `src/Coda.Tui/Commands/ResumeCommand.cs`
- Modify: `src/Coda.Tui/HeadlessRunner.cs`
- Modify: `src/Coda.Sdk/Serve/ServeHost.cs`
- Modify: `tests/Coda.Tui.Tests/SessionCliTests.cs`
- Modify: `tests/Coda.Tui.Tests/ResumeRewindCommandTests.cs`
- Modify: `tests/Coda.Tui.Tests/CodaSessionResumeTests.cs`
- Modify: `tests/Engine.Tests/Serve/ServeHostResumeTests.cs`
- Modify: `tests/Engine.Tests/Serve/ServeHostTests.cs`
- Modify: `tests/Engine.Tests/Scheduling/ScheduledAgentHostTests.cs`

- [ ] **Step 1: Write failing precedence and serve-initialization tests**

```csharp
[Theory]
[InlineData(null, "persisted", "persisted")]
[InlineData("cli", "persisted", "cli")]
[InlineData("", "persisted", "")]
[InlineData(null, null, null)]
public void Resume_applies_startup_then_persisted_precedence(
    string? startup,
    string? persisted,
    string? expected)
{
    var root = Directory.CreateTempSubdirectory().FullName;
    try
    {
        using var session = NewSession(root, startup);
        session.Resume(
            "target",
            [ChatMessage.UserText("history")],
            new SessionMetadata { SystemPromptOverride = persisted });

        Assert.Equal(expected, session.Options.SystemPromptOverride);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

private static CodaSession NewSession(
    string workingDirectory,
    string? startupOverride)
{
    var credentials = new CredentialManager(
        new InMemoryTokenStore(),
        [new ApiKeyProvider()]);
    return new CodaSession(
        credentials,
        new SessionOptions
        {
            ProviderId = ApiKeyProvider.Id,
            Model = "claude-sonnet-4-6",
            WorkingDirectory = workingDirectory,
            SystemPromptOverride = startupOverride,
        });
}
```

In `ServeHostResumeTests`, extend the host constructor with an internal initialization delegate seam:

```csharp
Func<CodaSession, CancellationToken, Task>? initializeSession = null
```

Production defaults it to `(session, ct) => session.InitializeAsync(ct)`. The resume test seeds `systemPromptOverride`, captures the created `CodaSession`, and supplies:

```csharp
var resumeCompletedBeforeInitialize = false;
string? observedOverride = null;
Task Initialize(CodaSession session, CancellationToken ct)
{
    resumeCompletedBeforeInitialize = session.SessionId == "resume-id";
    observedOverride = session.Options.SystemPromptOverride;
    return Task.CompletedTask;
}
```

After sending the existing JSON-RPC `initialize { sessionId: "resume-id" }` request, assert:

```csharp
Assert.True(resumeCompletedBeforeInitialize);
Assert.Equal("persisted", observedOverride);
```

Replace the old stdio assertion that initialization begins immediately after `connection.Start`; the new assertion is that `initialize`, or the first authenticated prompt when no initialize request is sent, drives initialization exactly once.

- [ ] **Step 2: Run resume/serve tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ServeHostResumeTests|FullyQualifiedName~ServeHostTests|FullyQualifiedName~ScheduledAgentHostTests"`

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~SessionCliTests|FullyQualifiedName~ResumeRewindCommandTests|FullyQualifiedName~CodaSessionResumeTests"`

Expected: FAIL because resume loads messages only and stdio initialization can begin before persisted metadata is applied.

- [ ] **Step 3: Thread typed metadata and enforce precedence**

Change the resume target:

```csharp
public sealed record ResumeTarget(
    string Id,
    IReadOnlyList<ChatMessage> Messages,
    SessionMetadata Metadata);
```

`SessionCli.ResolveAsync`, headless startup, interactive startup, and `/resume` use `LoadSessionAsync`. In TUI state, apply:

```csharp
context.Session.SystemPromptOverride =
    context.Session.StartupSystemPromptOverride
    ?? target.Metadata.SystemPromptOverride;
```

Capture startup authority in `CodaSession`:

```csharp
private readonly string? startupSystemPromptOverride;

public void Resume(
    string sessionId,
    IReadOnlyList<ChatMessage> messages,
    SessionMetadata metadata)
{
    ArgumentException.ThrowIfNullOrEmpty(sessionId);
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentNullException.ThrowIfNull(metadata);

    this.SessionId = sessionId;
    this.history.Clear();
    this.history.AddRange(messages);
    this.Options = this.Options with
    {
        SystemPromptOverride =
            this.startupSystemPromptOverride
            ?? metadata.SystemPromptOverride,
    };
}
```

Retain the old two-argument `Resume` as a wrapper passing `SessionMetadata.Empty`.

In `ServeHost`, remove the eager stdio `EnsureInitializationDriven` call after `connection.Start`. The initialize handler loads `StoredSession`, calls the metadata overload, then drives initialization. The prompt handler continues to drive initialization when a client legitimately sends a prompt without initialize:

```csharp
var stored = await transcripts.LoadSessionAsync(resumeId, ct).ConfigureAwait(false);
if (stored is null)
{
    throw new JsonRpcRequestException(-32002, "session not found");
}

sess.Resume(resumeId, stored.Messages, stored.Metadata);
this.EnsureInitializationDriven(hostCt);
await this.initializationGate.Task.WaitAsync(ct).ConfigureAwait(false);
```

Verify scheduled specs created after resume see the precedence-resolved override and subagents still use their own prompts.

- [ ] **Step 4: Run resume/serve tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~ServeHostResumeTests|FullyQualifiedName~ServeHostTests|FullyQualifiedName~ScheduledAgentHostTests"`

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~SessionCliTests|FullyQualifiedName~ResumeRewindCommandTests|FullyQualifiedName~CodaSessionResumeTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Sdk\CodaSession.cs src\Coda.Tui\SessionCli.cs src\Coda.Tui\InteractiveProgram.cs src\Coda.Tui\Commands\ResumeCommand.cs src\Coda.Tui\HeadlessRunner.cs src\Coda.Sdk\Serve\ServeHost.cs tests\Coda.Tui.Tests\SessionCliTests.cs tests\Coda.Tui.Tests\ResumeRewindCommandTests.cs tests\Coda.Tui.Tests\CodaSessionResumeTests.cs tests\Engine.Tests\Serve\ServeHostResumeTests.cs tests\Engine.Tests\Serve\ServeHostTests.cs tests\Engine.Tests\Scheduling\ScheduledAgentHostTests.cs
git commit -m "feat(sessions): restore exact prompt metadata on resume" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 7: Carry override metadata through every fork path

**Files:**
- Modify: `src/Coda.Sdk/SessionForking.cs`
- Modify: `src/Coda.Tui/InteractiveProgram.cs`
- Modify: `src/Coda.Tui/Commands/ForkCommand.cs`
- Modify: `src/Coda.Tui/HeadlessRunner.cs`
- Modify: `tests/Engine.Tests/SessionForkingTests.cs`
- Modify: `tests/Coda.Tui.Tests/ForkCommandTests.cs`
- Modify: `tests/Engine.Tests/HeadlessOptionsResumeTests.cs`

- [ ] **Step 1: Write failing fork metadata tests**

```csharp
[Fact]
public async Task Fork_copies_override_to_a_new_identity()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    try
    {
        var id = await SessionForking.ForkAsync(
            root,
            "source",
            [ChatMessage.UserText("history")],
            new SessionMetadata { SystemPromptOverride = "exact" });

        var fork = await new SessionTranscriptStore(root).LoadSessionAsync(id);
        Assert.Equal("exact", fork!.Metadata.SystemPromptOverride);
        Assert.NotEqual("source", id);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

[Fact]
public async Task Slash_fork_uses_the_current_precedence_resolved_override()
{
    var root = Directory.CreateTempSubdirectory().FullName;
    try
    {
        var built = TestAppBuilder.BuildApp(workingDirectory: root);
        var context = built.Context;
        context.Session.SystemPromptOverride = string.Empty;
        context.Session.History.Add(ChatMessage.UserText("history"));

        await new ForkCommand().ExecuteAsync(
            context,
            [],
            CancellationToken.None);

        var fork = await new SessionTranscriptStore(root)
            .LoadSessionAsync(context.Session.SessionId!);
        Assert.Equal(string.Empty, fork!.Metadata.SystemPromptOverride);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 2: Run fork tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SessionForkingTests|FullyQualifiedName~HeadlessOptionsResumeTests"`

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ForkCommandTests"`

Expected: FAIL because fork persistence copies messages/audit only.

- [ ] **Step 3: Add a metadata-aware overload and update all callers**

```csharp
public static Task<string> ForkAsync(
    string workingDirectory,
    string? sourceId,
    IReadOnlyList<ChatMessage> messages,
    SessionMetadata metadata,
    CancellationToken ct = default);
```

The overload saves the new transcript with `metadata` and copies the audit sidecar. Keep the old overload; it loads source metadata when `sourceId` exists, otherwise uses `SessionMetadata.Empty`.

Interactive startup fork, slash fork, and headless fork pass:

```csharp
new SessionMetadata
{
    SystemPromptOverride = context.Session.SystemPromptOverride,
}
```

For headless source forks, pass the `ResumeTarget.Metadata` returned by `SessionCli`; do not add prompt flags to `coda run`.

- [ ] **Step 4: Run fork tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SessionForkingTests|FullyQualifiedName~HeadlessOptionsResumeTests"`

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ForkCommandTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Sdk\SessionForking.cs src\Coda.Tui\InteractiveProgram.cs src\Coda.Tui\Commands\ForkCommand.cs src\Coda.Tui\HeadlessRunner.cs tests\Engine.Tests\SessionForkingTests.cs tests\Coda.Tui.Tests\ForkCommandTests.cs tests\Engine.Tests\HeadlessOptionsResumeTests.cs
git commit -m "feat(sessions): carry exact prompt metadata across forks" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 8: Round-trip override metadata in session bundles without changing schema

**Files:**
- Modify: `src/Coda.Sdk/SessionBundle.cs`
- Modify: `src/Coda.Sdk/SessionBundleService.cs`
- Modify: `tests/Engine.Tests/SessionBundleServiceTests.cs`
- Modify: `tests/Coda.Tui.Tests/ContextExportCommandTests.cs`

- [ ] **Step 1: Write failing export/import separation tests**

```csharp
[Fact]
public async Task Export_import_keeps_override_separate_from_effective_audit_prompt()
{
    var transcript = new SessionTranscriptStore(this.tempDir);
    await transcript.SaveAsync(
        "s1",
        [ChatMessage.UserText("hello")],
        new SessionMetadata { SystemPromptOverride = "OVERRIDE" });
    await new SessionAuditStore(this.tempDir).AppendTurnAsync(
        "s1",
        new SessionAuditTurn
        {
            TurnIndex = 0,
            TsUtc = FixedExport,
            Provider = "fake",
            Model = "model",
            InputTokens = 1,
            OutputTokens = 1,
            SystemPrompt = "EFFECTIVE",
        });
    var service = new SessionBundleService(this.tempDir, "test");
    var bundle = await service.ExportAsync("s1", FixedExport);
    Assert.NotNull(bundle);
    Assert.Equal("coda.session/1", bundle.Schema);
    Assert.Equal("OVERRIDE", bundle.SystemPromptOverride);
    Assert.Equal("EFFECTIVE", bundle.SystemPrompt);

    var path = Path.Combine(this.tempDir, "s1.coda-session.json");
    await service.WriteAsync(bundle, path, pretty: false);
    var destinationRoot = Directory.CreateTempSubdirectory().FullName;
    try
    {
        var importedId = await new SessionBundleService(destinationRoot, "test")
            .ImportAsync(path);
        var imported = await new SessionTranscriptStore(destinationRoot)
            .LoadSessionAsync(importedId);
        Assert.Equal("OVERRIDE", imported!.Metadata.SystemPromptOverride);
    }
    finally
    {
        Directory.Delete(destinationRoot, recursive: true);
    }
}

[Fact]
public async Task Empty_override_is_emitted_but_absent_override_is_omitted()
{
    var store = new SessionTranscriptStore(this.tempDir);
    await store.SaveAsync(
        "empty",
        [ChatMessage.UserText("empty")],
        new SessionMetadata { SystemPromptOverride = string.Empty });
    await store.SaveAsync(
        "absent",
        [ChatMessage.UserText("absent")],
        SessionMetadata.Empty);
    var service = new SessionBundleService(this.tempDir, "test");

    var withEmpty = await service.ExportAsync("empty", FixedExport);
    var emptyPath = Path.Combine(this.tempDir, "empty.json");
    await service.WriteAsync(withEmpty!, emptyPath, pretty: false);
    var emptyJson = await File.ReadAllTextAsync(emptyPath);
    Assert.Contains("\"systemPromptOverride\":\"\"", emptyJson, StringComparison.Ordinal);

    var absent = await service.ExportAsync("absent", FixedExport);
    var absentPath = Path.Combine(this.tempDir, "absent.json");
    await service.WriteAsync(absent!, absentPath, pretty: false);
    var absentJson = await File.ReadAllTextAsync(absentPath);
    Assert.DoesNotContain("systemPromptOverride", absentJson, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run bundle/command tests and verify RED**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SessionBundleServiceTests"`

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ContextExportCommandTests"`

Expected: FAIL because bundles do not carry separate override metadata.

- [ ] **Step 3: Add the optional bundle field and import it into transcript metadata**

Add:

```csharp
public string? SystemPromptOverride { get; init; }
```

`ExportAsync` loads `StoredSession`, uses `stored.Messages` for turns, and assigns `stored.Metadata.SystemPromptOverride`. Serialization adds the field only when non-null:

```csharp
if (bundle.SystemPromptOverride is not null)
{
    root["systemPromptOverride"] = bundle.SystemPromptOverride;
}
```

Deserialization reads the optional string independently of `systemPrompt`. Import saves:

```csharp
await transcriptStore.SaveAsync(
    targetId,
    messages,
    new SessionMetadata
    {
        SystemPromptOverride = bundle.SystemPromptOverride,
    },
    ct).ConfigureAwait(false);
```

Keep `Schema == "coda.session/1"`. Verify absent field, empty field, collision-minted IDs, unknown additive fields, and old bundles.

- [ ] **Step 4: Run bundle/command tests and verify GREEN**

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~SessionBundleServiceTests"`

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ContextExportCommandTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Sdk\SessionBundle.cs src\Coda.Sdk\SessionBundleService.cs tests\Engine.Tests\SessionBundleServiceTests.cs tests\Coda.Tui.Tests\ContextExportCommandTests.cs
git commit -m "feat(sessions): round-trip exact prompt metadata in bundles" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 9: CLI help, README, API, and serve protocol documentation

**Files:**
- Modify: `src/Coda.Tui/ImmediateCli.cs`
- Modify: `README.md`
- Modify: `docs/API.md`
- Modify: `docs/serve-protocol.md`
- Modify: `tests/Coda.Tui.Tests/ImmediateCliTests.cs`

- [ ] **Step 1: Write failing help assertions**

```csharp
[Fact]
public void Help_documents_both_exact_prompt_sources_and_scope()
{
    var writer = new StringWriter();

    Assert.Equal(0, ImmediateCli.TryHandle(["--help"], writer));

    var help = writer.ToString();
    Assert.Contains("coda --system-prompt <text>", help, StringComparison.Ordinal);
    Assert.Contains("coda --system-prompt-file <path>", help, StringComparison.Ordinal);
    Assert.Contains("coda serve --system-prompt <text>", help, StringComparison.Ordinal);
    Assert.Contains("exact root system prompt", help, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("coda run --system-prompt", help, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the help test and verify RED**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ImmediateCliTests"`

Expected: FAIL because help does not list the new flags.

- [ ] **Step 3: Update help and documentation with the approved semantics**

Add concise help lines:

```csharp
writer.WriteLine("  coda --system-prompt <text>       Use text as the exact root system prompt.");
writer.WriteLine("  coda --system-prompt-file <path>  Read the exact root system prompt once from a UTF-8 file.");
writer.WriteLine("  coda serve accepts the same two mutually exclusive startup options.");
```

Document all of the following in `README.md`, `docs/API.md`, and `docs/serve-protocol.md`:

- interactive and serve syntax, mutual exclusion, missing-value failure, and no `coda run` flag;
- startup-directory relative path behavior, strict UTF-8/BOM handling, exact whitespace/line-ending/trailing-newline preservation, and one-time read;
- complete root replacement with no built-in/project/output-style/provider prefix;
- non-blocking Claude.ai OAuth warning;
- scheduled-root and `/context` parity, with subagent role-prompt isolation;
- startup-over-persisted resume precedence;
- separate live metadata versus audited effective `systemPrompt`;
- fork/export/import behavior and unchanged `coda.session/1`;
- serve initialization is driven after resume metadata is applied.

- [ ] **Step 4: Run help and subsystem regression tests and verify GREEN**

Run: `dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~ImmediateCliTests|FullyQualifiedName~SystemPromptSourceResolverTests|FullyQualifiedName~InteractiveProgramTests|FullyQualifiedName~ServeRunnerTests|FullyQualifiedName~SessionCliTests|FullyQualifiedName~ForkCommandTests"`

Run: `dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~EffectiveSystemPromptTests|FullyQualifiedName~ExactSystemPromptWireTests|FullyQualifiedName~SessionTranscriptTests|FullyQualifiedName~SessionForkingTests|FullyQualifiedName~SessionBundleServiceTests|FullyQualifiedName~ServeHostResumeTests|FullyQualifiedName~ScheduledAgentHostTests"`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src\Coda.Tui\ImmediateCli.cs README.md docs\API.md docs\serve-protocol.md tests\Coda.Tui.Tests\ImmediateCliTests.cs
git commit -m "docs(cli): document exact startup system prompts" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

## Subsystem completion checks

Run from `C:\Users\yurio\Documents\github\coda-cli` after all tasks:

```powershell
dotnet test tests\Engine.Tests\Engine.Tests.csproj --filter "FullyQualifiedName~EffectiveSystemPromptTests|FullyQualifiedName~ExactSystemPromptWireTests|FullyQualifiedName~TurnPipelineBuilderTests|FullyQualifiedName~EffortAndContextTests|FullyQualifiedName~SubagentTypeTests|FullyQualifiedName~CodaSessionAuditIntegrationTests|FullyQualifiedName~SessionTranscriptTests|FullyQualifiedName~SessionForkingTests|FullyQualifiedName~SessionBundleServiceTests|FullyQualifiedName~ServeHostResumeTests|FullyQualifiedName~ScheduledAgentHostTests"
dotnet test tests\Coda.Tui.Tests\Coda.Tui.Tests.csproj --filter "FullyQualifiedName~SystemPromptSourceResolverTests|FullyQualifiedName~TuiLaunchOptionsTests|FullyQualifiedName~InteractiveProgramTests|FullyQualifiedName~ServeRunnerTests|FullyQualifiedName~AgentRunnerTests|FullyQualifiedName~ContextExportCommandTests|FullyQualifiedName~SetupWizardSystemPromptTests|FullyQualifiedName~SessionCliTests|FullyQualifiedName~ResumeRewindCommandTests|FullyQualifiedName~CodaSessionResumeTests|FullyQualifiedName~ForkCommandTests|FullyQualifiedName~ImmediateCliTests"
```

Expected: both targeted projects pass with no normal-prompt, resume/fork, scheduled-root, context-analysis, or serve compatibility regression.
