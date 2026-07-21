using Coda.Agent;
using Coda.Agent.Tasks;
using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;
using LlmClient;
using Spectre.Console;

namespace Coda.Tui.Agent;

/// <summary>
/// Thin TUI adapter over <see cref="CodaSession"/>: builds the per-turn options from the session
/// state, streams the reply into the semantic UI as typed events, and — after each completed turn —
/// republishes the session runtime, context-window, git, cost, and metadata state so any frontend
/// stays in sync. The shared engine (client, tools, subagents, permission mode, transactional
/// history) lives in <see cref="CodaSession"/>, reused across turns and sharing the session's
/// history list (so /clear works).
/// </summary>
public sealed class AgentRunner : IDisposable
{
    private readonly Func<IReadOnlyList<ITool>>? extraToolsProvider;
    private readonly Func<CommandContext, SessionOptions, CodaSession> sessionFactory;
    private readonly object turnGate = new();

    private CodaSession? session;

    // The linked cancellation source for the currently running turn (null when idle). Cancelling it
    // interrupts only the active turn, never the caller's ambient token.
    private CancellationTokenSource? activeTurnCts;
    private bool disposed;

    public AgentRunner(Func<IReadOnlyList<ITool>>? extraToolsProvider = null)
        : this(extraToolsProvider, DefaultSessionFactory)
    {
    }

