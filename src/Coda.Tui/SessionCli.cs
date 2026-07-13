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
}
