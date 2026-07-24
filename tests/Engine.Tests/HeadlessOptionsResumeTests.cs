using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Sdk;
using Engine.Tests.TestSupport;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using static Engine.Tests.TestSupport.CredentialFixtures;

namespace Engine.Tests;

public sealed class HeadlessOptionsResumeTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("headless-options-resume-").FullName;

    [Fact]
    public void Parses_continue_flag()
    {
        Assert.True(HeadlessOptions.TryParse(["-p", "go", "--continue"], out var o, out _));
        Assert.True(o.Continue);
        Assert.Null(o.ResumeSessionId);
    }

    [Fact]
    public void Parses_resume_with_id()
    {
        Assert.True(HeadlessOptions.TryParse(["-p", "go", "--resume", "abc123"], out var o, out _));
        Assert.Equal("abc123", o.ResumeSessionId);
        Assert.False(o.Continue);
    }

    [Fact]
    public void Resume_without_value_is_an_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "go", "--resume"], out _, out var err));
        Assert.Contains("--resume", err);
    }

    [Fact]
    public void Continue_and_resume_together_is_an_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "go", "--continue", "--resume", "x"], out _, out var err));
        Assert.NotNull(err);
    }

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

    [Theory]
    [InlineData("exact")]
    [InlineData("")]
    public void Fork_uses_source_metadata_as_the_initial_system_prompt_override(string systemPromptOverride)
    {
        var options = Parse("-p", "go", "--fork", "source-aaaa");

        var effectiveOverride = ResolveInitialSystemPromptOverride(
            options,
            new SessionMetadata { SystemPromptOverride = systemPromptOverride });

        Assert.Equal(systemPromptOverride, effectiveOverride);
    }

    [Fact]
    public void Ordinary_headless_run_starts_without_a_system_prompt_override()
    {
        var effectiveOverride = ResolveInitialSystemPromptOverride(Parse("-p", "go"), SessionMetadata.Empty);

        Assert.Null(effectiveOverride);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("")]
    public void Resume_defers_to_persisted_metadata_after_default_construction(string systemPromptOverride)
    {
        var options = Parse("-p", "go", "--resume", "source-aaaa");
        var metadata = new SessionMetadata { SystemPromptOverride = systemPromptOverride };
        var initialOverride = ResolveInitialSystemPromptOverride(options, metadata);

        Assert.Null(initialOverride);

        using var session = new CodaSession(
            SignedInClaude(),
            this.Options(initialOverride),
            history: []);
        session.Resume("source-aaaa", [], metadata);

        Assert.Equal(systemPromptOverride, session.Options.SystemPromptOverride);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("")]
    public async Task Forked_first_turn_persists_the_inherited_system_prompt_override(string systemPromptOverride)
    {
        var options = Parse("-p", "go", "--fork", "source-aaaa");
        var metadata = new SessionMetadata { SystemPromptOverride = systemPromptOverride };
        var initialOverride = ResolveInitialSystemPromptOverride(options, metadata);
        var loopFactory = new RecordingLoopFactory();
        const string forkId = "fork-aaaa";

        using (var session = new CodaSession(
                   SignedInClaude(),
                   this.Options(initialOverride),
                   sessionId: forkId,
                   agentLoopFactory: loopFactory))
        {
            var result = await session.RunAsync("go");
            Assert.True(result.Success);
        }

        var persisted = await new SessionTranscriptStore(this.root).LoadSessionAsync(forkId);
        Assert.NotNull(persisted);
        Assert.Equal(systemPromptOverride, persisted!.Metadata.SystemPromptOverride);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.root, recursive: true); } catch { /* ignore */ }
    }

    private SessionOptions Options(string? systemPromptOverride) => new()
    {
        ProviderId = ClaudeAiProvider.Id,
        Model = "claude-sonnet-4-6",
        WorkingDirectory = this.root,
        PermissionMode = PermissionMode.BypassPermissions,
        SystemPromptOverride = systemPromptOverride,
    };

    private static HeadlessOptions Parse(params string[] args)
    {
        Assert.True(HeadlessOptions.TryParse(args, out var options, out var error), error);
        return options;
    }

    private static string? ResolveInitialSystemPromptOverride(HeadlessOptions options, SessionMetadata metadata)
    {
        var assembly = HeadlessRunnerAssembly.Value;
        var runner = assembly.GetType("Coda.Tui.HeadlessRunner", throwOnError: true)!;
        var method = runner.GetMethod(
            "ResolveInitialSystemPromptOverride",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var targetType = assembly.GetType("Coda.Tui.SessionCli+ResumeTarget", throwOnError: true)!;
        var target = Activator.CreateInstance(
            targetType,
            "source-aaaa",
            Array.Empty<ChatMessage>(),
            metadata);
        return (string?)method!.Invoke(null, [options, target]);
    }

    private static readonly Lazy<Assembly> HeadlessRunnerAssembly = new(() =>
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "src", "Coda.Tui", "Coda.Tui.csproj");
        using var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{projectPath}\" --no-restore --nologo")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(build);
        var standardOutput = build!.StandardOutput.ReadToEndAsync();
        var standardError = build.StandardError.ReadToEndAsync();
        build!.WaitForExit();
        Assert.True(
            build.ExitCode == 0,
            $"Could not build Coda.Tui:{Environment.NewLine}{standardOutput.Result}{standardError.Result}");

        var assemblyPath = Path.Combine(repositoryRoot, "src", "Coda.Tui", "bin", "Debug", "net10.0", "Coda.Tui.dll");
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
    });

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "Coda.Tui", "Coda.Tui.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the coda-cli repository root.");
    }

    private sealed class RecordingLoopFactory : IAgentLoopFactory
    {
        public IAgentLoop Create(AgentLoopSpec spec) => new SuccessfulLoop();
    }

    private sealed class SuccessfulLoop : IAgentLoop
    {
        public GoalStatus? LastGoalStatus => null;

        public Task RunAsync(List<ChatMessage> history, IAgentSink sink, CancellationToken cancellationToken = default)
        {
            sink.OnAssistantText("done");
            sink.OnAssistantTextComplete();
            sink.OnStopReason("end_turn");
            return Task.CompletedTask;
        }
    }
}
