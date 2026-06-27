namespace Coda.Agent.ToolSearch;

/// <summary>
/// Controls how deferred tools are surfaced to the model.
/// <list type="bullet">
///   <item><term>Tst</term><description>Tool Search Tool — deferred tools discovered via tool_search (always enabled).</description></item>
///   <item><term>TstAuto</term><description>Auto — tools deferred only when they exceed a size threshold.</description></item>
///   <item><term>Standard</term><description>Tool search disabled — all tools exposed inline.</description></item>
/// </list>
/// </summary>
public enum ToolSearchMode
{
    Tst,
    TstAuto,
    Standard,
}
