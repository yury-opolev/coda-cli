using System.Text;
using Coda.Agent.Hooks;
using Coda.Common;
using Coda.Tui.Ui.Rendering;

namespace Coda.Tui.Commands;

/// <summary>Pure text rendering for the <c>/hooks</c> list, info, and test views (no console dependency).</summary>
public static class HooksView
{
    /// <summary>Formats the list of all configured hooks.</summary>
    public static string FormatList(IReadOnlyList<UserHook> hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        if (hooks.Count == 0)
        {
            return "No hooks configured. Add hooks to ~/.coda/settings.json or .coda/settings.json (project).";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Hooks ({hooks.Count}):");
        for (var i = 0; i < hooks.Count; i++)
        {
            var hook = hooks[i];
            var enabled = hook.Enabled ? "enabled" : "disabled";
            var scope = hook.Scope == HookScope.Project ? "project" : "user";
            var matcher = string.IsNullOrEmpty(hook.Matcher) ? "*" : hook.Matcher;
            var handlerType = hook.HandlerType ?? "command";
            builder.Append($"  {i + 1,2}.")
                   .Append($"  {Identifier(hook.Event),-22}")
                   .Append($"  {Identifier(handlerType),-8}")
                   .Append($"  {Identifier(matcher),-24}")
                   .Append($"  [{scope}]")
                   .Append($"  {enabled}")
                   .AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Formats full detail for a single hook.</summary>
    public static string FormatInfo(int hookIndex, UserHook hook, HookRunEntry? lastRun)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var builder = new StringBuilder();
        var enabled = hook.Enabled ? "enabled" : "disabled";
        var scope = hook.Scope == HookScope.Project ? "project" : "user";
        var matcher = string.IsNullOrEmpty(hook.Matcher) ? "*" : hook.Matcher;
        var handlerType = hook.HandlerType ?? "command";

        builder.AppendLine($"Hook {hookIndex + 1}: {Identifier(hook.Event)}  [{scope}]  {enabled}");
        builder.AppendLine($"  handler:     {Identifier(handlerType)}");

        if (!string.IsNullOrEmpty(hook.Command))
        {
            builder.AppendLine($"  command:     {FreeText(hook.Command)}");
        }

        if (!string.IsNullOrEmpty(hook.Url))
        {
            builder.AppendLine($"  url:         {FreeText(hook.Url)}");
        }

        if (!string.IsNullOrEmpty(hook.HookPrompt))
        {
            var promptPreview = hook.HookPrompt.Length > 80
                ? hook.HookPrompt[..80] + "…"
                : hook.HookPrompt;
            builder.AppendLine($"  prompt:      {FreeText(promptPreview)}");
        }

        builder.AppendLine($"  matcher:     {Identifier(matcher)}");

        var timeoutSeconds = hook.TimeoutSeconds.HasValue
            ? $"{hook.TimeoutSeconds.Value}s"
            : "(event default)";
        var failOpen = hook.FailOpen.HasValue
            ? (hook.FailOpen.Value ? "fail-open" : "fail-closed")
            : "(event default)";
        var unattended = hook.UnattendedDecision is not null
            ? Identifier(hook.UnattendedDecision)
            : "(event default)";

        builder.AppendLine($"  timeout:     {timeoutSeconds}");
        builder.AppendLine($"  on error:    {failOpen}");
        builder.AppendLine($"  unattended:  {unattended}");
        builder.AppendLine($"  system-prompt replace: {(hook.AllowSystemPromptReplace ? "allowed" : "not allowed")}");

        if (hook.Mutates is { Count: > 0 } mutates)
        {
            builder.AppendLine($"  mutates:     {string.Join(", ", mutates.Select(Identifier))}");
        }
        else
        {
            builder.AppendLine("  mutates:     (none declared)");
        }

        if (lastRun is null)
        {
            builder.AppendLine("  last run:    (not run this session)");
        }
        else
        {
            var when = lastRun.RanAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            builder.AppendLine($"  last run:    {when}  outcome={Identifier(lastRun.Outcome)}  duration={lastRun.DurationMs}ms");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Formats the result of a <c>/hooks test</c> dry-run.</summary>
    public static string FormatTest(int hookIndex, UserHook hook, HookTestResult result)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        builder.AppendLine($"Dry-run: hook {hookIndex + 1} ({Identifier(hook.Event)})");
        builder.AppendLine();
        builder.AppendLine("--- Payload sent ---");
        builder.AppendLine(result.Payload);
        builder.AppendLine();

        if (!string.IsNullOrEmpty(result.RawStdout))
        {
            builder.AppendLine("--- stdout ---");
            builder.AppendLine(FreeText(result.RawStdout));
            builder.AppendLine();
        }

        if (!string.IsNullOrEmpty(result.RawStderr))
        {
            builder.AppendLine("--- stderr ---");
            builder.AppendLine(FreeText(result.RawStderr));
            builder.AppendLine();
        }

        builder.AppendLine($"--- exit code: {result.ExitCode} ---");
        builder.AppendLine();
        builder.AppendLine("--- Parsed decision ---");
        var output = result.ParsedOutput;
        builder.AppendLine($"  decision:  {Identifier(output.Decision ?? "allow")}");
        if (!string.IsNullOrEmpty(output.Reason))
        {
            builder.AppendLine($"  reason:    {FreeText(output.Reason)}");
        }

        if (!string.IsNullOrEmpty(output.SystemMessage))
        {
            builder.AppendLine($"  systemMessage: {FreeText(output.SystemMessage)}");
        }

        builder.AppendLine($"  continue:  {output.Continue}");
        builder.AppendLine();
        builder.AppendLine("Nothing was applied — this was a dry-run.");
        return builder.ToString().TrimEnd();
    }

    private static string Identifier(string? value) =>
        TerminalTextSanitizer.SanitizeSingleLine(value);

    private static string FreeText(string? value) =>
        SecretRedactor.Redact(TerminalTextSanitizer.SanitizeSingleLine(SecretRedactor.Redact(value)));
}
