namespace Coda.Agent.ToolSearch;

/// <summary>
/// Helpers for identifying deferred tools — tools that are hidden from the
/// inline tool list and discovered on demand via <c>tool_search</c>.
/// </summary>
public static class DeferredTools
{
    /// <summary>
    /// Returns true when <paramref name="tool"/> should be deferred: its
    /// <see cref="ITool.ShouldDefer"/> is true AND its name is not
    /// <see cref="ToolSearchToolNames.ToolSearch"/> (the search tool itself
    /// must never be deferred, otherwise it cannot be called to discover others).
    /// </summary>
    public static bool IsDeferred(ITool tool)
        => tool.ShouldDefer && tool.Name != ToolSearchToolNames.ToolSearch;
}
