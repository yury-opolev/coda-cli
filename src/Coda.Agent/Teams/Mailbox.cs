using System.Collections.Concurrent;
using System.Text.Json;

namespace Coda.Agent.Teams;

public sealed class Mailbox
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    // Per-inbox-path lock. STATIC so it serializes read-modify-write across ALL Mailbox instances
    // in the process (a teammate runner and a tool ToolContext each construct their own Mailbox over
    // the same teams dir); a per-instance lock let their writes to the same file race.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();

    private readonly string teamsBaseDir;

    public Mailbox(string teamsBaseDir)
    {
        this.teamsBaseDir = teamsBaseDir;
    }

    private string GetInboxPath(string agent, string team) =>
        Path.Combine(
            this.teamsBaseDir,
            AgentId.SanitizeName(team),
            "inboxes",
            AgentId.SanitizeName(agent) + ".json");

    private static SemaphoreSlim GetLock(string path) =>
        locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

    public async Task WriteAsync(string recipient, string team, TeammateMessage message, CancellationToken ct = default)
    {
        var path = this.GetInboxPath(recipient, team);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var sem = GetLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var messages = await ReadRawAsync(path).ConfigureAwait(false);
            messages.Add(message with { Read = false });
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(messages, jsonOptions), ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task<IReadOnlyList<TeammateMessage>> ReadAsync(string agent, string team, CancellationToken ct = default)
    {
        var path = this.GetInboxPath(agent, team);
        var sem = GetLock(path);
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

    public async Task<IReadOnlyList<TeammateMessage>> ReadUnreadAsync(string agent, string team, CancellationToken ct = default)
    {
        var all = await this.ReadAsync(agent, team, ct).ConfigureAwait(false);
        return all.Where(m => !m.Read).ToList();
    }

    public async Task MarkReadByIndexAsync(string agent, string team, int index, CancellationToken ct = default)
    {
        var path = this.GetInboxPath(agent, team);
        var sem = GetLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var messages = await ReadRawAsync(path).ConfigureAwait(false);
            if (index < 0 || index >= messages.Count)
            {
                return;
            }

            if (messages[index].Read)
            {
                return;
            }

            messages[index] = messages[index] with { Read = true };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(messages, jsonOptions), ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task MarkAllReadAsync(string agent, string team, CancellationToken ct = default)
    {
        var path = this.GetInboxPath(agent, team);
        var sem = GetLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var messages = await ReadRawAsync(path).ConfigureAwait(false);
            if (messages.Count == 0)
            {
                return;
            }

            for (var i = 0; i < messages.Count; i++)
            {
                messages[i] = messages[i] with { Read = true };
            }

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(messages, jsonOptions), ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    public async Task ClearAsync(string agent, string team, CancellationToken ct = default)
    {
        var path = this.GetInboxPath(agent, team);
        if (!File.Exists(path))
        {
            return;
        }

        var sem = GetLock(path);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(Array.Empty<TeammateMessage>(), jsonOptions), ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    private static async Task<List<TeammateMessage>> ReadRawAsync(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<List<TeammateMessage>>(json, jsonOptions);
            return result ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
