using System.Collections.Concurrent;

namespace Coda.Agent.Teams;

/// <summary>
/// Owns the lifecycle of a single team: team store, mailbox, task board, and the registry
/// of running in-process teammates. Implements IAsyncDisposable so callers can await a
/// bounded teardown.
/// </summary>
public sealed class TeamManager : IAsyncDisposable
{
    private sealed record RunningTeammate(
        TeammateRunner Runner,
        CancellationTokenSource Cts,
        Task Task);

    private readonly string teamsBaseDir;
    private readonly Func<TeammateIdentity, string, ITeammateAgent> teammateAgentFactory;
    private readonly ConcurrentDictionary<string, RunningTeammate> registry = new();

    public TeamStore Store { get; }
    public Mailbox Mailbox { get; }
    public TaskBoard Board { get; }
    public string? TeamName { get; private set; }
    public string? LeadAgentId { get; private set; }

    public TeamManager(
        string teamsBaseDir,
        Func<TeammateIdentity, string, ITeammateAgent> teammateAgentFactory)
    {
        this.teamsBaseDir = teamsBaseDir;
        this.teammateAgentFactory = teammateAgentFactory;
        this.Store = new TeamStore(teamsBaseDir);
        this.Mailbox = new Mailbox(teamsBaseDir);
        this.Board = new TaskBoard(teamsBaseDir);
    }

    // ── Team creation ─────────────────────────────────────────────────────────

