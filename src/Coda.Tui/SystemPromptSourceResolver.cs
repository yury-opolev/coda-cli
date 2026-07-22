namespace Coda.Tui;

using System.Text;

public abstract record SystemPromptSource
{
    public sealed record Inline(string Text) : SystemPromptSource;
    public sealed record FilePath(string Path) : SystemPromptSource;
}

public sealed class SystemPromptSourceException : Exception
{
    public SystemPromptSourceException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public static class SystemPromptSourceResolver
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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

        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            if (argument is "--system-prompt" or "--system-prompt-file")
            {
                if (source is not null)
                {
                    remainingArgs = args;
                    error = "Specify only one of --system-prompt or --system-prompt-file, once.";
                    return false;
                }

                if (i + 1 >= args.Count
                    || args[i + 1] is "--system-prompt" or "--system-prompt-file")
                {
                    remainingArgs = args;
                    error = $"{argument} requires a value.";
                    return false;
                }

                var value = args[++i];
                source = argument == "--system-prompt"
                    ? new SystemPromptSource.Inline(value)
                    : new SystemPromptSource.FilePath(value);
                continue;
            }

            if (argument.StartsWith("--system-prompt=", StringComparison.Ordinal)
                || argument.StartsWith("--system-prompt-file=", StringComparison.Ordinal))
            {
                remainingArgs = args;
                error = "System prompt options require separate arguments for the flag and value.";
                return false;
            }

            remaining.Add(argument);
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
        string path;
        try
        {
            path = Path.IsPathRooted(fileSource.Path)
                ? Path.GetFullPath(fileSource.Path)
                : Path.GetFullPath(fileSource.Path, startupWorkingDirectory);

            if (Directory.Exists(path))
            {
                throw new SystemPromptSourceException($"System prompt path is a directory: {path}");
            }

            var bytes = await readAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
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
        catch (DecoderFallbackException exception)
        {
            throw new SystemPromptSourceException("System prompt file is not valid UTF-8.", exception);
        }
        catch (IOException exception)
        {
            throw new SystemPromptSourceException($"Unable to read system prompt file '{fileSource.Path}'.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new SystemPromptSourceException(
                $"Unable to access system prompt file '{fileSource.Path}': {exception.Message}", exception);
        }
        catch (ArgumentException exception)
        {
            throw new SystemPromptSourceException($"Invalid system prompt file path '{fileSource.Path}'.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new SystemPromptSourceException($"Invalid system prompt file path '{fileSource.Path}'.", exception);
        }
    }
}
