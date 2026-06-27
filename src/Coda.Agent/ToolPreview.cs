namespace Coda.Agent;

/// <summary>Collapses a tool's raw input JSON to a one-line preview for human display.</summary>
public static class ToolPreview
{
    public static string Compact(string inputJson, int maxLength = 120)
    {
        var compact = inputJson.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return compact.Length > maxLength ? compact[..maxLength] + "…" : compact;
    }
}
