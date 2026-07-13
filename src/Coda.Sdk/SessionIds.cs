namespace Coda.Sdk;

/// <summary>
/// Shared validity rule for a session id used as a file name, so the transcript and audit stores
/// (and anything else keyed by session id) apply exactly the same guard.
/// </summary>
internal static class SessionIds
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="sessionId"/> is safe to use as a file name:
    /// non-empty, contains no invalid file-name characters, and contains no path-separator
    /// components (guards against traversal like "../../secret").
    /// </summary>
    public static bool IsValid(string sessionId)
        => !string.IsNullOrEmpty(sessionId)
           && sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && Path.GetFileName(sessionId) == sessionId;
}
