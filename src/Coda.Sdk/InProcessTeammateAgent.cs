using Coda.Agent;
using Coda.Agent.OutputStyles;
using Coda.Agent.Permissions;
using Coda.Agent.Teams;
using Coda.Agent.Tools;
using LlmAuth;
using LlmAuth.Providers.GitHubCopilot;
using LlmClient;

namespace Coda.Sdk;

/// <summary>
/// In-process teammate agent: wraps a nested <see cref="AgentLoop"/> that accumulates
/// history across turns. Each <see cref="RunTurnAsync"/> call runs one iteration of
/// the loop (prompt → tool cycles → stop) and returns the assistant's text.
///
/// The teammate's tool set is the session's base tools PLUS the team messaging/board
/// tools, MINUS spawn_teammate/team_create/team_delete (no recursive team spawning).
/// The teammate's ToolContext wires AgentName = the teammate's own name so send_message
/// uses the correct sender identity.
///
/// FIX 2: receives the session's SINGLE shared <see cref="TeamManager"/> instead of
/// constructing a throwaway one per turn, so all locking in <see cref="TeamStore"/>
/// serializes against the same monitor objects used by the leader.
/// </summary>
internal sealed class InProcessTeammateAgent : ITeammateAgent
{
    private readonly TeammateIdentity identity;
    private readonly CredentialManager credentials;
    private readonly ClientFingerprint fingerprint;
    private readonly HttpClient http;
    private readonly SessionOptions sessionOptions;
    private readonly TeamManager sharedTeamManager;
    private readonly List<ChatMessage> history = [];

    public InProcessTeammateAgent(
        TeammateIdentity identity,
        CredentialManager credentials,
        ClientFingerprint fingerprint,
        HttpClient http,
        SessionOptions sessionOptions,
        TeamManager sharedTeamManager)
    {
        this.identity = identity;
        this.credentials = credentials;
        this.fingerprint = fingerprint;
        this.http = http;
        this.sessionOptions = sessionOptions;
        this.sharedTeamManager = sharedTeamManager;
    }

    public async Task<string> RunTurnAsync(string prompt, CancellationToken cancellationToken)
    {
        var options = this.sessionOptions;

        var client = LlmClientFactory.Create(options.ProviderId, this.credentials, this.fingerprint, this.http);
        if (client is null)
        {
            return $"(teammate error: no chat client for provider '{options.ProviderId}')";
        }

        var includeAnthropicSystemPrefix = options.ProviderId != GitHubCopilotProvider.Id;

        // Build a teammate-specific system prompt: base session prompt + teammate addendum.
        var baseSystemPrompt = AgentSystemPrompt.Build(
            options.WorkingDirectory,
            includeAnthropicSystemPrefix,
            ProjectContext.Load(options.WorkingDirectory),
            BuiltInOutputStyles.Resolve(options.OutputStyle).SystemPromptSuffix);

        var teammateAddendum = TeammateSystemPrompt.Format(
            this.identity.AgentName,
            this.identity.TeamName);

        var systemPrompt = baseSystemPrompt + "\n\n" + teammateAddendum;

        var agentOptions = new AgentOptions
        {
            Model = options.Model,
            SystemPrompt = systemPrompt,
            WorkingDirectory = options.WorkingDirectory,
            // Teammates run in bypass mode (see the ModePermissionPrompt below); keep the
            // options in sync so their filesystem tools match that (unrestricted) posture.
            PermissionMode = PermissionMode.BypassPermissions,
            MaxIterations = Math.Min(options.MaxIterations, 500),
            MaxTokens = ModelLimits.ResolveMaxOutputTokens(ModelCatalog.Default, options.ProviderId, options.Model, options.MaxTokens),
            AutoCompactTokenThreshold = ModelLimits.ResolveAutoCompactThreshold(ModelCatalog.Default, options.ProviderId, options.Model, options.AutoCompactTokenThreshold),
        };

        var permissions = new ModePermissionPrompt(
            PermissionMode.BypassPermissions,
            options.InteractivePrompt);

        // Teammate tool set: built-ins + team messaging/board tools.
        // Excludes spawn_teammate, team_create, team_delete, task (no nested teams or subagents).
        var teammateTools = new ITool[]
        {
            new SendMessageTool(),
            new TaskCreateTool(),
            new TaskListTool(),
            new TaskGetTool(),
            new TaskUpdateTool(),
            new TaskStopTool(),
        };

        var registry = new ToolRegistry([.. BuiltInTools.All(), .. options.ExtraTools, .. teammateTools]);

        // Reuse the session's shared TeamManager — do NOT construct a new one or call CreateTeam.
        // This ensures all read-modify-write operations on the TeamStore go through the same
        // per-file lock objects as the leader agent.
        var loop = new AgentLoop(
            client,
            registry,
            permissions,
            agentOptions,
            teams: this.sharedTeamManager);

        this.history.Add(ChatMessage.UserText(prompt));

        var sink = new CollectingTextSink();

        try
        {
            await loop.RunAsync(this.history, sink, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"(teammate error: {ex.Message})";
        }

        var text = sink.CollectedText;
        return text.Length == 0 ? "(no output)" : text;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private sealed class CollectingTextSink : IAgentSink
    {
        private readonly System.Text.StringBuilder sb = new();

        public string CollectedText => this.sb.ToString().Trim();

        public void OnAssistantText(string delta) => this.sb.Append(delta);
        public void OnAssistantTextComplete() { }
        public void OnToolCall(string toolName, string inputPreview) { }
        public void OnToolResult(string toolName, ToolResult result) { }
        public void OnError(string message) { }
        public void OnStopReason(string? stopReason) { }
    }
}
