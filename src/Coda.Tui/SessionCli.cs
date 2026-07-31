using Coda.Sdk;
using Coda.Tui.Repl;
using LlmClient;

namespace Coda.Tui;

/// <summary>Resolves a continue/resume request to a concrete session id + its loaded history.</summary>
public static class SessionCli
{
    /// <summary>A resolved resume target: the session id to adopt and its persisted messages.</summary>
    public sealed record ResumeTarget(
        string Id,
        IReadOnlyList<ChatMessage> Messages,
        SessionMetadata Metadata)
    {
        public ResumeTarget(string id, IReadOnlyList<ChatMessage> messages)
            : this(id, messages, SessionMetadata.Empty)
        {
        }

        public void Deconstruct(out string id, out IReadOnlyList<ChatMessage> messages)
        {
            id = this.Id;
            messages = this.Messages;
        }
    }

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

        var stored = await store.LoadSessionAsync(id, ct).ConfigureAwait(false);
        return stored is null ? null : new ResumeTarget(id, stored.Messages, stored.Metadata);
    }

    /// <summary>Apply a resumed root session to the mutable TUI state.</summary>
    public static void ApplyResumeTarget(SessionState session, ResumeTarget target)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(target);

        session.History.Clear();
        session.History.AddRange(target.Messages);
        session.SessionId = target.Id;
        session.SystemPromptOverride =
            session.StartupSystemPromptOverride
            ?? target.Metadata.SystemPromptOverride;
    }

    /// <summary>
    /// Parse a continue/resume/fork launch intent from argv: `-c`/`--continue`/`continue` → newest; `-r`/
    /// `--resume`/`resume` with an id → that id, or without one → newest; `-f`/`--fork`/`fork` with an id →
    /// fork that id, or without one → fork the newest, each into a new session id. Anything else → no intent.
    /// </summary>
    /// <remarks>
    /// Every argument is examined, not just the first. Reading argv[0] alone meant any flag in front of
    /// the intent — <c>--yolo</c>, say — silently defeated resume, continue and fork: the session started
    /// empty and nothing said why. Flag order must not decide whether a session is restored.
    /// </remarks>
    public static StartupIntent ParseStartupIntent(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-c" or "--continue" or "continue":
                    return new StartupIntent(true, null);

                case "-r" or "--resume" or "resume":
                    return TakesId(args, i, out var resumeId)
                        ? new StartupIntent(false, resumeId)
                        : new StartupIntent(true, null);

                case "-f" or "--fork" or "fork":
                    return TakesId(args, i, out var forkId)
                        ? new StartupIntent(false, forkId, Fork: true)
                        : new StartupIntent(true, null, Fork: true);
            }
        }

        return new StartupIntent(false, null);
    }

    /// <summary>Whether the argument after <paramref name="index"/> is an id rather than another flag.</summary>
    private static bool TakesId(IReadOnlyList<string> args, int index, out string id)
    {
        if (index + 1 < args.Count && !args[index + 1].StartsWith('-'))
        {
            id = args[index + 1];
            return true;
        }

        id = string.Empty;
        return false;
    }
}

/// <summary>A parsed TUI startup continue/resume/fork intent.</summary>
public sealed record StartupIntent(bool ContinueLatest, string? ResumeId, bool Fork = false)
{
    /// <summary>True when the user asked to continue, resume, or fork a session at launch.</summary>
    public bool HasIntent => this.ContinueLatest || this.ResumeId is not null;
}
