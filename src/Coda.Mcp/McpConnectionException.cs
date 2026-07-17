using System.Globalization;

namespace Coda.Mcp;

/// <summary>
/// A typed failure while <em>establishing</em> a connection to an MCP server, as opposed to a
/// mid-session <see cref="McpException"/>. <see cref="Phase"/> names the JSON-RPC method in flight
/// (the startup handshake is <c>initialize</c> then <c>tools/list</c>; a manager that cannot
/// attribute the failure to a single method may use <c>initialize/tools/list</c>).
/// <para>
/// Use the factory methods so every failure kind gets its exact, user-facing message. Any stderr
/// passed to <see cref="ProcessExited"/> must already be sanitized/trimmed by the caller — this
/// type appends it verbatim and performs no logging of its own.
/// </para>
/// </summary>
public sealed class McpConnectionException : McpException
{
    private McpConnectionException(string message, string phase, Exception? inner = null)
        : base(message, inner)
    {
        this.Phase = phase;
    }

    /// <summary>The JSON-RPC method in flight when the connection failed (e.g. <c>initialize</c>).</summary>
    public string Phase { get; }

    /// <summary>The connection timed out during <paramref name="phase"/> after <paramref name="timeout"/>.</summary>
    public static McpConnectionException Timeout(string server, string phase, TimeSpan timeout) =>
        new(
            $"MCP server '{server}' timed out during {phase} after {FormatSeconds(timeout)}s.",
            phase);

    /// <summary>The caller canceled the connection attempt during <paramref name="phase"/>.</summary>
    public static McpConnectionException Canceled(string server, string phase, Exception? innerException = null) =>
        new(
            $"MCP server '{server}' was canceled during {phase}.",
            phase,
            innerException);

    /// <summary>
    /// The server process exited during <paramref name="phase"/> with <paramref name="exitCode"/>.
    /// When <paramref name="stderr"/> is non-empty (already sanitized/trimmed) it is appended as a
    /// <c>Stderr:</c> tail.
    /// </summary>
    public static McpConnectionException ProcessExited(string server, string phase, int exitCode, string? stderr)
    {
        var message = $"MCP server '{server}' exited during {phase} with exit code {exitCode}.";
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            message += $" Stderr: {stderr}";
        }

        return new McpConnectionException(message, phase);
    }

    /// <summary>Invariant seconds that keeps useful fractional precision (e.g. <c>1.5</c>, <c>60</c>).</summary>
    private static string FormatSeconds(TimeSpan timeout) =>
        timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture);
}
