namespace Coda.Agent.Tasks;

/// <summary>Outcome of a lifecycle request (stop/steer) so tools can produce precise messages.</summary>
public enum TaskActionResult
{
    Ok,
    NotFound,
    InvalidState,
    Rejected,
}
