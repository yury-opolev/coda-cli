using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Coda.Agent.Scheduling;

/// <summary>
/// Thread-safe store for scheduled tasks. Optionally persists to a JSON file.
/// Corrupted or missing files are silently treated as an empty store.
/// </summary>
public sealed partial class ScheduledTaskStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [LoggerMessage(Level = LogLevel.Debug, Message = "scheduled-task persistence failed (best-effort); the store continues in-memory and this mutation is not durable: path={path}")]
    private static partial void LogPersistFailed(ILogger logger, string path, Exception ex);

    private readonly object gate = new();
    private readonly string? persistPath;
    private List<ScheduledTask> items = [];

    /// <summary>
    /// Optional logger for best-effort persistence failures. Settable so a host that builds its
    /// logger factory after this store (e.g. <c>CodaSession</c>, which builds telemetry last) can
    /// still wire real logging; left null in tests/in-memory use.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>Creates an in-memory store with no persistence.</summary>
    public ScheduledTaskStore()
    {
    }

    /// <summary>
    /// Creates a store backed by <paramref name="persistPath"/>.
    /// Loads existing tasks on construction; saves after every mutation.
    /// </summary>
    public ScheduledTaskStore(string? persistPath, ILogger? logger = null)
    {
        this.persistPath = persistPath;
        this.Logger = logger;
        if (!string.IsNullOrWhiteSpace(persistPath))
        {
            this.Load();
        }
    }

    /// <summary>Snapshot of current tasks.</summary>
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
    /// Adds a new scheduled task. Parses the cron expression, computes
    /// <see cref="ScheduledTask.NextRunUtc"/> as the first occurrence strictly after
    /// <paramref name="nowUtc"/>, and assigns a short unique Id.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="cron"/> is invalid.</exception>
    public ScheduledTask Add(string cron, string prompt, bool recurring, DateTime nowUtc)
    {
        if (!CronExpression.TryParse(cron, out var cronExpr, out var error))
        {
            throw new ArgumentException($"Invalid cron expression: {error}", nameof(cron));
        }

        var nextRun = cronExpr!.NextOccurrence(nowUtc);
        var id = Guid.NewGuid().ToString("N")[..12];
        var task = new ScheduledTask(id, cron, prompt, recurring, nextRun);

        lock (this.gate)
        {
            this.items.Add(task);
        }

        this.Save();
        return task;
    }

    /// <summary>Removes the task with the given Id. Returns <c>true</c> if found and removed.</summary>
    public bool Remove(string id)
    {
        bool removed;
        lock (this.gate)
        {
            var before = this.items.Count;
            this.items = [.. this.items.Where(t => t.Id != id)];
            removed = this.items.Count < before;
        }

        if (removed)
        {
            this.Save();
        }

        return removed;
    }

    /// <summary>Replaces the task with the same Id as <paramref name="updated"/>. No-op if not found.</summary>
    public void Replace(ScheduledTask updated)
    {
        ArgumentNullException.ThrowIfNull(updated);

        lock (this.gate)
        {
            var idx = this.items.FindIndex(t => t.Id == updated.Id);
            if (idx < 0)
            {
                return;
            }

            this.items[idx] = updated;
        }

        this.Save();
    }

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(this.persistPath) || !File.Exists(this.persistPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(this.persistPath);
            var loaded = JsonSerializer.Deserialize<List<ScheduledTask>>(json, SerializerOptions);
            if (loaded is not null)
            {
                this.items = loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable file → start empty.
            this.items = [];
        }
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(this.persistPath))
        {
            return;
        }

        try
        {
            List<ScheduledTask> snapshot;
            lock (this.gate)
            {
                snapshot = [.. this.items];
            }

            var dir = Path.GetDirectoryName(this.persistPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(this.persistPath, json);
        }
        catch (Exception ex)
        {
            // Persistence failures are non-fatal; the store continues in-memory.
            if (this.Logger is not null)
            {
                LogPersistFailed(this.Logger, this.persistPath!, ex);
            }
        }
    }
}
