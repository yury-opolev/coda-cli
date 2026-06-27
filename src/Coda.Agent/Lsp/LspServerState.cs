namespace Coda.Agent.Lsp;

/// <summary>
/// Lifecycle state of a single LSP server instance.
/// </summary>
public enum LspServerState
{
    Stopped,
    Starting,
    Running,
    Error
}
