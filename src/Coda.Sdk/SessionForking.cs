using LlmClient;

namespace Coda.Sdk;

/// <summary>Creates a forked session: a brand-new id seeded from an existing session's transcript
/// and audit sidecar, leaving the source untouched.</summary>
public static class SessionForking
{
    /// <summary>
    /// Forks <paramref name="sourceId"/> into a fresh session id under <paramref name="workingDirectory"/>:
    /// persists <paramref name="messages"/> as the new session's transcript and copies the source's audit
    /// sidecar so the fork is a complete, resumable, fully-auditable session. The source is never modified.
    /// Returns the new id.
    /// </summary>
    public static async Task<string> ForkAsync(
        string workingDirectory, string? sourceId, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var newId = SessionIds.NewId();
        await new SessionTranscriptStore(workingDirectory).SaveAsync(newId, messages, ct).ConfigureAwait(false);
        if (sourceId is not null)
        {
            await new SessionAuditStore(workingDirectory).CopyAsync(sourceId, newId, ct).ConfigureAwait(false);
        }

        return newId;
    }
}
