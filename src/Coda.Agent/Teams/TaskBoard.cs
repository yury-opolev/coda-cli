using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coda.Agent.Teams;

public sealed class TaskBoard
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string teamsBaseDir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();

    public TaskBoard(string teamsBaseDir)
    {
        this.teamsBaseDir = teamsBaseDir;
    }

    private string GetTasksPath(string team) =>
        Path.Combine(this.teamsBaseDir, AgentId.SanitizeName(team), "tasks.json");

    private SemaphoreSlim GetLock(string path) =>
        this.locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    public async Task<TeamTask> CreateAsync(
        string team,
        string subject,
        string? description,
        IReadOnlyList<string>? blockedBy,
        CancellationToken ct = default)
    {
        var path = this.GetTasksPath(team);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var sem = this.GetLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tasks = await ReadRawAsync(path).ConfigureAwait(false);

            var maxNumber = 0;
            foreach (var existing in tasks)
            {
                if (existing.Id.StartsWith("t", StringComparison.Ordinal)
                    && int.TryParse(existing.Id[1..], out var num)
                    && num > maxNumber)
                {
                    maxNumber = num;
                }
            }

            var id = $"t{maxNumber + 1}";
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var task = new TeamTask(
                Id: id,
                Subject: subject,
                Description: description,
                Status: TeamTaskStatus.Pending,
                Owner: null,
                BlockedBy: blockedBy ?? [],
                CreatedAt: now);

            tasks.Add(task);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(tasks, jsonOptions), ct).ConfigureAwait(false);
            return task;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<IReadOnlyList<TeamTask>> ListAsync(string team, CancellationToken ct = default)
    {
        var path = this.GetTasksPath(team);
        var sem = this.GetLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadRawAsync(path).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<TeamTask?> GetAsync(string team, string id, CancellationToken ct = default)
    {
        var tasks = await this.ListAsync(team, ct).ConfigureAwait(false);
        return tasks.FirstOrDefault(t => t.Id == id);
    }

    public async Task<bool> UpdateAsync(
        string team,
        string id,
        TeamTaskPatch patch,
        CancellationToken ct = default)
    {
        var path = this.GetTasksPath(team);
        var sem = this.GetLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tasks = await ReadRawAsync(path).ConfigureAwait(false);
            var index = tasks.FindIndex(t => t.Id == id);
            if (index == -1)
            {
                return false;
            }

            var task = tasks[index];
            var status = patch.Status ?? task.Status;
            var description = patch.Description ?? task.Description;
            var blockedBy = patch.BlockedBy ?? task.BlockedBy;
            string? owner;
            if (patch.ClearOwner)
            {
                owner = null;
            }
            else
            {
                owner = patch.Owner ?? task.Owner;
            }

            tasks[index] = task with
            {
                Status = status,
                Description = description,
                BlockedBy = blockedBy,
                Owner = owner,
            };

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(tasks, jsonOptions), ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<(bool Ok, string Reason)> ClaimAsync(
        string team,
        string id,
        string owner,
        CancellationToken ct = default)
    {
        var path = this.GetTasksPath(team);
        var sem = this.GetLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tasks = await ReadRawAsync(path).ConfigureAwait(false);
            var index = tasks.FindIndex(t => t.Id == id);
            if (index == -1)
            {
                return (false, "task not found");
            }

            var task = tasks[index];

            if (task.Status != TeamTaskStatus.Pending)
            {
                return (false, "not pending");
            }

            if (task.Owner is not null)
            {
                return (false, "already owned");
            }

            var taskById = tasks.ToDictionary(t => t.Id);
            foreach (var blockerId in task.BlockedBy)
            {
                if (!taskById.TryGetValue(blockerId, out var blocker) || blocker.Status != TeamTaskStatus.Completed)
                {
                    return (false, "blocked by incomplete tasks");
                }
            }

            tasks[index] = task with
            {
                Owner = owner,
                Status = TeamTaskStatus.InProgress,
            };

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(tasks, jsonOptions), ct).ConfigureAwait(false);
            return (true, string.Empty);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<bool> StopAsync(string team, string id, CancellationToken ct = default)
    {
        var path = this.GetTasksPath(team);
        var sem = this.GetLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tasks = await ReadRawAsync(path).ConfigureAwait(false);
            var index = tasks.FindIndex(t => t.Id == id);
            if (index == -1)
            {
                return false;
            }

            tasks[index] = tasks[index] with { Status = TeamTaskStatus.Cancelled };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(tasks, jsonOptions), ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            sem.Release();
        }
    }

    public static TeamTask? FindAvailable(IReadOnlyList<TeamTask> tasks)
    {
        var unresolvedTaskIds = new HashSet<string>(
            tasks.Where(t => t.Status != TeamTaskStatus.Completed).Select(t => t.Id));

        return tasks.FirstOrDefault(task =>
        {
            if (task.Status != TeamTaskStatus.Pending)
            {
                return false;
            }

            if (task.Owner is not null)
            {
                return false;
            }

            return task.BlockedBy.All(blockerId => !unresolvedTaskIds.Contains(blockerId));
        });
    }

    private static async Task<List<TeamTask>> ReadRawAsync(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<List<TeamTask>>(json, jsonOptions);
            return result ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
