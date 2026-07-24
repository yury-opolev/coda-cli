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
    /// Both the transcript write and the audit copy are best-effort — a disk fault on either is swallowed
    /// so the caller always gets a usable id back; the live session lazily re-persists its transcript on
    /// its first turn if the initial write failed. Returns the new id.
    /// </summary>
    public static async Task<string> ForkAsync(
        string workingDirectory, string? sourceId, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var source = sourceId is null
            ? null
            : await new SessionTranscriptStore(workingDirectory).LoadSessionAsync(sourceId, ct).ConfigureAwait(false);
        return await ForkAsync(
            workingDirectory,
            sourceId,
            messages,
            source?.Metadata ?? SessionMetadata.Empty,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forks <paramref name="sourceId"/> into a fresh session id under <paramref name="workingDirectory"/>
    /// with the supplied live-session <paramref name="metadata"/>.
    /// </summary>
    public static async Task<string> ForkAsync(
        string workingDirectory,
        string? sourceId,
        IReadOnlyList<ChatMessage> messages,
        SessionMetadata metadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(metadata);

        var newId = SessionIds.NewId();
        try
        {
            await new SessionTranscriptStore(workingDirectory).SaveAsync(newId, messages, metadata, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort: the live session lazily re-persists its transcript on its first turn.
        }

        if (sourceId is not null)
        {
            await new SessionAuditStore(workingDirectory).CopyAsync(sourceId, newId, ct).ConfigureAwait(false);
        }

        return newId;
    }
}
