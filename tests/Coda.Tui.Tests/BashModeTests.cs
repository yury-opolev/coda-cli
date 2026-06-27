using Coda.Agent;
using Coda.Tui;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmClient;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class CommandParserBashTests
{
    [Theory]
    [InlineData("!ls", "ls")]
    [InlineData("!  git status ", "git status")]
    [InlineData("   !pwd", "pwd")]
    public void Bang_prefix_parses_as_Bash(string input, string expectedCommand)
    {
        var result = CommandParser.Parse(input);

        Assert.Equal(ParsedInputKind.Bash, result.Kind);
        Assert.Equal(expectedCommand, result.Text);
    }

    [Fact]
    public void Bang_only_parses_as_Empty()
    {
        var result = CommandParser.Parse("!");

        Assert.Equal(ParsedInputKind.Empty, result.Kind);
    }

    [Fact]
    public void Bang_in_middle_of_text_is_Prompt()
    {
        var result = CommandParser.Parse("tell me !important");

        Assert.Equal(ParsedInputKind.Prompt, result.Kind);
        Assert.Equal("tell me !important", result.Text);
    }

    [Fact]
    public void Slash_prefix_parses_as_Slash()
    {
        var result = CommandParser.Parse("/help");

        Assert.Equal(ParsedInputKind.Slash, result.Kind);
        Assert.Equal("help", result.Name);
    }

    [Fact]
    public void Plain_text_parses_as_Prompt()
    {
        var result = CommandParser.Parse("hi");

        Assert.Equal(ParsedInputKind.Prompt, result.Kind);
        Assert.Equal("hi", result.Text);
    }
}

public sealed class BashModeDispatchTests
{
    private static (TuiApp App, CommandContext Context, TestConsole Console) BuildApp(
        IShellExecutor shellExecutor,
        string? workingDirectory = null)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;

        var store = new InMemoryTokenStore();
        var claude = new ClaudeAiProvider();
        var credentials = new CredentialManager(store, new ICredentialProvider[] { claude });

        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };

        var session = new SessionState("claude-ai", workingDirectory ?? Directory.GetCurrentDirectory());
        var registry = new SlashCommandRegistry(new ISlashCommand[]
        {
            new HelpCommand(), new ExitCommand(),
        });

        var context = new CommandContext(console, credentials, session, providers, registry);
        var app = new TuiApp(context, mcpTools: null, shellExecutor: shellExecutor);
        return (app, context, console);
    }

    [Fact]
    public async Task Bash_runs_executor_and_records_history()
    {
        var fake = new FakeShellExecutor(new ShellResult(0, "file1.txt\nfile2.txt", string.Empty, false));
        var (app, context, console) = BuildApp(fake, workingDirectory: "C:\\work");

        var result = await app.DispatchAsync(ParsedInput.Bash("ls"), CancellationToken.None);

        // Executor invoked with correct command and working directory
        Assert.Equal("ls", fake.LastCommand);
        Assert.Equal("C:\\work", fake.LastWorkingDirectory);

        // Console output contains stdout
        Assert.Contains("file1.txt", console.Output);

        // History has exactly one user message
        Assert.Single(context.Session.History);
        var msg = context.Session.History[0];
        Assert.Equal(ChatRole.User, msg.Role);
        var block = msg.Content.OfType<TextBlock>().Single();
        Assert.Contains("<bash-input>ls</bash-input>", block.Text);
        Assert.Contains("<bash-stdout>", block.Text);
        Assert.Contains("<bash-stderr>", block.Text);

        // Continue (no exit)
        Assert.False(result.ShouldExit);
    }

    [Fact]
    public async Task Bash_does_not_run_agent()
    {
        // The fake executor is the only thing invoked; exactly 1 user message,
        // no assistant message — proves the agent was not run.
        var fake = new FakeShellExecutor(new ShellResult(0, "output", string.Empty, false));
        var (app, context, _) = BuildApp(fake);

        await app.DispatchAsync(ParsedInput.Bash("ls"), CancellationToken.None);

        Assert.Single(context.Session.History);
        Assert.Equal(ChatRole.User, context.Session.History[0].Role);
    }

    [Fact]
    public async Task Bash_executor_throwing_records_stderr_and_continues()
    {
        var fake = new ThrowingShellExecutor("disk full");
        var (app, context, console) = BuildApp(fake);

        var result = await app.DispatchAsync(ParsedInput.Bash("ls"), CancellationToken.None);

        // An error line was rendered (no crash)
        Assert.False(string.IsNullOrEmpty(console.Output));

        // History has 1 message with exception message in <bash-stderr>
        Assert.Single(context.Session.History);
        var block = context.Session.History[0].Content.OfType<TextBlock>().Single();
        Assert.Contains("<bash-stderr>", block.Text);
        Assert.Contains("disk full", block.Text);

        // Continues (no exit, no exception)
        Assert.False(result.ShouldExit);
    }

    [Fact]
    public async Task XmlEscape_escapes_in_history()
    {
        var fake = new FakeShellExecutor(new ShellResult(0, "<x>", string.Empty, false));
        var (app, context, _) = BuildApp(fake);

        await app.DispatchAsync(ParsedInput.Bash("echo <a> & <b>"), CancellationToken.None);

        var block = context.Session.History[0].Content.OfType<TextBlock>().Single();

        // Command escaping: < > & in command
        Assert.Contains("&lt;a&gt; &amp; &lt;b&gt;", block.Text);

        // Stdout escaping: <x> in stdout
        Assert.Contains("&lt;x&gt;", block.Text);
    }

    private sealed class FakeShellExecutor : IShellExecutor
    {
        private readonly ShellResult result;

        public FakeShellExecutor(ShellResult result)
        {
            this.result = result;
        }

        public string? LastCommand { get; private set; }

        public string? LastWorkingDirectory { get; private set; }

        public Task<ShellResult> RunAsync(
            string command,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            this.LastCommand = command;
            this.LastWorkingDirectory = workingDirectory;
            return Task.FromResult(this.result);
        }
    }

    private sealed class ThrowingShellExecutor : IShellExecutor
    {
        private readonly string message;

        public ThrowingShellExecutor(string message)
        {
            this.message = message;
        }

        public Task<ShellResult> RunAsync(
            string command,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(this.message);
        }
    }
}
