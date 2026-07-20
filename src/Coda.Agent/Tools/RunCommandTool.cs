using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>Runs a shell command via PowerShell and returns its output. Mutating — requires permission.</summary>
public sealed class RunCommandTool : ITool
{
    /// <summary>
    /// Default command timeout: 10 minutes. A hung shell command is the tool's own
    /// concern (it is bounded here, at the layer of the operation it guards), not by any
    /// turn- or session-level watchdog. Overridable via <see cref="TimeoutEnv"/>.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Environment variable overriding the command timeout (whole seconds; &lt;= 0 disables).</summary>
    public const string TimeoutEnv = "CODA_RUN_COMMAND_TIMEOUT";

    private const int MaxChars = 30_000;

    public string Name => "run_command";

    public string Description => "Run a shell command (PowerShell) in the working directory and return combined stdout/stderr.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"command":{"type":"string","description":"The command line to run"},"timeoutSeconds":{"type":"integer","description":"Optional maximum seconds to allow this command to run before it is terminated (default 600). Raise it for a known-long command (a build, a large test suite); only this command is terminated on timeout, the session keeps running."},"run_in_background":{"type":"boolean","description":"When true, start the command as a background task and return its id immediately instead of waiting; read its progress with task_output and stop it with task_stop."}},"required":["command"]}
        """;

    public bool IsReadOnly => false;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        var command = ToolInput.GetString(input, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return new ToolResult("Missing required 'command'.", IsError: true);
        }

        var timeout = ResolveTimeout(TryGetTimeoutSeconds(input), Environment.GetEnvironmentVariable(TimeoutEnv));
        var runInBackground = TryGetRunInBackground(input);

        // No task manager wired (e.g. some unit tests): a background request cannot be honored
        // (there is nothing to observe or stop it), so fail loudly rather than silently running
        // it synchronously and discarding the caller's background intent. A normal foreground
        // command still falls back to direct, unmanaged execution exactly as before.
        if (context.Tasks is null)
        {
            if (runInBackground)
            {
                return new ToolResult(
                    "Cannot run in background: no task runtime is available in this session.",
                    IsError: true);
            }

            var executor = new ProcessShellExecutor(context.Logger, this.Name);
            var direct = await executor.RunAsync(command, context.WorkingDirectory, timeout, cancellationToken).ConfigureAwait(false);
            return FormatResult(direct.ExitCode, direct.Stdout, direct.Stderr, direct.TimedOut, timeout);
        }

        if (runInBackground)
        {
            var id = context.Tasks.StartShellBackground(command, context.WorkingDirectory, timeout, context.CurrentTaskId);
            return new ToolResult($"Started background task {id}. Use task_output to read its progress.");
        }

        var result = await context.Tasks
            .RunShellAsync(command, context.WorkingDirectory, timeout, context.CurrentTaskId, cancellationToken)
            .ConfigureAwait(false);

        if (result.Detached)
        {
            return new ToolResult($"Command detached as background task {result.TaskId}. Use task_output to read its progress.");
        }

        // A foreground shell killed by turn cancellation (that did not win a detach) must unwind the
        // parent turn exactly like the direct ProcessShellExecutor path, which rethrows on genuine
        // caller cancellation. Detach already returned above, so this only fires when cancellation won.
        cancellationToken.ThrowIfCancellationRequested();

        return FormatResult(result.ExitCode, result.Stdout, result.Stderr, result.TimedOut, timeout);
    }

    /// <summary>Formats a completed shell result exactly as before: exit code header, combined
    /// stdout+stderr, 30k-char truncation, and <c>IsError</c> on a non-zero exit.</summary>
    private static ToolResult FormatResult(int exitCode, string stdout, string stderr, bool timedOut, TimeSpan timeout)
    {
        if (timedOut)
        {
            return new ToolResult($"Command timed out after {timeout.TotalSeconds:N0}s.", IsError: true);
        }

        var text = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}{stderr}";
        if (text.Length > MaxChars)
        {
            text = text[..MaxChars] + $"\n… [truncated, {text.Length} chars total]";
        }

        var result = $"exit code: {exitCode}\n{text}".TrimEnd();
        return new ToolResult(result, IsError: exitCode != 0);
    }

    private static bool TryGetRunInBackground(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty("run_in_background", out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Resolve the effective timeout. A positive per-call value (the model's choice for this
    /// specific command) wins; otherwise fall back to the <see cref="TimeoutEnv"/> override,
    /// then <see cref="DefaultTimeout"/>.
    /// </summary>
    public static TimeSpan ResolveTimeout(int? perCallSeconds, string? rawEnv)
    {
        if (perCallSeconds is > 0)
        {
            return TimeSpan.FromSeconds(perCallSeconds.Value);
        }

        return ResolveTimeout(rawEnv);
    }

    /// <summary>
    /// Resolve the command timeout from the raw <see cref="TimeoutEnv"/> value: whole
    /// seconds when parseable, <see cref="DefaultTimeout"/> when unset/unparseable, and
    /// <see cref="Timeout.InfiniteTimeSpan"/> (no timeout) when &lt;= 0.
    /// </summary>
    public static TimeSpan ResolveTimeout(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var seconds))
        {
            return DefaultTimeout;
        }

        return seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Reads the optional per-call <c>timeoutSeconds</c> argument, if the model supplied one.</summary>
    private static int? TryGetTimeoutSeconds(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("timeoutSeconds", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var seconds))
        {
            return seconds;
        }

        return null;
    }
}
