namespace Coda.Agent.Lsp;

public sealed record LspDiagnostic(
    string Message,
    LspDiagnosticSeverity Severity,
    LspRange Range,
    string? Source,
    string? Code);
