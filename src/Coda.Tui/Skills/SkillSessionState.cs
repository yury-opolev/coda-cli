namespace Coda.Tui.Skills;

/// <summary>
/// Per-session, pure state tracker for model-invoked skills. Tracks which skills have
/// been loaded and their most-recent rendered body so that identical re-invocations produce
/// an "already loaded" note (avoiding a second copy in context) and re-invocations with
/// different arguments return the new body.
/// </summary>
/// <remarks>
/// Thread-safety is not required (single agent loop). State is per-session and must be
/// created once at composition time, not per turn.
/// </remarks>
public sealed class SkillSessionState
{
    /// <summary>Default character budget for <see cref="GetReattachContent"/> and the ceiling for <see cref="DeriveReattachBudget"/>.</summary>
    public const int DefaultReattachBudget = 20_000;

    private readonly Dictionary<string, (string RenderedBody, int InvocationOrder)> _loaded
        = new(StringComparer.OrdinalIgnoreCase);

    private int _nextOrder;

    /// <summary>
    /// Records the invocation of <paramref name="skillName"/> with <paramref name="renderedBody"/>
    /// and returns whether this is the first load, together with the content the tool should return.
    /// </summary>
    /// <returns>
    /// <list type="bullet">
    ///   <item>First load: <c>(true, renderedBody)</c>.</item>
    ///   <item>Re-invocation with identical body: <c>(false, "already loaded" note)</c>.</item>
    ///   <item>Re-invocation with different body: <c>(false, renderedBody)</c> — genuinely new content.</item>
    /// </list>
    /// </returns>
    public (bool IsFirstLoad, string Content) TryLoad(string skillName, string renderedBody)
    {
        if (this._loaded.TryGetValue(skillName, out var existing))
        {
            if (string.Equals(existing.RenderedBody, renderedBody, StringComparison.Ordinal))
            {
                // Same body — already in context. Return a short note so the model knows
                // the skill is available without injecting a duplicate copy.
                return (false, $"Skill '{skillName}' is already loaded in this session.");
            }

            // Different rendered body (different arguments) — update and return new body.
            this._loaded[skillName] = (renderedBody, this._nextOrder++);
            return (false, renderedBody);
        }

        // First load.
        this._loaded[skillName] = (renderedBody, this._nextOrder++);
        return (true, renderedBody);
    }

    /// <summary>
    /// Derives the character budget for <see cref="GetReattachContent"/> from the session's
    /// auto-compaction threshold so the reattach overhead scales with the model's actual context
    /// window rather than burning a fixed fraction of small windows.
    /// </summary>
    /// <param name="autoCompactTokenThreshold">
    /// The resolved compaction threshold for this turn (tokens). Must be the value after
    /// <c>ModelLimits.ResolveAutoCompactThreshold</c> has been applied; 0 or negative falls back
    /// to <see cref="DefaultReattachBudget"/>.
    /// </param>
    /// <returns>
    /// 25% of <paramref name="autoCompactTokenThreshold"/> converted at 4 chars/token,
    /// capped at <see cref="DefaultReattachBudget"/>. A threshold of 0 or below returns
    /// <see cref="DefaultReattachBudget"/> unchanged.
    /// </returns>
    public static int DeriveReattachBudget(int autoCompactTokenThreshold)
    {
        if (autoCompactTokenThreshold <= 0)
        {
            return DefaultReattachBudget;
        }

        // 25% of threshold tokens converted at ~4 chars/token; capped at the constant ceiling.
        const double Fraction = 0.25;
        const int CharsPerToken = 4;
        var derived = (int)(autoCompactTokenThreshold * Fraction * CharsPerToken);
        return Math.Min(DefaultReattachBudget, derived);
    }

    /// <summary>
    /// Returns the most-recent rendered body of each loaded skill, ordered most-recently-used
    /// first, truncated to <paramref name="charBudget"/> characters. Adjacent bodies are
    /// separated by a blank line. Returns an empty string when no skills have been loaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called after compaction to re-inject skill context into the session history so that a
    /// compacted session does not silently lose skills the model has already loaded.
    /// </para>
    /// <para>
    /// Skills whose bodies cannot fit within <paramref name="charBudget"/> are <em>evicted</em>
    /// from the loaded set. This means a later invocation of the same skill will return the full
    /// body again instead of an "already loaded" note — avoiding a state where the model is told
    /// a skill is loaded when its body is no longer reachable in context.
    /// </para>
    /// <para>
    /// A single large body that exceeds <paramref name="charBudget"/> but fits within
    /// <see cref="DefaultReattachBudget"/> is always emitted so the most-recent skill body is
    /// never silently dropped when the dynamic budget is small.
    /// </para>
    /// </remarks>
    public string GetReattachContent(int charBudget = DefaultReattachBudget)
    {
        if (this._loaded.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalLength = 0;
        const string Separator = "\n\n";

        var ordered = this._loaded
            .OrderByDescending(e => e.Value.InvocationOrder)
            .ToList();

        foreach (var entry in ordered)
        {
            var body = entry.Value.RenderedBody;
            var addition = parts.Count == 0 ? body.Length : Separator.Length + body.Length;

            if (totalLength + addition > charBudget)
            {
                // Do NOT break — continue to try smaller subsequent bodies so one oversized body
                // does not starve all smaller ones that would fit in the remaining budget.
                continue;
            }

            parts.Add(body);
            totalLength += addition;
            included.Add(entry.Key);
        }

        // Guarantee: always emit the single most-recent body when nothing fit within charBudget
        // but the body itself is within the fixed ceiling. Prevents complete silence when the
        // dynamic budget (derived from a small compaction threshold) is tighter than the body.
        if (parts.Count == 0 && ordered.Count > 0)
        {
            var mostRecentBody = ordered[0].Value.RenderedBody;
            if (mostRecentBody.Length <= DefaultReattachBudget)
            {
                parts.Add(mostRecentBody);
                included.Add(ordered[0].Key);
            }
        }

        // Evict skills whose bodies are not represented in the output. Their context has been
        // dropped by the compaction budget, so the "already loaded" note would be a lie — the
        // model no longer has those instructions. Removing them from the loaded set causes the
        // next invocation to re-emit the full body rather than the misleading note.
        foreach (var name in this._loaded.Keys.Where(k => !included.Contains(k)).ToList())
        {
            this._loaded.Remove(name);
        }

        return string.Join(Separator, parts);
    }
}
