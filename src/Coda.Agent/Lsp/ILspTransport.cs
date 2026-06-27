namespace Coda.Agent.Lsp;

/// <summary>
/// Abstracts a started LSP server reachable over two byte streams.
/// Inject this seam to test LspClient without spawning a real process.
/// </summary>
public interface ILspTransport : IAsyncDisposable
{
    /// <summary>Server stdout — we READ from this.</summary>
    Stream Input { get; }

    /// <summary>Server stdin — we WRITE to this.</summary>
    Stream Output { get; }
}
