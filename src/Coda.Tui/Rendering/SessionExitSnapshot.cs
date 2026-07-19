using Coda.Sdk;
using Coda.Tui.Repl;

namespace Coda.Tui.Rendering;

/// <summary>The latest cached context-window usage, projected for the exit card.</summary>
public sealed record ContextUsageSnapshot(int UsedTokens, int MaxTokens, int Percentage, bool IsExact);

/// <summary>
/// An immutable projection of the mutable <see cref="SessionState"/> (plus the latest cached
/// <see cref="ContextReport"/> and injected start/end timestamps) captured at clean interactive
/// exit. Building it must never trigger a new provider request or context analysis — it reads only
/// already-computed state.
/// </summary>
public sealed record SessionExitSnapshot
{
    /// <summary>Wall-clock session duration (never negative).</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>The persisted session id, or null when the conversation was never saved.</summary>
    public string? SessionId { get; init; }

    /// <summary>The session working directory.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The active provider id (e.g. <c>claude-ai</c>).</summary>
    public required string ProviderId { get; init; }

    /// <summary>The chat model id.</summary>
    public required string Model { get; init; }

    /// <summary>The reasoning effort level, or null for the model default ("auto").</summary>
    public string? Effort { get; init; }

    /// <summary>Number of conversation messages in history.</summary>
    public required int MessageCount { get; init; }

    public required int InputTokens { get; init; }

    public required int OutputTokens { get; init; }

    public int TotalTokens => this.InputTokens + this.OutputTokens;

    /// <summary>Estimated USD cost for the accumulated usage (catalog-aware pricing).</summary>
    public required decimal EstimatedUsd { get; init; }

    /// <summary>The latest cached context usage, or null when no analysis ran this session.</summary>
    public ContextUsageSnapshot? Context { get; init; }

    /// <summary>True when the conversation was persisted (a session id exists).</summary>
    public bool HasSession => !string.IsNullOrEmpty(this.SessionId);

    /// <summary>
    /// Projects an immutable snapshot from live session state. <paramref name="context"/> is the
    /// most recent cached report (<see cref="ContextSnapshotCache.Current"/>) or null; no new
    /// analysis is performed. <paramref name="catalog"/> defaults to <see cref="ModelCatalog.Default"/>.
    /// </summary>
    public static SessionExitSnapshot Create(
        SessionState session,
        ContextReport? context,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        ModelCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var effectiveCatalog = catalog ?? ModelCatalog.Default;
        var usage = session.SessionUsage;
        var catalogModel = effectiveCatalog.Get(session.ActiveProviderId, session.Model);
        var estimatedUsd = Pricing.EstimateUsd(session.Model, usage, catalogModel);

        var duration = endedAt - startedAt;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var contextSnapshot = context is null
            ? null
            : new ContextUsageSnapshot(context.UsedTokens, context.MaxTokens, context.Percentage, context.IsExact);

        return new SessionExitSnapshot
        {
            Duration = duration,
            SessionId = session.SessionId,
            WorkingDirectory = session.WorkingDirectory,
            ProviderId = session.ActiveProviderId,
            Model = session.Model,
            Effort = session.Effort,
            MessageCount = session.History.Count,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            EstimatedUsd = estimatedUsd,
            Context = contextSnapshot,
        };
    }
}
