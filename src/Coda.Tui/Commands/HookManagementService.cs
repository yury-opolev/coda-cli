using System.Text;
using System.Text.Json;
using Coda.Agent.Hooks;
using Coda.Agent.Settings;
using Coda.Common;

namespace Coda.Tui.Commands;

/// <summary>
/// Default implementation of <see cref="IHookManagementService"/>. Holds a mutable reference to
/// the session's hook list so that enable/disable changes take effect immediately within the
/// running session, and persists changes to the user settings file via
/// <see cref="SettingsWriter"/>.
/// </summary>
public sealed class HookManagementService : IHookManagementService
{
    private readonly List<UserHook> hooks;
    private readonly HookRunLog runLog;
    private readonly string? userSettingsDir;
    private readonly IHookExecutor executor;
    private readonly HookTrustGuard? trustGuard;

    /// <summary>
    /// Initialises the service.
    /// </summary>
    /// <param name="hooks">Mutable list shared with the session's <see cref="HookBus"/>.</param>
    /// <param name="runLog">Session-scoped run log used by <c>/hooks info</c>.</param>
    /// <param name="userSettingsDir">
    /// Directory containing the user settings file, used for persisting enable/disable state.
    /// Typically <c>~/.coda</c>. When <see langword="null"/>, changes are not persisted.
    /// </param>
    /// <param name="executor">
    /// Executor used for <c>/hooks test</c> dry-runs of <c>command</c>-type hooks.
    /// Defaults to <see cref="ShellHookExecutor"/> when <see langword="null"/>.
    /// </param>
    /// <param name="trustGuard">
    /// Optional trust guard. When provided, <c>/hooks test</c> refuses to dry-run untrusted
    /// project-scoped hooks (running the subprocess is the escalation, not just its output).
    /// </param>
    public HookManagementService(
        List<UserHook> hooks,
        HookRunLog runLog,
        string? userSettingsDir = null,
        IHookExecutor? executor = null,
        HookTrustGuard? trustGuard = null)
    {
        this.hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        this.runLog = runLog ?? throw new ArgumentNullException(nameof(runLog));
        this.userSettingsDir = userSettingsDir;
        this.executor = executor ?? new ShellHookExecutor();
        this.trustGuard = trustGuard;
    }

    /// <inheritdoc/>
    public IReadOnlyList<UserHook> Hooks => this.hooks;

    /// <inheritdoc/>
    public HookRunEntry? GetLastRun(int hookIndex) =>
        this.runLog.Get(hookIndex);

    /// <inheritdoc/>
    public void SetEnabled(int hookIndex, bool enabled)
    {
        if (hookIndex < 0 || hookIndex >= this.hooks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(hookIndex));
        }

        var hook = this.hooks[hookIndex];
        var hash = HookContentHash.Compute(hook);

        // Update in-memory state immediately so the current session respects the change.
        this.hooks[hookIndex] = hook with { Enabled = enabled };

