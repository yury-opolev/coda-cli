namespace Coda.Tui.Tests;

using Coda.Tui;

public sealed class SystemPromptSourceResolverTests
{
    [Fact]
    public void Inline_extraction_preserves_text_and_remaining_order()
    {
        var args = new[] { "--system-prompt", "a\r\n b ", "--plain", "--resume", "abc" };

        var result = SystemPromptSourceResolver.TryExtract(args, out var remaining, out var source, out var error);

        Assert.True(result);
        Assert.Null(error);
        Assert.Equal(new[] { "--plain", "--resume", "abc" }, remaining);
        Assert.Equal("a\r\n b ", Assert.IsType<SystemPromptSource.Inline>(source).Text);
    }

    [Theory]
    [InlineData("--system-prompt")]
    [InlineData("--system-prompt-file")]
    public void Source_flag_without_value_returns_error(string flag)
    {
        var result = SystemPromptSourceResolver.TryExtract(new[] { flag }, out _, out _, out var error);

        Assert.False(result);
        Assert.Equal($"{flag} requires a value.", error);
    }

    [Fact]
    public void Duplicate_source_is_rejected()
    {
        var result = SystemPromptSourceResolver.TryExtract(
            new[] { "--system-prompt", "one", "--system-prompt-file", "two" },
            out _, out _, out var error);

        Assert.False(result);
        Assert.Equal("Specify only one of --system-prompt or --system-prompt-file, once.", error);
    }

    [Fact]
    public void Prompt_flag_after_flag_is_previous_flag_missing_value()
    {
        var result = SystemPromptSourceResolver.TryExtract(
            new[] { "--plain", "--system-prompt", "--resume" },
            out _, out _, out var error);

        Assert.False(result);
        Assert.Equal("--system-prompt requires a value.", error);
    }

    [Theory]
    [InlineData("--system-prompt=hello")]
    [InlineData("--system-prompt-file=hello")]
    public void Equals_forms_are_rejected(string argument)
    {
        var result = SystemPromptSourceResolver.TryExtract(new[] { argument }, out _, out _, out var error);

        Assert.False(result);
        Assert.Contains("separate arguments", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bom_unicode_mixed_line_endings_and_trailing_newline_round_trip()
    {
        var directory = CreateDirectory();
        try
        {
            var expected = "Привет\r\n世界\nlast\r\n";
            byte[] bytes = [0xEF, 0xBB, 0xBF, ..System.Text.Encoding.UTF8.GetBytes(expected)];
            var path = Path.Combine(directory, "prompt.txt");
            await File.WriteAllBytesAsync(path, bytes);

            var actual = await SystemPromptSourceResolver.ResolveAsync(
                new SystemPromptSource.FilePath(path), directory);

            Assert.Equal(expected, actual);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Invalid_utf8_throws_source_exception()
    {
        var exception = await Assert.ThrowsAsync<SystemPromptSourceException>(() =>
            SystemPromptSourceResolver.ResolveAsync(
                new SystemPromptSource.FilePath("prompt"), Path.GetTempPath(),
                (_, _) => Task.FromResult(new byte[] { 0xC3, 0x28 }), CancellationToken.None));

        Assert.Contains("valid UTF-8", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Injectable_reader_is_invoked_exactly_once()
    {
        var calls = 0;
        var actual = await SystemPromptSourceResolver.ResolveAsync(
            new SystemPromptSource.FilePath("prompt"), Path.GetTempPath(),
            (_, _) =>
            {
                calls++;
                return Task.FromResult(System.Text.Encoding.UTF8.GetBytes("text"));
            }, CancellationToken.None);

        Assert.Equal("text", actual);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public async Task Empty_and_whitespace_files_are_exact(string expected)
    {
        var actual = await SystemPromptSourceResolver.ResolveAsync(
            new SystemPromptSource.FilePath("prompt"), Path.GetTempPath(),
            (_, _) => Task.FromResult(System.Text.Encoding.UTF8.GetBytes(expected)), CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Missing_and_directory_paths_throw_source_exceptions()
    {
        var directory = CreateDirectory();
        try
        {
            await Assert.ThrowsAsync<SystemPromptSourceException>(() =>
                SystemPromptSourceResolver.ResolveAsync(
                    new SystemPromptSource.FilePath(Path.Combine(directory, "missing")), directory));

            var exception = await Assert.ThrowsAsync<SystemPromptSourceException>(() =>
                SystemPromptSourceResolver.ResolveAsync(
                    new SystemPromptSource.FilePath(directory), directory));
            Assert.Contains("directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Malformed_and_access_errors_are_wrapped()
    {
        var malformed = await Assert.ThrowsAsync<SystemPromptSourceException>(() =>
            SystemPromptSourceResolver.ResolveAsync(
                new SystemPromptSource.FilePath("\0"), "startup"));
        Assert.NotNull(malformed.InnerException);

        var reason = "denied by test";
        var access = await Assert.ThrowsAsync<SystemPromptSourceException>(() =>
            SystemPromptSourceResolver.ResolveAsync(
                new SystemPromptSource.FilePath("prompt"), Path.GetTempPath(),
                (_, _) => Task.FromException<byte[]>(new UnauthorizedAccessException(reason)), CancellationToken.None));
        Assert.Contains(reason, access.Message);
        Assert.IsType<UnauthorizedAccessException>(access.InnerException);
    }

    [Fact]
    public async Task Large_file_is_not_truncated()
    {
        var expected = new string('x', 1_100_000);
        var actual = await SystemPromptSourceResolver.ResolveAsync(
            new SystemPromptSource.FilePath("prompt"), Path.GetTempPath(),
            (_, _) => Task.FromResult(System.Text.Encoding.UTF8.GetBytes(expected)), CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Relative_path_uses_startup_working_directory()
    {
        var startup = CreateDirectory();
        var session = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(startup, "prompt.txt"), "startup");
            await File.WriteAllTextAsync(Path.Combine(session, "prompt.txt"), "session");

            var actual = await SystemPromptSourceResolver.ResolveAsync(
                new SystemPromptSource.FilePath("prompt.txt"), startup);

            Assert.Equal("startup", actual);
        }
        finally
        {
            DeleteDirectory(startup);
            DeleteDirectory(session);
        }
    }

    [Fact]
    public async Task Null_source_returns_null_and_cancellation_is_not_wrapped()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Null(await SystemPromptSourceResolver.ResolveAsync(null, "unused", cancellation.Token));

        var expected = new OperationCanceledException(cancellation.Token);
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SystemPromptSourceResolver.ResolveAsync(
                new SystemPromptSource.FilePath("prompt"), Path.GetTempPath(),
                (_, token) => Task.FromException<byte[]>(expected), cancellation.Token));
        Assert.Same(expected, actual);
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "coda-system-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
