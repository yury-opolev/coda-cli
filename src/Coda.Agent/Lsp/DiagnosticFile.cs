namespace Coda.Agent.Lsp;

public sealed record DiagnosticFile(string Uri, IReadOnlyList<LspDiagnostic> Diagnostics);
