using System.Text.Json;
using System.Text.Json.Nodes;
using LlmClient;

namespace Coda.Sdk;

/// <summary>
/// Exports a session (lean transcript merged with the audit sidecar, when available) to a single
/// portable bundle file, and imports a bundle back into a local session (lean transcript plus a
/// reconstructed audit sidecar). See <see cref="SessionBundle"/> for the in-memory shape; on disk
/// it is a single JSON object whose <c>"schema"</c> field is validated on import.
/// </summary>
public sealed class SessionBundleService(string workingDirectory, string codaVersion)
{
    private const string SchemaPrefix = "coda.session/";
    private const int SupportedSchemaMajor = 1;

    private static readonly JsonSerializerOptions CompactJsonOptions = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions PrettyJsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Builds a portable bundle for <paramref name="sessionId"/>: the lean transcript's turns,
    /// enriched with the audit sidecar's per-turn usage/stop-reason/timestamp when a sidecar
    /// exists (aligning the k-th assistant message to audit turn k). Returns <c>null</c> if no
    /// transcript is persisted for <paramref name="sessionId"/>. Never throws.
    /// </summary>
    public async Task<SessionBundle?> ExportAsync(string sessionId, DateTime exportedUtc, CancellationToken ct = default)
    {
        var transcriptStore = new SessionTranscriptStore(workingDirectory);
        var messages = await transcriptStore.LoadAsync(sessionId, ct).ConfigureAwait(false);
        if (messages is null)
        {
            return null;
        }

        var createdUtc = exportedUtc;
        var summaries = await transcriptStore.ListAsync(ct).ConfigureAwait(false);
        foreach (var summary in summaries)
        {
            if (string.Equals(summary.Id, sessionId, StringComparison.Ordinal))
            {
                createdUtc = summary.CreatedUtc;
                break;
            }
        }

        var auditStore = new SessionAuditStore(workingDirectory);
        var auditAvailable = auditStore.Exists(sessionId);
        var auditTurns = auditAvailable
            ? await auditStore.LoadAsync(sessionId, ct).ConfigureAwait(false)
            : (IReadOnlyList<SessionAuditTurn>)[];

        var turns = new List<SessionBundleTurn>(messages.Count);
        var assistantIndex = 0;
        foreach (var message in messages)
        {
            var role = message.Role == ChatRole.User ? "user" : "assistant";
            if (message.Role != ChatRole.Assistant)
            {
                turns.Add(new SessionBundleTurn { Role = role, Blocks = message.Content });
                continue;
            }

            // Both the transcript and the audit sidecar record one entry per user/assistant turn,
            // so the k-th assistant message aligns to audit turn k. An older or edited session can
            // have the two counts disagree — enrich what aligns and leave the rest block-only.
            var auditTurn = assistantIndex < auditTurns.Count ? auditTurns[assistantIndex] : null;
            turns.Add(new SessionBundleTurn
            {
                Role = role,
                TsUtc = auditTurn?.TsUtc,
                InputTokens = auditTurn?.InputTokens,
                OutputTokens = auditTurn?.OutputTokens,
                StopReason = auditTurn?.StopReason,
                Blocks = message.Content,
            });
            assistantIndex++;
        }

        string? systemPrompt = null;
        IReadOnlyList<ToolDefinition> toolDefs = [];
        string? provider = null;
        string? model = null;
        if (auditTurns.Count > 0)
        {
            var lastAuditTurn = auditTurns[^1];
            systemPrompt = lastAuditTurn.SystemPrompt;
            toolDefs = lastAuditTurn.ToolDefs;
            provider = lastAuditTurn.Provider;
            model = lastAuditTurn.Model;
        }

        return new SessionBundle
        {
            CodaVersion = codaVersion,
            ExportedUtc = exportedUtc,
            Id = sessionId,
            CreatedUtc = createdUtc,
            Provider = provider,
            Model = model,
            AuditAvailable = auditAvailable,
            SystemPrompt = systemPrompt,
            ToolDefs = toolDefs,
            Turns = turns,
        };
    }

