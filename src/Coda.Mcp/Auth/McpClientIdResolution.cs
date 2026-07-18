namespace Coda.Mcp.Auth;

/// <summary>
/// The outcome of resolving an OAuth client id for the MCP authorization flow: either a
/// usable <see cref="ClientId"/> or an actionable, secret-free <see cref="Error"/>. Instances
/// are built through <see cref="Success"/> or <see cref="Failure"/> so a result always carries
/// exactly one of the two, making a malformed (both-null or both-set) state unrepresentable.
/// </summary>
internal sealed record McpClientIdResolution
{
    private McpClientIdResolution(string? clientId, string? error)
    {
        this.ClientId = clientId;
        this.Error = error;
    }

    /// <summary>The resolved client id, or <see langword="null"/> when resolution failed.</summary>
    public string? ClientId { get; }

    /// <summary>An actionable failure message, or <see langword="null"/> when resolution succeeded.</summary>
    public string? Error { get; }

    /// <summary>A successful resolution carrying a non-empty client id.</summary>
    public static McpClientIdResolution Success(string clientId)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        return new McpClientIdResolution(clientId, null);
    }

    /// <summary>A failed resolution carrying a non-empty, actionable error message.</summary>
    public static McpClientIdResolution Failure(string error)
    {
        ArgumentException.ThrowIfNullOrEmpty(error);
        return new McpClientIdResolution(null, error);
    }
}
