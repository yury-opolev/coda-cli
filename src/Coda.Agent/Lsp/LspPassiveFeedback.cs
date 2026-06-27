using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Coda.Agent.Lsp;

/// <summary>
/// Bridges LSP <c>textDocument/publishDiagnostics</c> notifications into the
/// <see cref="LspDiagnosticRegistry"/> so the agent loop can surface compiler
/// diagnostics for files it edits.
/// </summary>
public static partial class LspPassiveFeedback
{
    private const int FailureWarningThreshold = 3;

    [LoggerMessage(Level = LogLevel.Debug, Message = "repeated LSP publishDiagnostics handler failures (best-effort; isolated, never propagated): server={server}, consecutiveFailures={failures}")]
    private static partial void LogRepeatedHandlerFailures(ILogger logger, string server, int failures, Exception ex);

    /// <summary>
    /// Converts an LSP <c>publishDiagnostics</c> params node into the registry's
    /// <see cref="DiagnosticFile"/> shape. Returns an empty list when the params are
    /// malformed or carry no usable diagnostics.
    /// </summary>
    public static IReadOnlyList<DiagnosticFile> FormatDiagnosticsForAttachment(JsonNode publishParams)
    {
        if (publishParams is not JsonObject paramsObject)
        {
            return [];
        }

        if (paramsObject["uri"] is not JsonValue uriValue || uriValue.GetValueKind() != System.Text.Json.JsonValueKind.String)
        {
            return [];
        }

        if (paramsObject["diagnostics"] is not JsonArray diagnosticsArray)
        {
            return [];
        }

        var rawUri = uriValue.GetValue<string>();
        var uri = NormalizeUri(rawUri);

        var diagnostics = new List<LspDiagnostic>();
        foreach (var node in diagnosticsArray)
        {
            if (node is not JsonObject diagnosticObject)
            {
                continue;
            }

            var diagnostic = ParseDiagnostic(diagnosticObject);
            if (diagnostic is not null)
            {
                diagnostics.Add(diagnostic);
            }
        }

        return [new DiagnosticFile(uri, diagnostics)];
    }

    /// <summary>
    /// Registers a <c>textDocument/publishDiagnostics</c> handler on every server in
    /// <paramref name="manager"/>, routing valid diagnostics into <paramref name="registry"/>.
    /// Each server's handler is fully isolated: a malformed notification from one server
    /// can never throw out of the handler or affect another server.
    /// </summary>
    public static void RegisterNotificationHandlers(
        LspServerManager manager,
        LspDiagnosticRegistry registry,
        ILogger? logger = null)
    {
        var consecutiveFailures = new ConcurrentDictionary<string, int>();

        foreach (var (serverName, server) in manager.GetAllServers())
        {
            server.OnNotification("textDocument/publishDiagnostics", parameters =>
            {
                try
                {
                    if (parameters is null)
                    {
                        return;
                    }

                    var files = FormatDiagnosticsForAttachment(parameters);
                    var firstFile = files.Count > 0 ? files[0] : null;
                    if (firstFile is null || firstFile.Diagnostics.Count == 0)
                    {
                        return;
                    }

                    registry.RegisterPending(serverName, files);
                    consecutiveFailures[serverName] = 0;
                }
                catch (Exception ex)
                {
                    // Isolate: one server's malformed notification must never break others.
                    var failures = consecutiveFailures.AddOrUpdate(serverName, 1, (_, count) => count + 1);
                    if (failures >= FailureWarningThreshold && logger is not null)
                    {
                        // Repeated failures observed; surface at Debug (the handler still never crashes).
                        LogRepeatedHandlerFailures(logger, serverName, failures, ex);
                    }
                }
            });
        }
    }

    private static string NormalizeUri(string rawUri)
    {
        if (!rawUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return rawUri;
        }

        try
        {
            return new Uri(rawUri).LocalPath;
        }
        catch (UriFormatException)
        {
            return rawUri;
        }
    }

    private static LspDiagnostic? ParseDiagnostic(JsonObject diagnostic)
    {
        if (diagnostic["message"] is not JsonValue messageValue || messageValue.GetValueKind() != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        var message = messageValue.GetValue<string>();
        var severity = MapSeverity(diagnostic["severity"]);
        var range = ParseRange(diagnostic["range"]);
        var source = (diagnostic["source"] as JsonValue)?.GetValue<string>();
        var code = StringifyCode(diagnostic["code"]);

        return new LspDiagnostic(message, severity, range, source, code);
    }

    private static LspDiagnosticSeverity MapSeverity(JsonNode? severityNode)
    {
        if (severityNode is JsonValue value && value.TryGetValue(out int severity))
        {
            return severity switch
            {
                1 => LspDiagnosticSeverity.Error,
                2 => LspDiagnosticSeverity.Warning,
                3 => LspDiagnosticSeverity.Info,
                4 => LspDiagnosticSeverity.Hint,
                _ => LspDiagnosticSeverity.Error,
            };
        }

        return LspDiagnosticSeverity.Error;
    }

    private static LspRange ParseRange(JsonNode? rangeNode)
    {
        if (rangeNode is JsonObject rangeObject)
        {
            var start = ParsePosition(rangeObject["start"]);
            var end = ParsePosition(rangeObject["end"]);
            return new LspRange(start, end);
        }

        var zero = new LspPosition(0, 0);
        return new LspRange(zero, zero);
    }

    private static LspPosition ParsePosition(JsonNode? positionNode)
    {
        if (positionNode is JsonObject positionObject)
        {
            var line = ReadInt(positionObject["line"]);
            var character = ReadInt(positionObject["character"]);
            return new LspPosition(line, character);
        }

        return new LspPosition(0, 0);
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue(out int result))
        {
            return result;
        }

        return 0;
    }

    private static string? StringifyCode(JsonNode? codeNode)
    {
        if (codeNode is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => value.ToJsonString(),
            _ => null,
        };
    }
}