        // Persist to user settings so future sessions honour the decision.
        if (this.userSettingsDir is not null)
        {
            SettingsWriter.SetHookEnabled(hash, enabled, this.userSettingsDir);
        }
    }

    /// <inheritdoc/>
    public async Task<HookTestResult> TestAsync(int hookIndex, CancellationToken ct = default)
    {
        if (hookIndex < 0 || hookIndex >= this.hooks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(hookIndex));
        }

        var hook = this.hooks[hookIndex];
        var payload = BuildRepresentativePayload(hook);

        // I2: Refuse to dry-run an untrusted project-scoped hook. Running the subprocess
        // is the escalation — the "dry-run" label does not make it safe.
        if (this.trustGuard is not null && hook.Scope == HookScope.Project)
        {
            var canRun = await this.trustGuard.CanRunAsync(hook, ct).ConfigureAwait(false);
            if (!canRun)
            {
                return new HookTestResult(
                    Payload: payload,
                    ExitCode: -1,
                    RawStdout: string.Empty,
                    RawStderr: "Cannot dry-run an untrusted project hook. Use '/hooks trust <n>' to grant trust first.",
                    ParsedOutput: HookOutput.NoOp);
            }
        }

        if (!string.Equals(hook.HandlerType, "command", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(hook.HandlerType))
        {
            // Non-command handler types (http, prompt, agent) cannot produce raw stdout/stderr;
            // return informational output without executing.
            var note = $"(dry-run not available for handler type '{hook.HandlerType}')";
            return new HookTestResult(
                Payload: payload,
                ExitCode: 0,
                RawStdout: note,
                RawStderr: string.Empty,
                ParsedOutput: HookOutput.NoOp);
        }

        var command = hook.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            return new HookTestResult(
                Payload: payload,
                ExitCode: -1,
                RawStdout: string.Empty,
                RawStderr: "hook has no 'command' field",
                ParsedOutput: HookOutput.NoOp);
        }

        var (exitCode, stdout, stderr) = await this.executor.ExecAsync(command, payload, ct).ConfigureAwait(false);
        var parsed = HookOutputParser.Parse(stdout);

        return new HookTestResult(
            Payload: payload,
            ExitCode: exitCode,
            RawStdout: stdout,
            RawStderr: stderr,
            ParsedOutput: parsed);
    }

    /// <summary>
    /// Builds a minimal representative payload JSON string for the hook's event. The values are
    /// illustrative placeholders so the hook author can see the shape without needing a real
    /// session context.
    /// </summary>
    private static string BuildRepresentativePayload(UserHook hook)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("event", hook.Event ?? "Unknown");
        writer.WriteString("session_id", "dry-run");
        writer.WriteString("working_dir", ".");
        writer.WriteString("model", "claude-opus-4-5");

        switch (hook.Event?.ToUpperInvariant())
        {
            case "PRETOOLUSE":
            case "POSTTOOLUSE":
                writer.WriteString("tool", hook.Matcher ?? "bash");
                writer.WritePropertyName("input");
                writer.WriteRawValue("{\"command\":\"echo dry-run\"}");
                if (string.Equals(hook.Event, "PostToolUse", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteString("result", "(dry-run output)");
                }

                break;

            case "PERMISSIONREQUEST":
                writer.WriteString("tool", hook.Matcher ?? "bash");
                writer.WritePropertyName("input");
                writer.WriteRawValue("{\"command\":\"echo dry-run\"}");
                writer.WriteString("permissionMode", "default");
                break;

            case "USERPROMPTSUBMIT":
                writer.WriteString("prompt", "(dry-run prompt)");
                writer.WriteStartArray("attachments");
                writer.WriteEndArray();
                writer.WriteNumber("historyLength", 0);
                writer.WriteString("permissionMode", "default");
                break;

            case "SESSIONSTART":
                writer.WriteString("source", "cli");
                writer.WriteString("permissionMode", "default");
                break;

            case "SESSIONEND":
                writer.WriteString("reason", "exit");
                writer.WriteNumber("durationMs", 0);
                writer.WriteNumber("turnCount", 0);
                break;

            case "NOTIFICATION":
                writer.WriteString("kind", "info");
                writer.WriteString("message", "(dry-run notification)");
                break;

            case "STOP":
                writer.WriteNumber("iterations", 1);
                writer.WriteNumber("continuationCount", 0);
                writer.WriteBoolean("stopHookActive", false);
                break;

            case "AGENTRESPONSE":
                writer.WriteString("response", "(dry-run response)");
                writer.WriteStartObject("usage");
                writer.WriteNumber("inputTokens", 0);
                writer.WriteNumber("outputTokens", 0);
                writer.WriteEndObject();
                break;

            case "SUBAGENTSTART":
                writer.WriteString("prompt", "(dry-run subagent prompt)");
                writer.WriteNumber("depth", 1);
                break;

            case "SUBAGENTSTOP":
                writer.WriteString("result", "(dry-run)");
                writer.WriteNumber("depth", 1);
                break;

            case "PRECOMPACT":
                writer.WriteString("trigger", "manual");
                writer.WriteNumber("tokensBefore", 0);
                break;

            case "POSTCOMPACT":
                writer.WriteNumber("tokensBefore", 0);
                writer.WriteNumber("tokensAfter", 0);
                break;
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
