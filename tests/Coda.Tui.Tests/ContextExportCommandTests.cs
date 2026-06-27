using Coda.Tui;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmClient;

namespace Coda.Tui.Tests;

public sealed class ContextExportCommandTests
{
    // ── /context tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task Context_on_empty_history_shows_breakdown_with_zero_messages()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();

        var command = new ContextCommand();
        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("Context Usage", console.Output);
        Assert.Contains("0 messages", console.Output);
        Assert.Contains("Free space", console.Output);
    }

    [Fact]
    public async Task Context_with_messages_shows_token_count_and_message_count()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.History.Add(ChatMessage.UserText("Hello, how are you?"));
        context.Session.History.Add(new ChatMessage(ChatRole.Assistant, [new TextBlock("I am doing well, thank you!")]));

        var command = new ContextCommand();
        var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Contains("tokens", console.Output);
        Assert.Contains("2", console.Output);
        Assert.Contains("messages", console.Output);
    }

    [Fact]
    public async Task Context_with_one_message_uses_singular_word()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        context.Session.History.Add(ChatMessage.UserText("Single message here."));

        var command = new ContextCommand();
        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        // The header uses the singular form for a single message.
        Assert.Contains("across 1 message", console.Output);
    }

    [Fact]
    public async Task Context_shows_percentage()
    {
        var (_, context, console, _) = TestAppBuilder.BuildApp();
        // Add enough text to produce a non-zero percentage (4000 chars → ~1000 tokens → 0.5%)
        context.Session.History.Add(ChatMessage.UserText(new string('a', 4000)));

        var command = new ContextCommand();
        await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        Assert.Contains("%", console.Output);
    }

    // ── /export tests ────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_on_empty_history_writes_no_file_and_says_nothing_to_export()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var (_, context, console, _) = TestAppBuilder.BuildApp();
            context.Session.WorkingDirectory = tempDir;

            var command = new ExportCommand();
            var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

            Assert.False(result.ShouldExit);
            Assert.Contains("Nothing to export", console.Output);
            Assert.Empty(Directory.GetFiles(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Export_writes_markdown_file_with_user_and_assistant_headings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var (_, context, console, _) = TestAppBuilder.BuildApp();
            context.Session.WorkingDirectory = tempDir;
            context.Session.History.Add(ChatMessage.UserText("Hello world"));
            context.Session.History.Add(new ChatMessage(ChatRole.Assistant, [new TextBlock("Hi there!")]));

            var command = new ExportCommand();
            var result = await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

            Assert.False(result.ShouldExit);

            var files = Directory.GetFiles(tempDir, "coda-conversation-*.md");
            Assert.Single(files);

            var content = File.ReadAllText(files[0]);
            Assert.Contains("## User", content);
            Assert.Contains("## Assistant", content);
            Assert.Contains("Hello world", content);
            Assert.Contains("Hi there!", content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Export_renders_tool_use_and_tool_result_blocks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var (_, context, console, _) = TestAppBuilder.BuildApp();
            context.Session.WorkingDirectory = tempDir;
            context.Session.History.Add(new ChatMessage(ChatRole.Assistant,
            [
                new ToolUseBlock("id1", "read_file", "{}"),
            ]));
            context.Session.History.Add(new ChatMessage(ChatRole.User,
            [
                new ToolResultBlock("id1", "file contents here"),
            ]));

            var command = new ExportCommand();
            await command.ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

            var files = Directory.GetFiles(tempDir, "coda-conversation-*.md");
            Assert.Single(files);

            var content = File.ReadAllText(files[0]);
            Assert.Contains("- tool call: read_file", content);
            Assert.Contains("- tool result", content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Export_accepts_custom_output_path_argument()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var (_, context, console, _) = TestAppBuilder.BuildApp();
            context.Session.WorkingDirectory = tempDir;
            context.Session.History.Add(ChatMessage.UserText("Custom path test"));

            var customFileName = "my-export.md";
            var command = new ExportCommand();
            await command.ExecuteAsync(context, [customFileName], CancellationToken.None);

            var expectedPath = Path.Combine(tempDir, customFileName);
            Assert.True(File.Exists(expectedPath));

            var content = File.ReadAllText(expectedPath);
            Assert.Contains("Custom path test", content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Export_to_nonexistent_parent_directory_does_not_throw_and_returns_continue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var (_, context, console, _) = TestAppBuilder.BuildApp();
            context.Session.WorkingDirectory = tempDir;
            context.Session.History.Add(ChatMessage.UserText("Write failure test"));

            // Parent directory does not exist → DirectoryNotFoundException (: IOException) on WriteAllText
            var invalidPath = Path.Combine(tempDir, "nope-does-not-exist-dir", "x.md");
            var command = new ExportCommand();

            var result = await command.ExecuteAsync(context, [invalidPath], CancellationToken.None);

            Assert.False(result.ShouldExit);
            Assert.Contains("Export failed", console.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(invalidPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
