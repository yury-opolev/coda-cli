namespace Coda.Agent.Tasks;

/// <summary>Outcome of a lifecycle request (stop/steer) so tools can produce precise messages.</summary>
public enum TaskActionResult
{
    Ok,
    NotFound,
    InvalidState,
    Rejected,

    /// <summary>
    /// The caller is not authorized to act on the target task (it is outside the caller's own
    /// subtree). Tools map this to the same wording as <see cref="NotFound"/> so a subagent
    /// cannot probe the existence of tasks it does not own.
    /// </summary>
    Denied,
}