    public (bool Ok, string Message) CreateTeam(string name, string? description)
    {
        if (!TeamStore.IsValidTeamName(name))
        {
            return (false, $"Invalid team name: '{name}'.");
        }

        var leadAgentId = AgentId.Format(TeamConstants.TeamLeadName, name);
        var leadColor = TeamColors.Assign(leadAgentId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var existing = this.Store.Read(name);
        if (existing is null)
        {
            var leadMember = new TeamMember(
                AgentId: leadAgentId,
                Name: TeamConstants.TeamLeadName,
                AgentType: null,
                Model: null,
                Prompt: null,
                Color: leadColor,
                JoinedAt: now,
                IsActive: true,
                Subscriptions: []);

            var teamFile = new TeamFile(
                Name: name,
                Description: description,
                CreatedAt: now,
                LeadAgentId: leadAgentId,
                Members: [leadMember]);

            this.Store.Write(name, teamFile);
        }

        this.TeamName = name;
        this.LeadAgentId = leadAgentId;
        return (true, $"Created team '{name}'.");
    }

    // ── Spawn teammate ────────────────────────────────────────────────────────

    public async Task<(bool Ok, string Message)> SpawnTeammateAsync(
        string name,
        string prompt,
        string? agentType,
        string? model,
        CancellationToken ct)
    {
        if (this.TeamName is null)
        {
            return (false, "No team. Call team_create first.");
        }

        var uniqueName = this.GenerateUniqueTeammateName(name);
        var sanitized = AgentId.SanitizeAgentName(uniqueName);
        var agentId = AgentId.Format(sanitized, this.TeamName);
        var color = TeamColors.Assign(agentId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var member = new TeamMember(
            AgentId: agentId,
            Name: sanitized,
            AgentType: agentType,
            Model: model,
            Prompt: prompt,
            Color: color,
            JoinedAt: now,
            IsActive: true,
            Subscriptions: []);

        this.Store.AddMember(this.TeamName, member);

        var identity = new TeammateIdentity(agentId, sanitized, this.TeamName, color);
        var agent = this.teammateAgentFactory(identity, prompt);
        var runner = new TeammateRunner(identity, agent, this.Mailbox, this.Store, this.Board);
        var cts = new CancellationTokenSource();

        var teamName = this.TeamName; // capture for the lambda
        var runTask = Task.Run(
            async () =>
            {
                try
                {
                    await runner.RunAsync(prompt, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected on Kill / DisposeAsync
                }
            },
            ct);

        this.registry[agentId] = new RunningTeammate(runner, cts, runTask);
        return (true, $"Spawned teammate '{sanitized}'.");
    }

    // ── Queries / control ─────────────────────────────────────────────────────

    public IReadOnlyList<TeamMember> ListMembers()
    {
        if (this.TeamName is null)
        {
            return [];
        }

        return this.Store.Read(this.TeamName)?.Members ?? [];
    }

    public bool SignalShutdownApproved(string agentId)
    {
        if (!this.registry.TryGetValue(agentId, out var running))
        {
            return false;
        }

        running.Runner.SignalShutdownApproved();
        // Also kill (cancel CTS) and remove from registry so the runner exits promptly.
        this.Kill(agentId);
        return true;
    }

    public bool Kill(string agentId)
    {
        if (!this.registry.TryRemove(agentId, out var running))
        {
            return false;
        }

        running.Cts.Cancel();

        if (this.TeamName is not null)
        {
            this.Store.RemoveMemberByAgentId(this.TeamName, agentId);
        }

        return true;
    }

    // ── Drain leader inbox ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> DrainLeaderInboxAsync(CancellationToken ct)
    {
        if (this.TeamName is null)
        {
            return [];
        }

        var messages = await this.Mailbox
            .ReadAsync(TeamConstants.TeamLeadName, this.TeamName, ct)
            .ConfigureAwait(false);

        var surfaced = new List<string>();

        foreach (var msg in messages)
        {
            if (msg.Read)
            {
                continue;
            }

            if (TeamMessages.IsStructuredProtocolMessage(msg.Text))
            {
                this.HandleStructuredMessage(msg);
            }
            else
            {
                surfaced.Add(TeamMessages.FormatTeammateMessage(
                    msg.From,
                    msg.Text,
                    msg.Color,
                    msg.Summary));
            }
        }

        await this.Mailbox
            .MarkAllReadAsync(TeamConstants.TeamLeadName, this.TeamName, ct)
            .ConfigureAwait(false);

        return surfaced;
    }

    // ── Delete team ───────────────────────────────────────────────────────────

    public async Task<(bool Ok, string Message)> DeleteTeamAsync(CancellationToken ct)
    {
        await this.CancelAllAndWaitAsync().ConfigureAwait(false);

        if (this.TeamName is not null)
        {
            this.Store.Delete(this.TeamName);
        }

        this.TeamName = null;
        this.LeadAgentId = null;
        return (true, "Team deleted.");
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await this.CancelAllAndWaitAsync().ConfigureAwait(false);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Generates a unique teammate name within the current team by appending a numeric
    /// suffix (-2, -3, …) if the base name is already taken (case-insensitive).
    /// </summary>
    private string GenerateUniqueTeammateName(string baseName)
    {
        if (this.TeamName is null)
        {
            return baseName;
        }

        var teamFile = this.Store.Read(this.TeamName);
        if (teamFile is null)
        {
            return baseName;
        }

        var existingNames = new HashSet<string>(
            teamFile.Members.Select(m => m.Name.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        if (!existingNames.Contains(baseName.ToLowerInvariant()))
        {
            return baseName;
        }

        var suffix = 2;
        while (existingNames.Contains($"{baseName}-{suffix}".ToLowerInvariant()))
        {
            suffix++;
        }

        return $"{baseName}-{suffix}";
    }

    /// <summary>
    /// Handles a structured protocol message directed at the team-lead inbox.
    /// </summary>
    private void HandleStructuredMessage(TeammateMessage msg)
    {
        var idleNotification = TeamMessages.TryParseIdleNotification(msg.Text);
        if (idleNotification is not null && this.TeamName is not null)
        {
            this.Store.SetMemberActive(this.TeamName, idleNotification.From, false);
            return;
        }

        var shutdownApproved = TeamMessages.TryParseShutdownApproved(msg.Text);
        if (shutdownApproved is not null && this.TeamName is not null)
        {
            // Find the member whose name matches From, get their agentId, and kill.
            var teamFile = this.Store.Read(this.TeamName);
            if (teamFile is not null)
            {
                var member = teamFile.Members.FirstOrDefault(m =>
                    string.Equals(m.Name, shutdownApproved.From, StringComparison.OrdinalIgnoreCase));
                if (member is not null)
                {
                    this.Kill(member.AgentId);
                }
            }
        }
    }

    /// <summary>
    /// Cancels all running teammates and waits (bounded to 5 s) for their tasks to complete.
    /// </summary>
    private async Task CancelAllAndWaitAsync()
    {
        var snapshot = this.registry.ToArray();

        foreach (var kvp in snapshot)
        {
            kvp.Value.Cts.Cancel();
        }

        var tasks = snapshot.Select(kvp => kvp.Value.Task).ToArray();

        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks)
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
            catch
            {
                // best-effort: defensive teardown — teammate tasks are cancelled above and the
                // 5 s WhenAll bound only guards against a hung teammate. A logger is not threaded
                // here: TeamManager is constructed in CodaSession before the loggerFactory exists
                // (its telemetry handle is opened last, by invariant), so none is reachable, and a
                // cancellation/timeout on teardown is untestable, low-value defensive cleanup.
            }
        }

        foreach (var kvp in snapshot)
        {
            kvp.Value.Cts.Dispose();
            this.registry.TryRemove(kvp.Key, out _);
        }
    }
}
