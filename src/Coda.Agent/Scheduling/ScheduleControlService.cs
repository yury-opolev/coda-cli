using Coda.Agent.Tools;

namespace Coda.Agent.Scheduling;

/// <summary>
/// Single facade over a session's <see cref="ScheduledTaskStore"/>,
/// <see cref="IScheduleRuntimeView"/>, and <see cref="ScheduleDefinitionParser"/> that
/// implements the <em>list / create / delete</em> control surface. One implementation shared
/// by the <c>schedule_*</c> agent tools, the serve request handlers, and the (future) TUI
/// browser so the parse→store→project flow never drifts between surfaces.
///
/// <para>Inject <paramref name="timeProvider"/> and <paramref name="localTimeZone"/> for
/// deterministic, zone-independent tests; both default to system values in production.</para>
/// </summary>
public sealed class ScheduleControlService : IScheduleControl
{
    // A null store means the session context does not support scheduling (e.g. a subagent).
    private readonly ScheduledTaskStore? store;
    private readonly Func<IScheduleRuntimeView?> runtimeViewProvider;
    private readonly TimeProvider timeProvider;
    private readonly Func<TimeZoneInfo> localTimeZone;

    /// <summary>
    /// Creates the service with an injectable runtime-view accessor and time/zone seams.
    /// </summary>
    /// <param name="store">The session-owned store, or <see langword="null"/> when scheduling
    /// is unavailable (subagent context). <see cref="Create"/> returns an error in that case.</param>
    /// <param name="runtimeViewProvider">
    /// Returns the live runtime view at call time. Use a lambda that reads the session's volatile
    /// runtime field so the service sees the live view after <c>InitializeAsync</c> completes.
    /// </param>
    /// <param name="timeProvider">Clock for <c>nowUtc</c> in create; defaults to system.</param>
    /// <param name="localTimeZone">Local zone for offset-less values; defaults to system local.</param>
    public ScheduleControlService(
        ScheduledTaskStore? store,
        Func<IScheduleRuntimeView?> runtimeViewProvider,
        TimeProvider? timeProvider = null,
        Func<TimeZoneInfo>? localTimeZone = null)
    {
        this.store = store;
        this.runtimeViewProvider = runtimeViewProvider ?? (() => null);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.localTimeZone = localTimeZone ?? (() => TimeZoneInfo.Local);
    }

    /// <summary>
    /// Creates the service with a direct (non-lazy) runtime view reference and optional time/zone
    /// seams. Convenience overload for tool usage where the view is captured at execute time.
    /// </summary>
    public ScheduleControlService(
        ScheduledTaskStore? store,
        IScheduleRuntimeView? runtimeView,
        TimeProvider? timeProvider = null,
        Func<TimeZoneInfo>? localTimeZone = null)
        : this(store, () => runtimeView, timeProvider, localTimeZone) { }

    /// <summary>
    /// Projects all definitions in the store into <see cref="ScheduledTaskReadModel"/> instances,
    /// joined with live runtime state from the runtime view. Returns an empty list when the store
    /// is null (subagent context) or empty.
    /// </summary>
    public IReadOnlyList<ScheduledTaskReadModel> List()
    {
        if (this.store is null)
        {
            return [];
        }

        var snapshot = this.store.GetSnapshot();
        if (snapshot.Items.Count == 0)
        {
            return [];
        }

        var runtime = this.runtimeViewProvider();
        var result = new List<ScheduledTaskReadModel>(snapshot.Items.Count);
        foreach (var task in snapshot.Items)
        {
            result.Add(this.ProjectTask(task, runtime));
        }

        return result;
    }

