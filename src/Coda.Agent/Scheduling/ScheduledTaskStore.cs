using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Coda.Agent.Scheduling;

/// <summary>
/// A point-in-time, immutable view of the store: the monotonic <paramref name="Version"/> observed
/// together with a copied, safe-to-retain list of <paramref name="Items"/>.
/// </summary>
/// <param name="Version">The store version at capture time. Pass to
/// <see cref="ScheduledTaskStore.WaitForChangeAsync"/> to await the next change.</param>
/// <param name="Items">A copied snapshot of the scheduled tasks; mutating it cannot affect the store.</param>
public sealed record ScheduledTaskStoreSnapshot(long Version, IReadOnlyList<ScheduledTask> Items);

/// <summary>
/// Thread-safe store for schema-v2 scheduled definitions with optional durable JSON persistence.
///
/// <para>Loading recovers each array element independently: a malformed or invariant-violating
/// record is skipped and logged without discarding its valid neighbours, and a wholly invalid or
/// non-array document loads empty. Legacy (schema v1) records are migrated in memory; the file is
/// only rewritten by the next successful mutation.</para>
///
/// <para>Every successful mutation advances a monotonic <see cref="ScheduledTaskStoreSnapshot.Version"/>
/// and wakes waiters registered through <see cref="WaitForChangeAsync"/>. Persistence is atomic
/// (unique sibling temp file + <see cref="File.Move(string, string, bool)"/>) and best-effort: a
/// failed write leaves the previous on-disk document intact while the in-memory mutation still
/// succeeds.</para>
/// </summary>
public sealed partial class ScheduledTaskStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    [LoggerMessage(Level = LogLevel.Debug, Message = "scheduled-task persistence failed (best-effort); the store continues in-memory and this mutation is not durable: path={path}")]
    private static partial void LogPersistFailed(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "scheduled-task record skipped during load (invalid or unreadable); other records are unaffected: reason={reason}")]
    private static partial void LogRecordSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "scheduled-task store document was not a loadable array; starting empty: path={path} reason={reason}")]
    private static partial void LogDocumentNotLoadable(ILogger logger, string path, string reason);

    private readonly object gate = new();
    private readonly string? persistPath;

    // Diagnostics raised before a Logger is wired (see the Logger property) are buffered here and
    // flushed exactly once when a real logger is assigned, so load-time skips are never lost.
    private readonly object diagnosticsGate = new();
    private readonly List<Action<ILogger>> pendingDiagnostics = [];
    private ILogger? logger;

    private List<ScheduledTask> items = [];
    private long version;
    private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates an in-memory store with no persistence.</summary>
    public ScheduledTaskStore()
    {
    }

    /// <summary>
    /// Creates a store backed by <paramref name="persistPath"/>. Loads existing records on
    /// construction and persists after every successful mutation.
    /// </summary>
    public ScheduledTaskStore(string? persistPath, ILogger? logger = null)
    {
        this.persistPath = persistPath;

        // Set the backing field directly: nothing is buffered yet, and if a logger is supplied the
        // load below logs straight through it rather than via the deferred buffer.
        this.logger = logger;

        if (!string.IsNullOrWhiteSpace(persistPath))
        {
            this.Load();
        }
    }

    /// <summary>
    /// Optional logger for load diagnostics and best-effort persistence failures. Settable so a host
    /// that builds its logger factory after this store (e.g. <c>CodaSession</c>, which builds
    /// telemetry last) can still wire real logging; assigning it flushes any diagnostics buffered
    /// during construction. Left null in tests/in-memory use.
    /// </summary>
    public ILogger? Logger
    {
        get
        {
            lock (this.diagnosticsGate)
            {
                return this.logger;
            }
        }

        set
        {
            List<Action<ILogger>>? flush = null;
            lock (this.diagnosticsGate)
            {
                this.logger = value;
                if (value is not null && this.pendingDiagnostics.Count > 0)
                {
                    flush = [.. this.pendingDiagnostics];
                    this.pendingDiagnostics.Clear();
                }
            }

            if (flush is not null && value is not null)
            {
                foreach (var emit in flush)
                {
                    emit(value);
                }
            }
        }
    }

    /// <summary>
    /// Compatibility snapshot access: a copied list of the current tasks that cannot be mutated to
    /// affect the store. Prefer <see cref="GetSnapshot"/>, which also returns the observed version.
    /// </summary>
    public IReadOnlyList<ScheduledTask> Items
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.items];
            }
        }
    }

    /// <summary>
    /// Returns a linearizable snapshot: the current version and a copied task list. Pair the
    /// returned <see cref="ScheduledTaskStoreSnapshot.Version"/> with <see cref="WaitForChangeAsync"/>
    /// to await the next change without a missed wakeup.
    /// </summary>
    public ScheduledTaskStoreSnapshot GetSnapshot()
    {
        lock (this.gate)
        {
            return new ScheduledTaskStoreSnapshot(this.version, [.. this.items]);
        }
    }

    /// <summary>
    /// Adds a new schema-v2 definition built from <paramref name="draft"/>, assigning a short unique
    /// id and stamping <paramref name="nowUtc"/> as the created/updated time.
    /// </summary>
    public ScheduledTask Add(ScheduleDefinitionDraft draft, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var task = new ScheduledTask(
            ScheduledTask.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N")[..12],
            draft.Name,
            draft.Kind,
            draft.Prompt,
            draft.Interval,
            draft.AtUtc,
            draft.Cron,
            draft.TimeZoneId,
            draft.NextRunUtc,
            CreatedAtUtc: nowUtc,
            UpdatedAtUtc: nowUtc,
            LastTerminalOutcome: null);

        return this.AddCore(task);
    }

    /// <summary>
    /// TEMPORARY Task-3 compatibility shim: the legacy cron/recurring create path. Maps onto the
    /// versioned record so existing callers/tests compile until <c>schedule_create</c> is redesigned.
    /// The canonical API is <see cref="Add(ScheduleDefinitionDraft, DateTimeOffset)"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="cron"/> is invalid.</exception>
    public ScheduledTask Add(string cron, string prompt, bool recurring, DateTime nowUtc)
    {
        if (!CronExpression.TryParse(cron, out var cronExpr, out var error))
        {
            throw new ArgumentException($"Invalid cron expression: {error}", nameof(cron));
        }

        var nextRun = new DateTimeOffset(cronExpr!.NextOccurrence(nowUtc));
        var createdAt = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));

        var task = new ScheduledTask(
            ScheduledTask.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N")[..12],
            Name: null,
            Kind: recurring ? ScheduleKind.Cron : ScheduleKind.At,
            Prompt: prompt,
            Interval: null,
            AtUtc: recurring ? null : nextRun,
            Cron: recurring ? cron : null,
            TimeZoneId: ScheduleTimeZones.FixedOffsetId(TimeSpan.Zero),
            NextRunUtc: nextRun,
            CreatedAtUtc: createdAt,
            UpdatedAtUtc: createdAt,
            LastTerminalOutcome: null);

        return this.AddCore(task);
    }

    /// <summary>Removes the task with the given id. Returns <c>true</c> if found and removed.</summary>
    public bool Remove(string id)
    {
        TaskCompletionSource previous;
        lock (this.gate)
        {
            var idx = this.items.FindIndex(t => t.Id == id);
            if (idx < 0)
            {
                return false;
            }

            this.items.RemoveAt(idx);
            previous = this.CommitLocked();
        }

        previous.TrySetResult();
        return true;
    }

    /// <summary>
    /// Replaces the task sharing <paramref name="updated"/>'s id. Returns <c>true</c> if a matching
    /// task existed and was replaced; an unknown id is a no-op that does not change the version.
    /// </summary>
    public bool Replace(ScheduledTask updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        TaskCompletionSource previous;
        lock (this.gate)
        {
            var idx = this.items.FindIndex(t => t.Id == updated.Id);
            if (idx < 0)
            {
                return false;
            }

            this.items[idx] = updated;
            previous = this.CommitLocked();
        }

        previous.TrySetResult();
        return true;
    }

    /// <summary>
    /// Completes when the store advances past <paramref name="observedVersion"/>. If the store has
    /// already changed, returns a completed task immediately; otherwise awaits the next successful
    /// mutation. Captures the wait target under the same lock <see cref="GetSnapshot"/> uses, so a
    /// mutation landing between snapshot and wait cannot be missed.
    /// </summary>
    public Task WaitForChangeAsync(long observedVersion, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        Task signalTask;
        lock (this.gate)
        {
            if (this.version != observedVersion)
            {
                return Task.CompletedTask;
            }

            signalTask = this.signal.Task;
        }

        return cancellationToken.CanBeCanceled
            ? AwaitWithCancellation(signalTask, cancellationToken)
            : signalTask;
    }

    private static async Task AwaitWithCancellation(Task signalTask, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), cancelled))
        {
            var completed = await Task.WhenAny(signalTask, cancelled.Task).ConfigureAwait(false);
            if (completed == signalTask)
            {
                await signalTask.ConfigureAwait(false);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private ScheduledTask AddCore(ScheduledTask task)
    {
        TaskCompletionSource previous;
        lock (this.gate)
        {
            this.items.Add(task);
            previous = this.CommitLocked();
        }

        previous.TrySetResult();
        return task;
    }

    /// <summary>
    /// Called while holding <see cref="gate"/> after an in-memory mutation. Advances the version,
    /// persists best-effort, then swaps in a fresh signal and returns the previous one for the
    /// caller to complete after releasing the lock (per the version/signal ordering contract).
    /// </summary>
    private TaskCompletionSource CommitLocked()
    {
        this.version++;
        this.PersistLocked(this.items);

        var previous = this.signal;
        this.signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return previous;
    }

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(this.persistPath) || !File.Exists(this.persistPath))
        {
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(this.persistPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this.Report(l => LogDocumentNotLoadable(l, this.persistPath!, $"unreadable: {ex.GetType().Name}"));
            return;
        }

        this.items = this.DeserializeItems(json);
    }

    /// <summary>
    /// Legacy on-disk shape (schema v1): <c>Id,Cron,Prompt,Recurring,NextRunUtc</c>. Retained only so
    /// <see cref="Load"/> can migrate old records into the versioned <see cref="ScheduledTask"/>.
    /// </summary>
    private sealed record LegacyScheduledTask(
        string Id,
        string Cron,
        string Prompt,
        bool Recurring,
        DateTimeOffset NextRunUtc);

    /// <summary>
    /// Parses the document root with <see cref="JsonDocument"/> and recovers each array element
    /// independently. Invalid elements are skipped and logged; a wholly invalid or non-array document
    /// loads empty and logs. The <see cref="JsonException"/> catches are the narrowly justified JSON
    /// boundary — no broader exceptions are swallowed.
    /// </summary>
    private List<ScheduledTask> DeserializeItems(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            this.Report(l => LogDocumentNotLoadable(l, this.persistPath ?? "<memory>", $"invalid JSON: {ex.Message}"));
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                this.Report(l => LogDocumentNotLoadable(l, this.persistPath ?? "<memory>", "root is not an array"));
                return [];
            }

            var result = new List<ScheduledTask>();
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var loaded = this.TryLoadElement(element, index);
                if (loaded is not null)
                {
                    result.Add(loaded);
                }

                index++;
            }

            return result;
        }
    }

    private ScheduledTask? TryLoadElement(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            this.Report(l => LogRecordSkipped(l, $"element[{index}] is not a JSON object"));
            return null;
        }

        if (HasPropertyIgnoreCase(element, "schemaVersion"))
        {
            return this.TryLoadV2(element, index);
        }

        if (HasPropertyIgnoreCase(element, "recurring"))
        {
            return this.TryLoadLegacy(element, index);
        }

        this.Report(l => LogRecordSkipped(l, $"element[{index}] is neither a versioned nor a legacy record"));
        return null;
    }

    private ScheduledTask? TryLoadV2(JsonElement element, int index)
    {
        ScheduledTask? task;
        try
        {
            task = element.Deserialize<ScheduledTask>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            this.Report(l => LogRecordSkipped(l, $"element[{index}] failed to deserialize: {ex.Message}"));
            return null;
        }

        if (task is null)
        {
            this.Report(l => LogRecordSkipped(l, $"element[{index}] deserialized to null"));
            return null;
        }

        if (!IsValid(task, out var reason))
        {
            this.Report(l => LogRecordSkipped(l, $"element[{index}] invalid v2 record: {reason}"));
            return null;
        }

        return task;
    }

    private ScheduledTask? TryLoadLegacy(JsonElement element, int index)
    {
        LegacyScheduledTask? legacy;
        try
        {
            legacy = element.Deserialize<LegacyScheduledTask>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            this.Report(l => LogRecordSkipped(l, $"element[{index}] failed legacy deserialize: {ex.Message}"));
            return null;
        }

        if (legacy is null || string.IsNullOrWhiteSpace(legacy.Id) || string.IsNullOrWhiteSpace(legacy.Prompt))
        {
            this.Report(l => LogRecordSkipped(l, $"element[{index}] invalid legacy record"));
            return null;
        }

        return MigrateLegacy(legacy);
    }

    /// <summary>
    /// Validates schema-v2 invariants: a supported schema version, non-blank id/prompt/timezone, and
    /// the rule field required by the record's <see cref="ScheduleKind"/>.
    /// </summary>
    private static bool IsValid(ScheduledTask task, out string reason)
    {
        if (task.SchemaVersion != ScheduledTask.CurrentSchemaVersion)
        {
            reason = $"unsupported schema version {task.SchemaVersion}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(task.Id))
        {
            reason = "blank id";
            return false;
        }

        if (string.IsNullOrWhiteSpace(task.Prompt))
        {
            reason = "blank prompt";
            return false;
        }

        if (string.IsNullOrWhiteSpace(task.TimeZoneId))
        {
            reason = "blank timezone";
            return false;
        }

        switch (task.Kind)
        {
            case ScheduleKind.Interval when task.Interval is not { } interval || interval <= TimeSpan.Zero:
                reason = "interval kind requires a positive interval";
                return false;

            case ScheduleKind.At when task.AtUtc is null:
                reason = "at kind requires an atUtc instant";
                return false;

            case ScheduleKind.Cron when string.IsNullOrWhiteSpace(task.Cron):
                reason = "cron kind requires a cron expression";
                return false;

            case ScheduleKind.Interval:
            case ScheduleKind.At:
            case ScheduleKind.Cron:
                reason = string.Empty;
                return true;

            default:
                reason = $"unknown kind {task.Kind}";
                return false;
        }
    }

    /// <summary>
    /// Maps a legacy (schema v1) record onto the versioned <see cref="ScheduledTask"/>: recurring
    /// entries become UTC <see cref="ScheduleKind.Cron"/> schedules; one-shot entries become
    /// <see cref="ScheduleKind.At"/> schedules whose instant is the persisted next-run time.
    /// </summary>
    private static ScheduledTask MigrateLegacy(LegacyScheduledTask legacy)
    {
        var nextRun = legacy.NextRunUtc;
        return new ScheduledTask(
            ScheduledTask.CurrentSchemaVersion,
            legacy.Id,
            Name: null,
            Kind: legacy.Recurring ? ScheduleKind.Cron : ScheduleKind.At,
            Prompt: legacy.Prompt,
            Interval: null,
            AtUtc: legacy.Recurring ? null : nextRun,
            Cron: legacy.Recurring ? legacy.Cron : null,
            TimeZoneId: ScheduleTimeZones.FixedOffsetId(TimeSpan.Zero),
            NextRunUtc: nextRun,
            CreatedAtUtc: nextRun,
            UpdatedAtUtc: nextRun,
            LastTerminalOutcome: null);
    }

    /// <summary>
    /// Best-effort atomic persistence, called while holding <see cref="gate"/>. Serializes to a
    /// unique sibling temp file, restricts it to the owning user on non-Windows, then replaces the
    /// target with <see cref="File.Move(string, string, bool)"/>. On failure the previous on-disk
    /// document is left intact and a structured debug entry is emitted; the temp file is always
    /// cleaned up. Exception handling is scoped to the file/JSON boundary.
    /// </summary>
    private void PersistLocked(IReadOnlyList<ScheduledTask> snapshot)
    {
        if (string.IsNullOrWhiteSpace(this.persistPath))
        {
            return;
        }

        string? tempPath = null;
        try
        {
            var dir = Path.GetDirectoryName(this.persistPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var directory = string.IsNullOrWhiteSpace(dir) ? "." : dir;
            tempPath = Path.Combine(directory, $".{Path.GetFileName(this.persistPath)}.{Guid.NewGuid():N}.tmp");

            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(tempPath, json);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tempPath, this.persistPath, overwrite: true);
            tempPath = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or JsonException)
        {
            this.Report(l => LogPersistFailed(l, this.persistPath!, ex));
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup; a leaked temp file is harmless and never loaded.
                }
            }
        }
    }

    /// <summary>
    /// Emits a diagnostic through the current logger, or buffers it for flush once a logger is wired
    /// (the store is constructed before <c>CodaSession</c> builds its logger factory).
    /// </summary>
    private void Report(Action<ILogger> emit)
    {
        ILogger? current;
        lock (this.diagnosticsGate)
        {
            current = this.logger;
            if (current is null)
            {
                this.pendingDiagnostics.Add(emit);
                return;
            }
        }

        emit(current);
    }

    private static bool HasPropertyIgnoreCase(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
