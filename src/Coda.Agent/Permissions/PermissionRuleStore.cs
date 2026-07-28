namespace Coda.Agent.Permissions;

/// <summary>
/// A session-scoped, mutable store of permission rules. Holds the rules loaded from settings at
/// session start plus any rules added at runtime by a <c>PermissionRequest</c> hook returning
/// <c>updatedPermissions</c>.
/// </summary>
/// <remarks>
/// <para>
/// The store is the live source of truth for rule evaluation: <see cref="RulesPermissionPrompt"/>
/// reads it on every decision, so a rule added mid-session takes effect on the very next tool call.
/// It also computes the <c>matchedRule</c> field of the <c>PermissionRequest</c> hook payload.
/// </para>
/// <para>Instances are safe for concurrent use.</para>
/// </remarks>
public sealed class PermissionRuleStore
{
    private readonly Lock gate = new();
    private readonly List<PermissionRule> allowRules;
    private readonly List<PermissionRule> denyRules;

    /// <summary>Creates a store seeded with the rules loaded from settings.</summary>
    /// <param name="allow">Initial allow rules, or <see langword="null"/> for none.</param>
    /// <param name="deny">Initial deny rules, or <see langword="null"/> for none.</param>
    public PermissionRuleStore(
        IEnumerable<PermissionRule>? allow = null,
        IEnumerable<PermissionRule>? deny = null)
    {
        this.allowRules = [.. allow ?? []];
        this.denyRules = [.. deny ?? []];
    }

    /// <summary>A snapshot of the current allow rules.</summary>
    public IReadOnlyList<PermissionRule> Allow
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.allowRules];
            }
        }
    }

    /// <summary>A snapshot of the current deny rules.</summary>
    public IReadOnlyList<PermissionRule> Deny
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.denyRules];
            }
        }
    }

    /// <summary>Adds allow rules to the live set.</summary>
    public void AddAllow(IEnumerable<PermissionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        lock (this.gate)
        {
            this.allowRules.AddRange(rules);
        }
    }

    /// <summary>Adds deny rules to the live set.</summary>
    public void AddDeny(IEnumerable<PermissionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        lock (this.gate)
        {
            this.denyRules.AddRange(rules);
        }
    }

    /// <summary>
    /// Returns the string form of the first rule matching <paramref name="toolName"/> and
    /// <paramref name="inputJson"/>, prefixed with its list: <c>"deny:rule"</c> or
    /// <c>"allow:rule"</c>. Deny rules are checked first because deny always wins.
    /// Returns <see langword="null"/> when no rule matches.
    /// </summary>
    /// <param name="toolName">The name of the tool being called.</param>
    /// <param name="inputJson">The raw JSON arguments for the tool call.</param>
    public string? FindMatchedRule(string toolName, string inputJson)
    {
        PermissionRule[] deny;
        PermissionRule[] allow;
        lock (this.gate)
        {
            deny = [.. this.denyRules];
            allow = [.. this.allowRules];
        }

        foreach (var rule in deny)
        {
            if (rule.Matches(toolName, inputJson))
            {
                return $"deny:{Format(rule)}";
            }
        }

        foreach (var rule in allow)
        {
            if (rule.Matches(toolName, inputJson))
            {
                return $"allow:{Format(rule)}";
            }
        }

        return null;
    }

    private static string Format(PermissionRule rule) =>
        rule.ArgPattern is null ? rule.ToolName : $"{rule.ToolName}({rule.ArgPattern})";
}