    /// <summary>
    /// Serializes <paramref name="bundle"/> to <paramref name="outPath"/> (compact unless
    /// <paramref name="pretty"/> is set), creating the parent directory if needed. Returns
    /// <paramref name="outPath"/>.
    /// </summary>
    public async Task<string> WriteAsync(SessionBundle bundle, string outPath, bool pretty, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var root = SerializeBundle(bundle);
        var options = pretty ? PrettyJsonOptions : CompactJsonOptions;
        await File.WriteAllTextAsync(outPath, root.ToJsonString(options), ct).ConfigureAwait(false);
        return outPath;
    }

    /// <summary>
    /// Reads a bundle from <paramref name="bundlePath"/> and writes it back as a local session:
    /// the lean transcript (always), plus a reconstructed audit sidecar when the bundle carries
    /// one. If a transcript already exists locally under the bundle's id, a new id is minted.
    /// Returns the (possibly new) local id.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The bundle is not a JSON object, has no <c>"schema"</c>/<c>"id"</c>, or its schema major
    /// version is not one this build of coda-cli understands. This is the only persistence path in
    /// this type that is allowed to throw.
    /// </exception>
    public async Task<string> ImportAsync(string bundlePath, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(bundlePath, ct).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"'{bundlePath}' is not a valid session bundle: the file is not a JSON object.");

        var schema = root["schema"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"'{bundlePath}' is not a valid session bundle: it has no 'schema' field.");
        ValidateSchema(schema, bundlePath);

        var bundle = DeserializeBundle(root, schema);

        var transcriptStore = new SessionTranscriptStore(workingDirectory);
        var targetId = await transcriptStore.LoadAsync(bundle.Id, ct).ConfigureAwait(false) is not null
            ? Guid.NewGuid().ToString("N")[..12]
            : bundle.Id;

        var messages = new List<ChatMessage>(bundle.Turns.Count);
        foreach (var turn in bundle.Turns)
        {
            var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;
            messages.Add(new ChatMessage(role, turn.Blocks));
        }

        await transcriptStore.SaveAsync(targetId, messages, ct).ConfigureAwait(false);

        if (bundle.AuditAvailable)
        {
            var auditStore = new SessionAuditStore(workingDirectory);
            var assistantIndex = 0;
            foreach (var turn in bundle.Turns)
            {
                if (!string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Per-turn system-prompt/tool-defs change history is not preserved across
                // export/import (known v1 limitation) — the effective final values are stored
                // top-level and reattached to the first turn; change-only emission on append then
                // carries them forward for the rest.
                var auditTurn = new SessionAuditTurn
                {
                    TurnIndex = assistantIndex,
                    TsUtc = turn.TsUtc ?? bundle.ExportedUtc,
                    Provider = bundle.Provider ?? string.Empty,
                    Model = bundle.Model ?? string.Empty,
                    InputTokens = turn.InputTokens ?? 0,
                    OutputTokens = turn.OutputTokens ?? 0,
                    StopReason = turn.StopReason,
                    SystemPrompt = assistantIndex == 0 ? bundle.SystemPrompt : null,
                    ToolDefs = assistantIndex == 0 ? bundle.ToolDefs : [],
                };

                await auditStore.AppendTurnAsync(targetId, auditTurn, ct).ConfigureAwait(false);
                assistantIndex++;
            }
        }

        return targetId;
    }

    // ── Schema validation ──────────────────────────────────────────────────────

