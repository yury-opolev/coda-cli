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

    /// <summary>
    /// Guards <see cref="discovered"/>. One coordinator instance is shared by every loop a session
    /// builds, and a scheduled turn runs on a background task concurrently with a main turn — so
    /// reads (<c>Contains</c>, snapshots) genuinely race writes (<c>Add</c>/<c>Remove</c>/<c>Clear</c>),
    /// which <see cref="HashSet{T}"/> does not tolerate.
    /// </summary>
    private readonly Lock gate = new();

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
    ///   <item><see cref="ToolSearchMode.TstAuto"/> → true only when the total character size of the
    ///     registry's tool definitions meets or exceeds <c>autoPercent</c>% of the context window
    ///     (char-based heuristic).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The TstAuto measure covers <em>every</em> tool definition, not only the deferrable ones.
    /// Always-inline tools consume the same context window — the <c>skill</c> tool's catalogue
    /// description alone can reach ~8 000 characters — so excluding them would let a large inline
    /// footprint sit under the threshold while the deferrable tools it is competing with stay on
    /// the wire. Deferred tools remain the only ones that can actually be reclaimed.
    /// </remarks>
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
                var definitionChars = registry.All
                    .Sum(t => t.Name.Length + t.Description.Length + t.InputSchemaJson.Length);
                var thresholdChars = (int)Math.Floor(this.contextWindowTokens * (this.autoPercent / 100.0) * CharsPerToken);
                return definitionChars >= thresholdChars;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// Adds tool names to the discovered set.
    /// </summary>
    public void AddDiscovered(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        lock (this.gate)
        {
            foreach (var name in names)
            {
                this.discovered.Add(name);
            }
        }
    }

    /// <summary>
    /// Removes a tool from the discovered set, returning it to deferred state. Returns false when
    /// it was not discovered.
    /// </summary>
    /// <remarks>
    /// Discovery used to be add-only, which turned a single tool definition the model API refuses
    /// into a permanently unusable session: once discovered, the bad definition was re-sent on
    /// every subsequent request and there was no way to take it back. Eviction is what makes that
    /// recoverable in-process.
    /// </remarks>
    public bool RemoveDiscovered(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (this.gate)
        {
            return this.discovered.Remove(name);
        }
    }

    /// <summary>
    /// Clears all discovery state (e.g. on <c>/clear</c>), so the deferred tools return to their
    /// unloaded state along with the conversation they were loaded for.
    /// </summary>
    public void ResetDiscovered()
    {
        lock (this.gate)
        {
            this.discovered.Clear();
        }
    }

    /// <summary>A snapshot of the currently discovered tool names.</summary>
    public IReadOnlyCollection<string> Discovered => this.SnapshotDiscovered();

    /// <summary>
    /// A private copy of the discovered set, taken under the lock so callers can enumerate it
    /// freely while another turn mutates the original.
    /// </summary>
    private HashSet<string> SnapshotDiscovered()
    {
        lock (this.gate)
        {
            return [.. this.discovered];
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

        var snapshot = this.SnapshotDiscovered();
        return [.. registry.All
            .Where(t => !DeferredTools.IsDeferred(t) || snapshot.Contains(t.Name))
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

        var snapshot = this.SnapshotDiscovered();
        var undiscovered = registry.All
            .Where(t => DeferredTools.IsDeferred(t) && !snapshot.Contains(t.Name))
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
