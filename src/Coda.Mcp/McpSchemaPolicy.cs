namespace Coda.Mcp;

/// <summary>
/// What to do about an MCP server that advertises a tool whose <c>inputSchema</c> the model
/// APIs would reject. Configured with <c>"mcpSchemaPolicy"</c> in <c>settings.json</c>.
/// </summary>
public enum McpSchemaPolicy
{
    /// <summary>
    /// Default. Repair the schema (keeping every advertised key) and keep the tool. A tool that
    /// may not accept arguments correctly is strictly better than one that cannot be used at all.
    /// </summary>
    Coerce,

    /// <summary>Drop the affected tools from the registry; the server's other tools still work.</summary>
    Skip,

    /// <summary>Refuse to register the server at all, surfacing a hard error at connect time.</summary>
    Strict,
}