    /// <summary>
    /// Validates <paramref name="request"/> via <see cref="ScheduleDefinitionParser"/>, persists
    /// the draft when valid, and returns the projected <see cref="ScheduledTaskReadModel"/> on
    /// success. Returns a failure carrying the parser's exact error message on invalid input, or
    /// the "no store available" message when the session context does not support scheduling.
    /// </summary>
    public ScheduleCreateResult Create(ScheduleCreateRequest request)
    {
        var nowUtc = this.timeProvider.GetUtcNow();
        var zone = this.localTimeZone();

        if (!ScheduleDefinitionParser.TryParse(request, nowUtc, zone, out var draft, out var error))
        {
            return ScheduleCreateResult.Fail(error ?? "Invalid schedule request.");
        }

        if (this.store is null)
        {
            return ScheduleCreateResult.Fail(
                "No schedule store is available in this context (e.g. running as a subagent); the " +
                "task was not persisted.");
        }

        var task = this.store.Add(draft!, nowUtc);
        return ScheduleCreateResult.Ok(this.ProjectNewTask(task, zone));
    }

    /// <summary>
    /// Removes the definition with <paramref name="id"/> from the store. Returns
    /// <see langword="true"/> when the definition was found and removed, <see langword="false"/>
    /// when the id was not found or no store is available.
    /// </summary>
    public bool Delete(string id)
    {
        return this.store is not null && this.store.Remove(id);
    }

    // ── Private helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Projects a persisted task joined with the runtime state for display in <see cref="List"/>.
    /// Uses <see cref="ScheduleDisplay.FormatLocal"/> for the local-time display.
    /// </summary>
    private ScheduledTaskReadModel ProjectTask(ScheduledTask task, IScheduleRuntimeView? runtime)
    {
        ResolveRuntimeState(runtime, task.Id, out var status, out var activeTaskId);
        var nextRunLocal = ScheduleDisplay.FormatLocal(task.NextRunUtc, task.TimeZoneId, out var label);
        return new ScheduledTaskReadModel(
            task.Id,
            task.Name,
            task.Kind,
            task.Prompt,
            ScheduleDisplay.DescribeRule(task),
            task.TimeZoneId,
            task.NextRunUtc,
            nextRunLocal,
            label,
            status,
            activeTaskId,
            task.LastTerminalOutcome);
    }

    /// <summary>
    /// Projects a newly persisted task for the <see cref="Create"/> result. Mirrors the local-time
    /// display logic of <see cref="ScheduleCreateTool"/>: when the definition's zone matches the
    /// injected local zone (e.g. an offset-less <c>at</c> value), the zone object is used directly
    /// so custom test zones that are not in the system registry still produce a correct local time.
    /// </summary>
    private ScheduledTaskReadModel ProjectNewTask(ScheduledTask task, TimeZoneInfo zone)
    {
        string nextRunLocal, nextRunLocalLabel;
        if (string.Equals(task.TimeZoneId, zone.Id, StringComparison.Ordinal))
        {
            nextRunLocal = TimeZoneInfo.ConvertTime(task.NextRunUtc, zone).ToString("yyyy-MM-dd HH:mm");
            nextRunLocalLabel = task.TimeZoneId;
        }
        else
        {
            nextRunLocal = ScheduleDisplay.FormatLocal(task.NextRunUtc, task.TimeZoneId, out nextRunLocalLabel);
        }

        return new ScheduledTaskReadModel(
            task.Id,
            task.Name,
            task.Kind,
            task.Prompt,
            ScheduleDisplay.DescribeRule(task),
            task.TimeZoneId,
            task.NextRunUtc,
            nextRunLocal,
            nextRunLocalLabel,
            ScheduleRuntimeStatus.Idle,
            ActiveTaskId: null,
            LastOutcome: null);
    }

    private static void ResolveRuntimeState(
        IScheduleRuntimeView? runtime,
        string id,
        out ScheduleRuntimeStatus status,
        out string? activeTaskId)
    {
        if (runtime is not null && runtime.TryGetState(id, out var state) && state is not null)
        {
            status = state.Status;
            activeTaskId = state.ActiveTaskId;
        }
        else
        {
            status = ScheduleRuntimeStatus.Idle;
            activeTaskId = null;
        }
    }
}