    private static void ValidateSchema(string schema, string bundlePath)
    {
        if (!schema.StartsWith(SchemaPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{bundlePath}' has an unrecognized session bundle schema '{schema}': expected a '{SchemaPrefix}*' schema.");
        }

        var versionPart = schema[SchemaPrefix.Length..];
        var majorDigits = new string([.. versionPart.TakeWhile(char.IsDigit)]);
        if (!int.TryParse(majorDigits, out var major) || major != SupportedSchemaMajor)
        {
            throw new InvalidOperationException(
                $"'{bundlePath}' has an unsupported session bundle schema version '{schema}': this build of coda-cli supports schema major version {SupportedSchemaMajor}.");
        }
    }

    // ── Serialization ──────────────────────────────────────────────────────────

    private static JsonObject SerializeBundle(SessionBundle bundle) => new()
    {
        ["schema"] = bundle.Schema,
        ["codaVersion"] = bundle.CodaVersion,
        ["exportedUtc"] = bundle.ExportedUtc.ToString("O"),
        ["id"] = bundle.Id,
        ["createdUtc"] = bundle.CreatedUtc.ToString("O"),
        ["provider"] = bundle.Provider,
        ["model"] = bundle.Model,
        ["auditAvailable"] = bundle.AuditAvailable,
        ["systemPrompt"] = bundle.SystemPrompt,
        ["toolDefs"] = SerializeToolDefs(bundle.ToolDefs),
        ["turns"] = SerializeTurns(bundle.Turns),
    };

    private static JsonArray SerializeTurns(IReadOnlyList<SessionBundleTurn> turns)
    {
        var array = new JsonArray();
        foreach (var turn in turns)
        {
            array.Add(new JsonObject
            {
                ["role"] = turn.Role,
                ["tsUtc"] = turn.TsUtc?.ToString("O"),
                ["inputTokens"] = turn.InputTokens,
                ["outputTokens"] = turn.OutputTokens,
                ["stopReason"] = turn.StopReason,
                ["blocks"] = ChatMessageJson.SerializeBlocks(turn.Blocks),
            });
        }

        return array;
    }

    private static JsonArray SerializeToolDefs(IReadOnlyList<ToolDefinition> toolDefs)
    {
        var array = new JsonArray();
        foreach (var def in toolDefs)
        {
            array.Add(new JsonObject
            {
                ["name"] = def.Name,
                ["description"] = def.Description,
                ["inputSchema"] = def.InputSchemaJson,
            });
        }

        return array;
    }

    private static SessionBundle DeserializeBundle(JsonObject root, string schema)
    {
        var codaVersion = root["codaVersion"]?.GetValue<string>() ?? string.Empty;
        var exportedUtc = ParseDateTime(root["exportedUtc"]) ?? DateTime.UtcNow;
        var id = root["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("session bundle is missing an 'id'.");
        var createdUtc = ParseDateTime(root["createdUtc"]) ?? exportedUtc;
        var provider = root["provider"]?.GetValue<string>();
        var model = root["model"]?.GetValue<string>();
        var auditAvailable = root["auditAvailable"]?.GetValue<bool>() ?? false;
        var systemPrompt = root["systemPrompt"]?.GetValue<string>();
        var toolDefsArray = root["toolDefs"]?.AsArray();
        var toolDefs = toolDefsArray is not null ? DeserializeToolDefs(toolDefsArray) : (IReadOnlyList<ToolDefinition>)[];
        var turnsArray = root["turns"]?.AsArray() ?? new JsonArray();
        var turns = DeserializeTurns(turnsArray);

        return new SessionBundle
        {
            Schema = schema,
            CodaVersion = codaVersion,
            ExportedUtc = exportedUtc,
            Id = id,
            CreatedUtc = createdUtc,
            Provider = provider,
            Model = model,
            AuditAvailable = auditAvailable,
            SystemPrompt = systemPrompt,
            ToolDefs = toolDefs,
            Turns = turns,
        };
    }

    private static IReadOnlyList<SessionBundleTurn> DeserializeTurns(JsonArray array)
    {
        var turns = new List<SessionBundleTurn>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var blocksArray = obj["blocks"]?.AsArray();
            var blocks = blocksArray is not null ? ChatMessageJson.DeserializeBlocks(blocksArray) : (IReadOnlyList<ContentBlock>)[];

            turns.Add(new SessionBundleTurn
            {
                Role = obj["role"]?.GetValue<string>() ?? "user",
                TsUtc = ParseDateTime(obj["tsUtc"]),
                InputTokens = obj["inputTokens"] is JsonValue inputTokensValue ? inputTokensValue.GetValue<int>() : null,
                OutputTokens = obj["outputTokens"] is JsonValue outputTokensValue ? outputTokensValue.GetValue<int>() : null,
                StopReason = obj["stopReason"]?.GetValue<string>(),
                Blocks = blocks,
            });
        }

        return turns;
    }

    private static IReadOnlyList<ToolDefinition> DeserializeToolDefs(JsonArray array)
    {
        var list = new List<ToolDefinition>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            list.Add(new ToolDefinition(
                obj["name"]?.GetValue<string>() ?? string.Empty,
                obj["description"]?.GetValue<string>() ?? string.Empty,
                obj["inputSchema"]?.GetValue<string>() ?? string.Empty));
        }

        return list;
    }

    private static DateTime? ParseDateTime(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        var raw = value.GetValue<string>();
        return DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }
}
