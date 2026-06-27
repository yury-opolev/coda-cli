using System.Text;
using Coda.Common;
using LlmClient;

namespace Coda.Agent.ToolSearch;

/// <summary>
/// Manages which tools are exposed on the wire and constructs the deferred-tools
/// system-reminder block for modes where tool search is active.
/// </summary>
public sealed class ToolSearchCoordinator
{
    /// <summary>Approximate characters per token for MCP tool definitions (reference CHARS_PER_TOKEN).</summary>
    private const double CharsPerToken = 2.5;

    private readonly ToolSearchMode mode;
    private readonly int autoPercent;
    private readonly int contextWindowTokens;
    private readonly HashSet<string> discovered = new(StringComparer.Ordinal);

    public ToolSearchCoordinator(ToolSearchMode mode, int autoPercent = 10, int contextWindowTokens = ContextWindow.DefaultTokens)
    {
        this.mode = mode;
        this.autoPercent = autoPercent;
        this.contextWindowTokens = contextWindowTokens;
    }

    /// <summary>True when the coordinator is in an active tool-search mode (not Standard).</summary>
    public bool IsActive => this.mode != ToolSearchMode.Standard;

    /// <summary>
    /// Returns true when deferred tools should actually be hidden from the wire this turn.
    /// <list type="bullet">
    ///   <item><see cref="ToolSearchMode.Standard"/> → always false.</item>
    ///   <item><see cref="ToolSearchMode.Tst"/> → always true.</item>
    ///   <item><see cref="ToolSearchMode.TstAuto"/> → true only when the total character size of all
    ///     deferred tools meets or exceeds <c>autoPercent</c>% of the context window (char-based heuristic).</item>
    /// </list>
    /// </summary>
    private bool ShouldDeferNow(ToolRegistry registry)
    {
        switch (this.mode)
        {
            case ToolSearchMode.Standard:
                return false;
            case ToolSearchMode.Tst:
                return true;
            case ToolSearchMode.TstAuto:
            {
                var deferredChars = registry.All
                    .Where(DeferredTools.IsDeferred)
                    .Sum(t => t.Name.Length + t.Description.Length + t.InputSchemaJson.Length);
                var thresholdChars = (int)Math.Floor(this.contextWindowTokens * (this.autoPercent / 100.0) * CharsPerToken);
                return deferredChars >= thresholdChars;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// Adds tool names to the discovered set.  Thread-safe enough for single-threaded loops.
    /// </summary>
    public void AddDiscovered(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        foreach (var name in names)
        {
            this.discovered.Add(name);
        }
    }

    /// <summary>
    /// Returns the tool definitions to send on the wire for the current turn.
    /// <list type="bullet">
    ///   <item>Standard mode or TstAuto below threshold → all registry tools returned inline.</item>
    ///   <item>Tst, or TstAuto at/above threshold → deferred tools excluded unless already discovered.</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<ToolDefinition> BuildWireDefinitions(ToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (!this.ShouldDeferNow(registry))
        {
            return registry.Definitions;
        }

        return [.. registry.All
            .Where(t => !DeferredTools.IsDeferred(t) || this.discovered.Contains(t.Name))
            .Select(t => t.ToDefinition())];
    }

    /// <summary>
    /// Returns the &lt;deferred-tools&gt; reminder block listing not-yet-discovered deferred
    /// tools, or null when deferral is not active this turn or all deferred tools have been discovered.
    /// </summary>
    public string? BuildDeferredToolsReminder(ToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (!this.ShouldDeferNow(registry))
        {
            return null;
        }

        var undiscovered = registry.All
            .Where(t => DeferredTools.IsDeferred(t) && !this.discovered.Contains(t.Name))
            .ToList();

        if (undiscovered.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<deferred-tools>");
        sb.AppendLine(
            "The following tools are available but their schemas are not loaded. " +
            "Use the tool_search tool to load a tool's schema before calling it " +
            "(query by name with select:<name>, or by keywords).");
        foreach (var tool in undiscovered)
        {
            sb.AppendLine(tool.Name);
        }
        sb.Append("</deferred-tools>");

        return sb.ToString();
    }
}
