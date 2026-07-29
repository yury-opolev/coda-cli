namespace Coda.Agent.Hooks;

/// <summary>
/// Stores and queries trust decisions for project-scoped hooks.
/// Keyed by <c>(projectPath, hookContentHash)</c> so that editing a trusted hook's
/// command or URL re-prompts rather than inheriting prior trust.
/// </summary>
public interface IHookTrustStore
{
    /// <summary>
    /// Returns <see langword="true"/> when the hook identified by <paramref name="hookHash"/>
    /// has been explicitly trusted for <paramref name="projectPath"/>.
    /// </summary>
    bool IsTrusted(string projectPath, string hookHash);

    /// <summary>Records that the hook identified by <paramref name="hookHash"/> is trusted for <paramref name="projectPath"/>.</summary>
    void Trust(string projectPath, string hookHash);

    /// <summary>Removes the trust decision for <paramref name="hookHash"/> in <paramref name="projectPath"/>.</summary>
    void Revoke(string projectPath, string hookHash);
}
