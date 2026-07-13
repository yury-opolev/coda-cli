using Coda.Sdk;
using LlmClient;

namespace Coda.Tui;

/// <summary>Resolves a continue/resume request to a concrete session id + its loaded history.</summary>
public static class SessionCli
{
    /// <summary>A resolved resume target: the session id to adopt and its persisted messages.</summary>
    public sealed record ResumeTarget(string Id, IReadOnlyList<ChatMessage> Messages);

    /// <summary>
    /// Resolve a continue/resume request over <paramref name="workingDirectory"/>'s sessions.
    /// <paramref name="continueLatest"/> → the newest session in the dir; <paramref name="resumeId"/> →
    /// that id. Returns <see langword="null"/> when nothing is requested, or the target is missing.
    /// </summary>
    public static async Task<ResumeTarget?> ResolveAsync(
        string workingDirectory, bool continueLatest, string? resumeId, CancellationToken ct = default)
    {
        var store = new SessionTranscriptStore(workingDirectory);

        var id = resumeId;
        if (continueLatest)
        {
            var summaries = await store.ListAsync(ct).ConfigureAwait(false);
            id = summaries.Count > 0 ? summaries[0].Id : null;
        }

        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var messages = await store.LoadAsync(id, ct).ConfigureAwait(false);
        return messages is null ? null : new ResumeTarget(id, messages);
    }

    /// <summary>
    /// Parse a continue/resume launch intent from argv: `-c`/`--continue`/`continue` → newest; `-r`/
    /// `--resume`/`resume` with an id → that id, or without one → newest. Anything else → no intent.
    /// </summary>
    public static StartupIntent ParseStartupIntent(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new StartupIntent(false, null);
        }

        switch (args[0])
        {
            case "-c" or "--continue" or "continue":
                return new StartupIntent(true, null);
            case "-r" or "--resume" or "resume":
                var hasId = args.Count > 1 && !args[1].StartsWith('-');
                return hasId ? new StartupIntent(false, args[1]) : new StartupIntent(true, null);
            default:
                return new StartupIntent(false, null);
        }
    }
}

/// <summary>A parsed TUI startup continue/resume intent.</summary>
public sealed record StartupIntent(bool ContinueLatest, string? ResumeId)
{
    /// <summary>True when the user asked to continue or resume a session at launch.</summary>
    public bool HasIntent => this.ContinueLatest || this.ResumeId is not null;
}
