namespace Coda.Agent.Tools;

/// <summary>
/// Pure allow/deny name filter for the main agent's tool registry.
/// Created from the <c>agent.tools</c> settings block and applied at composition time in
/// <see cref="Coda.Sdk.Turns.TurnPipelineBuilder.BuildParentTools"/> — and NOWHERE ELSE.
/// </summary>
/// <remarks>
/// <para>
/// Subagent and scheduled-root registries are built independently and are never filtered here.
/// Non-propagation is the design intent: using <c>agent.tools</c> as a
/// <see cref="Coda.Agent.TurnShape.AllowedTools"/> value would intersect with each subagent's
/// own registry and could leave every subagent with zero tools. Composition-time filtering of
/// only the main-agent registry gives non-propagation by construction.
/// </para>
/// <para>
/// This is a WORKFLOW control, NOT a security boundary: subagents retain full toolsets so
/// workflows that need only a subset of tools at the top level can enforce that shape while
/// still permitting unrestricted delegation.
/// </para>
/// </remarks>
public sealed class ToolNameFilter
{
    /// <summary>
    /// When non-null, only tools whose <see cref="ITool.Name"/> appears in this set
    /// (case-insensitive) pass through. An empty allowlist is honoured literally — every tool
    /// is excluded. Null means no allowlist restriction (today's behaviour).
    /// </summary>
    public IReadOnlyList<string>? Allow { get; }

    /// <summary>
    /// Tool names (case-insensitive) that are removed after the allowlist step.
    /// Deny wins when a name appears in both <see cref="Allow"/> and <see cref="Deny"/>.
    /// An empty list has no effect.
    /// </summary>
    public IReadOnlyList<string> Deny { get; }

    /// <summary>Creates a filter with the given allowlist and deny list.</summary>
    /// <param name="allow">Allowlist; null means no restriction.</param>
    /// <param name="deny">Deny list; must not be null (use empty list for no denials).</param>
    public ToolNameFilter(IReadOnlyList<string>? allow, IReadOnlyList<string> deny)
    {
        this.Allow = allow;
        this.Deny = deny ?? throw new ArgumentNullException(nameof(deny));
    }

    /// <summary>
    /// Returns a monotonically tightening merge of <paramref name="user"/> and
    /// <paramref name="project"/>:
    /// <list type="bullet">
    ///   <item><c>allow</c> is INTERSECTED — a project file can restrict further but can never
    ///     widen what the user file permitted.</item>
    ///   <item><c>deny</c> is UNIONED — either file's denials are always honoured.</item>
    /// </list>
    /// This mirrors the <c>allowedTools</c> merge in <c>HookBus</c> (~lines 1648-1663).
    /// </summary>
    public static ToolNameFilter Merge(ToolNameFilter? user, ToolNameFilter? project)
    {
        if (user is null && project is null)
        {
            return new ToolNameFilter(null, []);
        }

        // Intersect allow lists: null from either side means "no opinion".
        IReadOnlyList<string>? mergedAllow;
        if (user?.Allow is null && project?.Allow is null)
        {
            mergedAllow = null;
        }
        else if (user?.Allow is null)
        {
            mergedAllow = project!.Allow;
        }
        else if (project?.Allow is null)
        {
            mergedAllow = user.Allow;
        }
        else
        {
            var projectSet = new HashSet<string>(project.Allow, StringComparer.OrdinalIgnoreCase);
            mergedAllow = [.. user.Allow.Where(n => projectSet.Contains(n))];
        }

        // Union deny lists.
        var userDeny = user?.Deny ?? [];
        var projectDeny = project?.Deny ?? [];
        IReadOnlyList<string> mergedDeny;
        if (userDeny.Count == 0 && projectDeny.Count == 0)
        {
            mergedDeny = [];
        }
        else
        {
            var combined = new HashSet<string>(userDeny, StringComparer.OrdinalIgnoreCase);
            foreach (var n in projectDeny)
            {
                combined.Add(n);
            }

            mergedDeny = [.. combined];
        }

        return new ToolNameFilter(mergedAllow, mergedDeny);
    }

    /// <summary>
    /// Filters a sequence of tools, returning only those whose names pass the allow/deny rules.
    /// The output preserves the input order.
    /// </summary>
    public IEnumerable<ITool> Apply(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        IEnumerable<ITool> result = tools;

        if (this.Allow is not null)
        {
            var allowSet = new HashSet<string>(this.Allow, StringComparer.OrdinalIgnoreCase);
            result = result.Where(t => allowSet.Contains(t.Name));
        }

        if (this.Deny.Count > 0)
        {
            var denySet = new HashSet<string>(this.Deny, StringComparer.OrdinalIgnoreCase);
            result = result.Where(t => !denySet.Contains(t.Name));
        }

        return result;
    }

    /// <summary>
    /// Returns <see langword="true"/> when a hypothetical tool with <paramref name="toolName"/>
    /// would survive this filter. Used by the inert-agent guard to probe built-in tool names
    /// without materialising the full registry.
    /// </summary>
    public bool Passes(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        if (this.Allow is not null)
        {
            var allowSet = new HashSet<string>(this.Allow, StringComparer.OrdinalIgnoreCase);
            if (!allowSet.Contains(toolName))
            {
                return false;
            }
        }

        if (this.Deny.Count > 0)
        {
            var denySet = new HashSet<string>(this.Deny, StringComparer.OrdinalIgnoreCase);
            if (denySet.Contains(toolName))
            {
                return false;
            }
        }

        return true;
    }
}
