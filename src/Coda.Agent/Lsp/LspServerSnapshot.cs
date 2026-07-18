namespace Coda.Agent.Lsp;

/// <summary>An immutable, UI-facing view of a single LSP server's identity, state and handled extensions.</summary>
public sealed record LspServerSnapshot(string Name, LspServerState State, IReadOnlyList<string> Extensions);
