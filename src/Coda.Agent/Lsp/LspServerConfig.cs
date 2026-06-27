using System.Text.Json.Nodes;

namespace Coda.Agent.Lsp;

/// <summary>
/// Configuration for a single LSP server, loaded from the <c>lspServers</c>
/// section of a settings.json file.
/// </summary>
public sealed record LspServerConfig(
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> ExtensionToLanguage,
    IReadOnlyDictionary<string, string>? Env,
    JsonNode? InitializationOptions,
    int? StartupTimeoutMs);
