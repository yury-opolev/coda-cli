namespace Coda.Agent;

/// <summary>
/// Host callback for tool permission. Called before a non-read-only tool runs;
/// the TUI renders an allow/deny prompt (same host-callback model as login).
/// </summary>
public interface IPermissionPrompt
{
    Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default);
}

/// <summary>Always allows — for non-interactive/test use.</summary>
public sealed class AllowAllPermissionPrompt : IPermissionPrompt
{
    public Task<bool> RequestAsync(ITool tool, string inputPreview, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
