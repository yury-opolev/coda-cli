using Coda.Agent;
using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using LlmClient;
using Spectre.Console;

namespace Coda.Tui.Agent;

/// <summary>
/// Thin TUI adapter over <see cref="CodaSession"/>: builds the per-turn options
/// from the session state and streams the reply into the console. The shared
/// engine (client, tools, subagents, permission mode, transactional history) lives
/// in <see cref="CodaSession"/>, which is reused across turns and shares the
/// session's history list (so /clear works).
/// </summary>
public sealed class AgentRunner : IDisposable
{
    private readonly Func<IReadOnlyList<ITool>>? extraToolsProvider;
    private CodaSession? session;

    public AgentRunner(Func<IReadOnlyList<ITool>>? extraToolsProvider = null)
    {
        this.extraToolsProvider = extraToolsProvider;
    }

    public async Task RunAsync(CommandContext context, string prompt, CancellationToken cancellationToken = default)
    {
        // Created lazily and reused (so the HttpClient + conversation persist); the
        // session shares the SessionState history list, so /clear resets both.
        if (this.session is null)
        {
            this.session = new CodaSession(
                context.Credentials,
                this.BuildOptions(context),
                history: context.Session.History,
                sessionId: context.Session.SessionId);

            // If the session generated its own id (no resume seeded one), capture it back so /export,
            // /resume, and later turns all reference the same id.
            context.Session.SessionId ??= this.session.SessionId;

            // Start configured LSP servers + diagnostics handlers (no-op when none
            // are configured). Done once, when the session is first created.
            await this.session.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (context.Session.SessionId is { } id && this.session.SessionId != id)
        {
            // /resume changed the target id mid-session; adopt it so this turn appends to that transcript.
            this.session.AdoptSessionId(id);
        }

        this.session.Options = this.BuildOptions(context);

        RunResult result;
        var hadImages = context.Session.PendingImages.Count > 0;
        if (hadImages)
        {
            // Build a multimodal turn: staged images + the text prompt.
            // PendingImages is cleared only AFTER a successful turn so that a
            // failed or cancelled request does not silently discard the user's
            // attachment (clear-on-success policy).
            var userContent = new List<ContentBlock>(context.Session.PendingImages) { new TextBlock(prompt) };
            result = await this.session.RunAsync(
                userContent,
                new TuiAgentSink(context.Console),
                cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                context.Session.PendingImages.Clear();
            }
        }
        else
        {
            result = await this.session.RunAsync(prompt, new TuiAgentSink(context.Console), cancellationToken)
                .ConfigureAwait(false);
        }

        // Keep the TUI SessionState in sync so /cost can read accumulated usage.
        context.Session.SessionUsage = this.session.SessionUsage;

        if (!result.Success)
        {
            this.RenderFailure(context, result.Error);
        }
    }

    private SessionOptions BuildOptions(CommandContext context) => new()
    {
        ProviderId = context.ActiveProvider.Id,
        Model = context.Session.Model,
        WorkingDirectory = context.Session.WorkingDirectory,
        PermissionMode = context.Session.PermissionMode,
        // Re-read each turn so /mcp start|stop changes take effect from the next turn.
        ExtraTools = this.extraToolsProvider?.Invoke() ?? [],
        InteractivePrompt = new TuiPermissionPrompt(context.Prompts, context.Events),
        UserQuestionPrompt = context.Prompts.IsInteractive
            ? new TuiUserQuestionPrompt(context.Prompts, context.Events)
            : null,
        PlanApprover = context.Prompts.IsInteractive
            ? new TuiPlanApprover(context.Prompts, context.Events, context.Session)
            : null,
        OutputStyle = context.Session.OutputStyle,
        Effort = context.Session.Effort,
        Goal = context.Session.Goal,
        GoalMaxDuration = context.Session.GoalMaxDuration,
        GoalMaxContinuations = context.Session.GoalMaxContinuations,
    };

    private void RenderFailure(CommandContext context, string? error)
    {
        if (string.IsNullOrEmpty(error) || error == "Canceled.")
        {
            context.Console.MarkupLine(Theme.DimMarkup("Canceled."));
        }
        else if (error.Contains("credential", StringComparison.OrdinalIgnoreCase))
        {
            context.Console.MarkupLine(Theme.WarnMarkup("Not signed in. Run /login or /setup first."));
        }
        else if (error.Contains("No chat client", StringComparison.OrdinalIgnoreCase))
        {
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"Chat isn't available for {context.ActiveProvider.DisplayName} yet. Switch with /provider claude, or use an API key."));
        }
        else
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"Model request failed: {error}"));
        }
    }

    public void Dispose() => this.session?.Dispose();
}
