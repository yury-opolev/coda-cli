namespace Coda.Agent;

/// <summary>
/// Host callback that lets the agent present a proposed plan to the user for
/// approval before switching out of plan mode.
/// Implementations are UI-specific (TUI, tests). Null means headless — no user
/// available to approve.
/// </summary>
public interface IPlanApprover
{
    /// <summary>
    /// Presents the proposed <paramref name="plan"/> to the user and returns
    /// <see langword="true"/> if the user approved it.
    /// The implementation is responsible for any host-side mode change on approval.
    /// </summary>
    Task<bool> ApproveAsync(string plan, CancellationToken cancellationToken = default);
}
