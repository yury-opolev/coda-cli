using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Coda.Agent.Hooks;

/// <summary>
/// Executes user-configured shell hooks at agent lifecycle events
/// (PreToolUse, PostToolUse, Stop).
/// </summary>
/// <remarks>
/// Process execution is injected via <paramref name="execOverride"/> for testability.
/// When null, the real OS shell is used (cmd.exe on Windows, /bin/sh elsewhere).
/// A broken hook command (exec exception) is treated as Allow and never propagates.
/// </remarks>
public sealed class UserHookRunner
{
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<UserHook> hooks;
    private readonly Func<string, string, CancellationToken, Task<(int exitCode, string stdout)>>? execOverride;

    public UserHookRunner(
        IReadOnlyList<UserHook> hooks,
        Func<string, string, CancellationToken, Task<(int exitCode, string stdout)>>? execOverride = null)
    {
        this.hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        this.execOverride = execOverride;
        this.hasPreToolUse = hooks.Any(h => string.Equals(h.Event, "PreToolUse", StringComparison.OrdinalIgnoreCase));
    }

    private readonly bool hasPreToolUse;

    /// <summary>True when at least one PreToolUse hook is configured.</summary>
    public bool HasPreToolUse => this.hasPreToolUse;

    /// <summary>
    /// Runs all matching PreToolUse hooks in order.
    /// Returns <see cref="UserHookResult"/> with Block=true on the first non-zero exit.
    /// Exec exceptions are swallowed and treated as Allow.
    /// </summary>
    public async Task<UserHookResult> RunPreToolUseAsync(string toolName, string inputJson, CancellationToken ct)
    {
        var payload = BuildPayload(toolName, inputJson);
        foreach (var hook in this.hooks)
        {
            if (!string.Equals(hook.Event, "PreToolUse", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!MatchesTool(hook, toolName))
            {
                continue;
            }

            try
            {
                var (exitCode, stdout) = await this.ExecAsync(hook.Command, payload, ct).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    var message = string.IsNullOrWhiteSpace(stdout)
                        ? "blocked by PreToolUse hook"
                        : stdout;
                    return new UserHookResult(Block: true, Message: message);
                }
            }
            catch
            {
                // A broken hook command must not crash the turn — treat exec failure as Allow.
            }
        }

        return UserHookResult.Allow;
    }

    /// <summary>
    /// Runs all matching PostToolUse hooks. Exit code and errors are ignored.
    /// </summary>
    public async Task RunPostToolUseAsync(string toolName, string inputJson, string toolResultText, CancellationToken ct)
    {
        var payload = BuildPostPayload(toolName, inputJson, toolResultText);
        foreach (var hook in this.hooks)
        {
            if (!string.Equals(hook.Event, "PostToolUse", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!MatchesTool(hook, toolName))
            {
                continue;
            }

            try
            {
                await this.ExecAsync(hook.Command, payload, ct).ConfigureAwait(false);
            }
            catch
            {
                // Ignore all errors for observation-only hooks.
            }
        }
    }

    /// <summary>
    /// Runs all Stop hooks. Exit code and errors are ignored.
    /// </summary>
    public async Task RunStopAsync(CancellationToken ct)
    {
        var payload = "{}";
        foreach (var hook in this.hooks)
        {
            if (!string.Equals(hook.Event, "Stop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                await this.ExecAsync(hook.Command, payload, ct).ConfigureAwait(false);
            }
            catch
            {
                // Ignore all errors for observation-only hooks.
            }
        }
    }

    private Task<(int exitCode, string stdout)> ExecAsync(string command, string stdinPayload, CancellationToken ct)
    {
        if (this.execOverride is not null)
        {
            return this.execOverride(command, stdinPayload, ct);
        }

        return ExecShellAsync(command, stdinPayload, ct);
    }

    private static async Task<(int exitCode, string stdout)> ExecShellAsync(
        string command,
        string stdinPayload,
        CancellationToken ct)
    {
        var (shell, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", command })
            : ("/bin/sh", new[] { "-c", command });

        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Write payload to stdin then close so the process can read EOF.
        await process.StandardInput.WriteAsync(stdinPayload).ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HookTimeout);

        // Drain both pipes concurrently to avoid deadlock.
        // Pass the linked timeout token so the read tasks are cancelled on timeout too.
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, maxChars: 4096, timeoutCts.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, maxChars: 4096, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }

            // Drain read tasks so the streams are settled before the Process is disposed.
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                // Drained / cancelled — expected on timeout path.
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            // Timeout — treat as Allow (broken hook).
            return (0, string.Empty);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false); // drain stderr to avoid deadlock

        return (process.ExitCode, stdout);
    }

    private static async Task<string> ReadBoundedAsync(
        System.IO.TextReader reader,
        int maxChars,
        CancellationToken ct)
    {
        var buffer = new char[maxChars];
        var sb = new StringBuilder();
        int read;
        while (sb.Length < maxChars && (read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            sb.Append(buffer, 0, read);
        }

        return sb.ToString();
    }

    private static bool MatchesTool(UserHook hook, string toolName) =>
        hook.Matcher is null ||
        string.Equals(hook.Matcher, toolName, StringComparison.OrdinalIgnoreCase);

    private static string BuildPayload(string toolName, string inputJson)
    {
        var normalizedInput = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson;
        try
        {
            using var doc = JsonDocument.Parse(normalizedInput);
            using var ms = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("tool", toolName);
                writer.WritePropertyName("input");
                doc.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (JsonException)
        {
            // Malformed tool input — send the input as a JSON string so the payload stays valid.
            using var ms = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteString("tool", toolName);
                writer.WriteString("input", inputJson);
                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private static string BuildPostPayload(string toolName, string inputJson, string resultText)
    {
        var normalizedInput = string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson;
        JsonElement inputElement;
        bool inputParsed;
        try
        {
            using var doc = JsonDocument.Parse(normalizedInput);
            inputElement = doc.RootElement.Clone();
            inputParsed = true;
        }
        catch (JsonException)
        {
            inputElement = default;
            inputParsed = false;
        }

        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("tool", toolName);
            writer.WritePropertyName("input");
            if (inputParsed)
            {
                inputElement.WriteTo(writer);
            }
            else
            {
                writer.WriteStringValue(inputJson);
            }

            writer.WriteString("result", resultText);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
