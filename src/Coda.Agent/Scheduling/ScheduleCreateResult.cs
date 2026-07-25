namespace Coda.Agent.Scheduling;

/// <summary>
/// Result of <see cref="ScheduleControlService.Create"/>. Either a success carrying the newly
/// created definition as a projected <see cref="ScheduledTaskReadModel"/>, or a failure carrying
/// the exact validation or context-error message from the parser or the store check.
/// </summary>
public sealed class ScheduleCreateResult
{
    /// <summary>Whether the create succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// The projected read model of the newly persisted definition on success; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public ScheduledTaskReadModel? Task { get; }

    /// <summary>The failure reason on error; otherwise <see langword="null"/>.</summary>
    public string? Error { get; }

    private ScheduleCreateResult(bool isSuccess, ScheduledTaskReadModel? task, string? error)
    {
        IsSuccess = isSuccess;
        Task = task;
        Error = error;
    }

    /// <summary>Builds a successful result for the given read model.</summary>
    public static ScheduleCreateResult Ok(ScheduledTaskReadModel task) => new(true, task, null);

    /// <summary>Builds a failure result with the given error message.</summary>
    public static ScheduleCreateResult Fail(string error) => new(false, null, error);
}
