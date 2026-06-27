using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

/// <summary>Tests for the /memory and /init slash commands.</summary>
public sealed class MemoryCommandTests : IDisposable
{
    private readonly string tempDir;
    private readonly TestConsole console;
    private readonly CommandContext context;

    public MemoryCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(this.tempDir);

        this.console = new TestConsole();
        this.console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var credentials = new CredentialManager(store, new ICredentialProvider[]
        {
            new ClaudeAiProvider(),
            new GitHubCopilotProvider(),
            new ApiKeyProvider(),
        });

        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai", workingDirectory: this.tempDir);
        var registry = new SlashCommandRegistry(Array.Empty<ISlashCommand>());
        this.context = new CommandContext(this.console, credentials, session, providers, registry);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Memory_when_file_absent_prints_not_found_message()
    {
        var cmd = new MemoryCommand();

        var result = await cmd.ExecuteAsync(this.context, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.Contains("not found", this.console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/init", this.console.Output);
    }

    [Fact]
    public async Task Memory_when_file_exists_prints_its_contents()
    {
        var path = Path.Combine(this.tempDir, "CLAUDE.md");
        var expectedContent = "# Project Memory\nThis is the project memory file.";
        await File.WriteAllTextAsync(path, expectedContent);

        var cmd = new MemoryCommand();
        var result = await cmd.ExecuteAsync(this.context, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        Assert.Contains("Project Memory", this.console.Output);
        Assert.Contains("project memory file", this.console.Output);
    }

    [Fact]
    public async Task Memory_always_prints_the_file_path()
    {
        var cmd = new MemoryCommand();

        await cmd.ExecuteAsync(this.context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("CLAUDE.md", this.console.Output);
    }
}

public sealed class InitCommandTests : IDisposable
{
    private readonly string tempDir;
    private readonly TestConsole console;
    private readonly CommandContext context;

    public InitCommandTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(this.tempDir);

        this.console = new TestConsole();
        this.console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var credentials = new CredentialManager(store, new ICredentialProvider[]
        {
            new ClaudeAiProvider(),
            new GitHubCopilotProvider(),
            new ApiKeyProvider(),
        });

        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai", workingDirectory: this.tempDir);
        var registry = new SlashCommandRegistry(Array.Empty<ISlashCommand>());
        this.context = new CommandContext(this.console, credentials, session, providers, registry);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Init_when_claude_md_already_exists_does_not_overwrite_and_returns_continue()
    {
        var path = Path.Combine(this.tempDir, "CLAUDE.md");
        var originalContent = "# Original content — must not be changed";
        await File.WriteAllTextAsync(path, originalContent);

        var cmd = new InitCommand();
        var result = await cmd.ExecuteAsync(this.context, Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(CommandResult.Continue, result);
        var actualContent = await File.ReadAllTextAsync(path);
        Assert.Equal(originalContent, actualContent);
        Assert.Contains("already exists", this.console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Init_with_no_credentials_returns_continue_without_throwing_and_does_not_write_file()
    {
        // No credentials stored → LlmClientFactory returns null → RunResult.Success = false.
        var cmd = new InitCommand();

        var exception = await Record.ExceptionAsync(
            () => cmd.ExecuteAsync(this.context, Array.Empty<string>(), CancellationToken.None));

        Assert.Null(exception);

        var path = Path.Combine(this.tempDir, "CLAUDE.md");
        Assert.False(File.Exists(path));

        // The output should contain an error message (not overwrite path).
        Assert.DoesNotContain("already exists", this.console.Output, StringComparison.OrdinalIgnoreCase);
    }
}
