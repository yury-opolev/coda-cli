using System.Collections.Concurrent;
using System.Text.Json;

namespace Coda.Agent.Teams;

public sealed class TeamStore
{
    private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string teamsBaseDir;

    /// <summary>
    /// Per-config-file monitors used to serialize concurrent read-modify-write operations.
    /// Each team gets its own lock object so distinct teams never contend with each other.
    /// </summary>
    private readonly ConcurrentDictionary<string, object> configLocks = new();

    public TeamStore(string teamsBaseDir)
    {
        this.teamsBaseDir = teamsBaseDir;
    }

    private string TeamDir(string team) =>
        Path.Combine(this.teamsBaseDir, AgentId.SanitizeName(team));

    private string ConfigPath(string team) =>
        Path.Combine(this.TeamDir(team), "config.json");

    public static bool IsValidTeamName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name == ".." || name == ".")
        {
            return false;
        }

        if (name.Contains('/') || name.Contains('\\'))
        {
            return false;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var ch in name)
        {
            if (Array.IndexOf(invalidChars, ch) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    public TeamFile? Read(string team)
    {
        var configPath = this.ConfigPath(team);

        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<TeamFile>(json, jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Write(string team, TeamFile file)
    {
        if (!IsValidTeamName(team))
        {
            throw new ArgumentException($"Invalid team name: '{team}'", nameof(team));
        }

        var dir = this.TeamDir(team);
        Directory.CreateDirectory(dir);

        var configPath = this.ConfigPath(team);
        var fileLock = this.configLocks.GetOrAdd(configPath, _ => new object());
        lock (fileLock)
        {
            var json = JsonSerializer.Serialize(file, jsonOptions);
            File.WriteAllText(configPath, json);
        }
    }

    public bool AddMember(string team, TeamMember member)
    {
        var configPath = this.ConfigPath(team);
        var fileLock = this.configLocks.GetOrAdd(configPath, _ => new object());
        lock (fileLock)
        {
            var file = this.Read(team);

            if (file is null)
            {
                return false;
            }

            var members = file.Members
                .Where(m => m.AgentId != member.AgentId)
                .Append(member)
                .ToList();

            this.Write(team, file with { Members = members });
            return true;
        }
    }

    public bool RemoveMemberByAgentId(string team, string agentId)
    {
        var configPath = this.ConfigPath(team);
        var fileLock = this.configLocks.GetOrAdd(configPath, _ => new object());
        lock (fileLock)
        {
            var file = this.Read(team);

            if (file is null)
            {
                return false;
            }

            var original = file.Members;
            var updated = original.Where(m => m.AgentId != agentId).ToList();

            if (updated.Count == original.Count)
            {
                return false;
            }

            this.Write(team, file with { Members = updated });
            return true;
        }
    }

    public bool RemoveMemberByName(string team, string name)
    {
        var configPath = this.ConfigPath(team);
        var fileLock = this.configLocks.GetOrAdd(configPath, _ => new object());
        lock (fileLock)
        {
            var file = this.Read(team);

            if (file is null)
            {
                return false;
            }

            var original = file.Members;
            var updated = original.Where(m => m.Name != name).ToList();

            if (updated.Count == original.Count)
            {
                return false;
            }

            this.Write(team, file with { Members = updated });
            return true;
        }
    }

    public bool SetMemberActive(string team, string name, bool isActive)
    {
        var configPath = this.ConfigPath(team);
        var fileLock = this.configLocks.GetOrAdd(configPath, _ => new object());
        lock (fileLock)
        {
            var file = this.Read(team);

            if (file is null)
            {
                return false;
            }

            var member = file.Members.FirstOrDefault(m => m.Name == name);

            if (member is null)
            {
                return false;
            }

            var updatedMembers = file.Members
                .Select(m => m.Name == name ? m with { IsActive = isActive } : m)
                .ToList();

            this.Write(team, file with { Members = updatedMembers });
            return true;
        }
    }

    public IReadOnlyList<string> ListTeams()
    {
        if (!Directory.Exists(this.teamsBaseDir))
        {
            return [];
        }

        var result = new List<string>();

        foreach (var dir in Directory.GetDirectories(this.teamsBaseDir))
        {
            var configPath = Path.Combine(dir, "config.json");

            if (!File.Exists(configPath))
            {
                continue;
            }

            var file = this.Read(Path.GetFileName(dir));

            if (file is not null)
            {
                result.Add(file.Name);
            }
        }

        return result;
    }

    public bool Delete(string team)
    {
        var dir = this.TeamDir(team);

        if (!Directory.Exists(dir))
        {
            return false;
        }

        Directory.Delete(dir, recursive: true);
        return true;
    }
}