    internal AgentRunner(
        Func<IReadOnlyList<ITool>>? extraToolsProvider,
        Func<CommandContext, SessionOptions, CodaSession> sessionFactory)
    {
        this.extraToolsProvider = extraToolsProvider;
        this.sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    /// <summary>True while a turn is running (between <see cref="RunAsync"/> start and completion).</summary>
    public bool HasActiveTurn
    {
        get
        {
            lock (this.turnGate)
            {
                return this.activeTurnCts is not null;
            }
        }
    }

    /// <summary>The live task registry once the session exists (null before the first turn runs).</summary>
    public TaskManager? Tasks => this.session?.Tasks;

    /// <summary>The session's cooperative pause gate (null before the first turn runs).</summary>
    public AgentExecutionGate? ExecutionGate => this.session?.ExecutionGate;

    /// <summary>For tests: whether <see cref="Dispose"/> has already run.</summary>
    internal bool IsDisposed
    {
        get
        {
            lock (this.turnGate)
            {
                return this.disposed;
            }
        }
    }

    /// <summary>
    /// Cancel the active turn's linked token (interrupting only that turn, not the caller's token).
    /// Returns false when no turn is running.
    /// </summary>
    public bool TryInterruptActiveTurn()
    {
        CancellationTokenSource? cts;
        lock (this.turnGate)
        {
            cts = this.activeTurnCts;
        }

        if (cts is null)
        {
            return false;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn finished and disposed its source between the read and the cancel — treat as
            // "nothing to interrupt".
            return false;
        }

        return true;
    }

    /// <summary>A fresh copy of the current session runtime snapshot, or null before the first turn.</summary>
    public SessionRuntimeSnapshot? GetRuntimeSnapshot() => this.session?.GetRuntimeSnapshot();

    public async Task RunAsync(CommandContext context, string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var turnCts = this.BeginTurn(cancellationToken);
        try
        {
            // Publish the prompt + turn start BEFORE any model/sink activity, so the transcript and
            // status reflect the in-flight turn immediately.
            context.Events.Publish(new UserPromptSubmittedEvent(prompt));
            context.Events.Publish(new TurnStartedEvent(prompt));

            await this.EnsureSessionAsync(context, turnCts.Token).ConfigureAwait(false);

            var result = await this.RunTurnAsync(context, prompt, turnCts.Token).ConfigureAwait(false);

            // Keep the TUI SessionState in sync so /cost can read accumulated usage.
            context.Session.SessionUsage = this.session!.SessionUsage;

            if (!result.Success && turnCts.Token.IsCancellationRequested)
            {
                // The turn was cancelled (Ctrl-C / mode switch / shutdown). Preserve the existing
                // user-facing cancellation behavior and signal the interruption; skip the post-turn
                // refresh (a rolled-back turn changed nothing meaningful).
                context.Events.Publish(new TurnInterruptedEvent());
                this.RenderFailure(context, result.Error);
                return;
            }

            await this.PublishPostTurnAsync(context, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                this.RenderFailure(context, result.Error);
            }

            context.Events.Publish(new TurnCompletedEvent(result.Success));
        }
        finally
        {
            this.EndTurn(turnCts);
        }
    }

    private static CodaSession DefaultSessionFactory(CommandContext context, SessionOptions options) =>
        new(
            context.Credentials,
            options,
            history: context.Session.History,
            sessionId: context.Session.SessionId);

    /// <summary>
    /// Register a fresh per-turn linked cancellation source. Overlapping turns are rejected rather
    /// than allowed to corrupt the reused session or the active-turn source.
    /// </summary>
    private CancellationTokenSource BeginTurn(CancellationToken cancellationToken)
    {
        lock (this.turnGate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (this.activeTurnCts is not null)
            {
                throw new InvalidOperationException("A turn is already running; interrupt it before starting another.");
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            this.activeTurnCts = cts;
            return cts;
        }
    }

    private void EndTurn(CancellationTokenSource cts)
    {
        lock (this.turnGate)
        {
            if (ReferenceEquals(this.activeTurnCts, cts))
            {
                this.activeTurnCts = null;
            }
        }

        cts.Dispose();
    }

    private async Task EnsureSessionAsync(CommandContext context, CancellationToken cancellationToken)
    {
        // Created lazily and reused (so the HttpClient + conversation persist); the session shares
        // the SessionState history list, so /clear resets both.
        if (this.session is null)
        {
            this.session = this.sessionFactory(context, this.BuildOptions(context));

            // If the session generated its own id (no resume seeded one), capture it back so /export,
            // /resume, and later turns all reference the same id.
            context.Session.SessionId ??= this.session.SessionId;

            // Start configured LSP servers + diagnostics handlers (no-op when none are configured).
            await this.session.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (context.Session.SessionId is { } id && this.session.SessionId != id)
        {
            // /resume changed the target id mid-session; adopt it so this turn appends to that transcript.
            this.session.AdoptSessionId(id);
        }

        this.session.Options = this.BuildOptions(context);
    }

    private async Task<RunResult> RunTurnAsync(CommandContext context, string prompt, CancellationToken cancellationToken)
    {
        var sink = new TuiAgentSink(context.Events);
        if (context.Session.PendingImages.Count > 0)
        {
            // Build a multimodal turn: staged images + the text prompt. PendingImages is cleared only
            // AFTER a successful turn so a failed/cancelled request never silently discards the user's
            // attachment (clear-on-success policy).
            var userContent = new List<ContentBlock>(context.Session.PendingImages) { new TextBlock(prompt) };
            var result = await this.session!.RunAsync(userContent, sink, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                context.Session.PendingImages.Clear();
            }

            return result;
        }

        return await this.session!.RunAsync(prompt, sink, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// After a completed (non-interrupted) turn, republish the runtime, context-window, git, cost,
    /// and metadata state so the semantic UI reflects the new session state. Cache-refresh failures
    /// are surfaced as diagnostics rather than turning a successful model run into a turn failure;
    /// caller cancellation still propagates.
    /// </summary>
    private async Task PublishPostTurnAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var session = this.session!;

        context.Events.Publish(new SessionRuntimeChangedEvent(session.GetRuntimeSnapshot()));

        if (context.ContextSnapshots is { } contextCache)
        {
            await this.RefreshContextAsync(context, contextCache, cancellationToken).ConfigureAwait(false);
        }

        if (context.GitStatus is { } gitCache)
        {
            await this.RefreshGitAsync(context, gitCache, cancellationToken).ConfigureAwait(false);
        }

        // Cost estimate: same model/catalog inputs as /cost.
        var usage = context.Session.SessionUsage;
        var catalog = ModelCatalog.Default.Get(context.Session.ActiveProviderId, context.Session.Model);
        var estimatedUsd = Pricing.EstimateUsd(context.Session.Model, usage, catalog);
        context.Events.Publish(new CostEstimateChangedEvent(estimatedUsd));

        SessionMetadataEvents.Publish(context);
    }

    private async Task RefreshContextAsync(CommandContext context, ContextSnapshotCache cache, CancellationToken cancellationToken)
    {
        cache.InvalidateAfterTurn();
        ContextReport report;
        try
        {
            report = await cache.GetAsync(force: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Events.Publish(new DiagnosticEvent("context", $"Context refresh failed: {ex.Message}", UiNotificationLevel.Warning));
            return;
        }

        context.Events.Publish(new ContextChangedEvent(
            new ContextStatus(report.UsedTokens, report.MaxTokens, report.Percentage, report.IsExact)));
    }

    private async Task RefreshGitAsync(CommandContext context, GitStatusCache cache, CancellationToken cancellationToken)
    {
        cache.InvalidateAfterTurn();
        GitStatus status;
        try
        {
            status = await cache.GetAsync(context.Session.WorkingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Events.Publish(new DiagnosticEvent("git", $"Git status refresh failed: {ex.Message}", UiNotificationLevel.Warning));
            return;
        }

        context.Events.Publish(new GitChangedEvent(status));
    }

    private SessionOptions BuildOptions(CommandContext context) => new()
    {
        ProviderId = context.ActiveProvider.Id,
        Model = context.Session.Model,
        WorkingDirectory = context.Session.WorkingDirectory,
        PermissionMode = context.Session.PermissionMode,
        // Pass the session's stable, shared state so a mid-turn /yolo or /permissions change is
        // observed by the running loop and its subagents' next permission decision.
        PermissionModeState = context.Session.PermissionModes,
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

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (this.turnGate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            cts = this.activeTurnCts;
            this.activeTurnCts = null;
        }

        cts?.Dispose();
        this.session?.Dispose();
    }
}
