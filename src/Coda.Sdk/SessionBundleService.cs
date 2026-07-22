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
    /// Builds a portable bundle for <paramref name="sessionId"/>: the lean transcript's messages
    /// (role + blocks, in order) plus, when a sidecar exists, the audit turns carried VERBATIM as a
    /// separate top-level array. The transcript and the sidecar are not 1:1 (the agent loop appends
    /// several assistant messages per audit turn), so they are kept as independent lists rather than
    /// interleaved. Returns <c>null</c> if no transcript is persisted for <paramref name="sessionId"/>.
    /// Never throws.
    /// </summary>
    public async Task<SessionBundle?> ExportAsync(string sessionId, DateTime exportedUtc, CancellationToken ct = default)
    {
        var transcriptStore = new SessionTranscriptStore(workingDirectory);
        var storedSession = await transcriptStore.LoadSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (storedSession is null)
        {
            return null;
        }

        var messages = storedSession.Messages;
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
        foreach (var message in messages)
        {
            var role = message.Role == ChatRole.User ? "user" : "assistant";
            turns.Add(new SessionBundleTurn { Role = role, Blocks = message.Content });
        }

        // Effective final system prompt / tool defs / provider / model come from the last audit
        // turn (LoadAsync carries them forward, so the last turn holds the effective values).
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
            SystemPromptOverride = storedSession.Metadata.SystemPromptOverride,
            ToolDefs = toolDefs,
            Turns = turns,
            AuditTurns = auditTurns,
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
            ? SessionIds.NewId()
            : bundle.Id;

        var messages = new List<ChatMessage>(bundle.Turns.Count);
        foreach (var turn in bundle.Turns)
        {
            var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;
            messages.Add(new ChatMessage(role, turn.Blocks));
        }

        await transcriptStore.SaveAsync(
            targetId,
            messages,
            new SessionMetadata { SystemPromptOverride = bundle.SystemPromptOverride },
            ct).ConfigureAwait(false);

        // Replay the audit turns verbatim. Each turn already carries its effective per-turn system
        // prompt / tool defs, so AppendTurnAsync's change-only emission re-derives the exact on-disk
        // compaction — mid-session prompt/tool changes round-trip losslessly, and there is one audit
        // line per user turn (not per assistant message).
        if (bundle.AuditTurns.Count > 0)
        {
            var auditStore = new SessionAuditStore(workingDirectory);
            foreach (var auditTurn in bundle.AuditTurns)
            {
                await auditStore.AppendTurnAsync(targetId, auditTurn, ct).ConfigureAwait(false);
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

    private static JsonObject SerializeBundle(SessionBundle bundle)
    {
        var root = new JsonObject
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
        };
        if (bundle.SystemPromptOverride is not null)
        {
            root["systemPromptOverride"] = bundle.SystemPromptOverride;
        }

        root["toolDefs"] = AuditJson.SerializeToolDefs(bundle.ToolDefs);
        root["turns"] = SerializeTurns(bundle.Turns);
        root["auditTurns"] = SerializeAuditTurns(bundle.AuditTurns);
        return root;
    }

    private static JsonArray SerializeTurns(IReadOnlyList<SessionBundleTurn> turns)
    {
        var array = new JsonArray();
        foreach (var turn in turns)
        {
            array.Add(new JsonObject
            {
                ["role"] = turn.Role,
                ["blocks"] = ChatMessageJson.SerializeBlocks(turn.Blocks),
            });
        }

        return array;
    }

    // Audit turns are serialized in the same field shape SessionAuditStore writes to the sidecar
    // (turnIndex, tsUtc "O", provider, model, usage:{in,out}, stopReason, toolCalls, systemPrompt,
    // toolDefs) so a bundle round-trips the sidecar exactly. Each turn here carries its effective
    // (carried-forward) systemPrompt/toolDefs, so every entry emits them in full.
    private static JsonArray SerializeAuditTurns(IReadOnlyList<SessionAuditTurn> auditTurns)
    {
        var array = new JsonArray();
        foreach (var turn in auditTurns)
        {
            array.Add(new JsonObject
            {
                ["turnIndex"] = turn.TurnIndex,
                ["tsUtc"] = turn.TsUtc.ToString("O"),
                ["provider"] = turn.Provider,
                ["model"] = turn.Model,
                ["usage"] = new JsonObject
                {
                    ["in"] = turn.InputTokens,
                    ["out"] = turn.OutputTokens,
                },
                ["stopReason"] = turn.StopReason,
                ["toolCalls"] = AuditJson.SerializeToolCalls(turn.ToolCalls),
                ["systemPrompt"] = turn.SystemPrompt,
                ["toolDefs"] = AuditJson.SerializeToolDefs(turn.ToolDefs),
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
        var systemPromptOverride = root["systemPromptOverride"] is JsonValue systemPromptOverrideValue
            && systemPromptOverrideValue.TryGetValue<string>(out var overrideValue)
            ? overrideValue
            : null;
        var toolDefsArray = root["toolDefs"]?.AsArray();
        var toolDefs = toolDefsArray is not null ? AuditJson.DeserializeToolDefs(toolDefsArray) : (IReadOnlyList<ToolDefinition>)[];
        var turnsArray = root["turns"]?.AsArray() ?? new JsonArray();
        var turns = DeserializeTurns(turnsArray);
        var auditTurnsArray = root["auditTurns"]?.AsArray();
        var auditTurns = auditTurnsArray is not null ? DeserializeAuditTurns(auditTurnsArray) : (IReadOnlyList<SessionAuditTurn>)[];

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
            SystemPromptOverride = systemPromptOverride,
            ToolDefs = toolDefs,
            Turns = turns,
            AuditTurns = auditTurns,
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
                Blocks = blocks,
            });
        }

        return turns;
    }

    private static IReadOnlyList<SessionAuditTurn> DeserializeAuditTurns(JsonArray array)
    {
        var turns = new List<SessionAuditTurn>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var usage = obj["usage"]?.AsObject();
            var inputTokens = usage?["in"] is JsonValue inValue ? inValue.GetValue<int>() : 0;
            var outputTokens = usage?["out"] is JsonValue outValue ? outValue.GetValue<int>() : 0;

            var toolCallsArray = obj["toolCalls"]?.AsArray();
            var toolCalls = toolCallsArray is not null ? AuditJson.DeserializeToolCalls(toolCallsArray) : (IReadOnlyList<ToolCallRecord>)[];
            var toolDefsArray = obj["toolDefs"]?.AsArray();
            var toolDefs = toolDefsArray is not null ? AuditJson.DeserializeToolDefs(toolDefsArray) : (IReadOnlyList<ToolDefinition>)[];

            turns.Add(new SessionAuditTurn
            {
                TurnIndex = obj["turnIndex"] is JsonValue turnIndexValue ? turnIndexValue.GetValue<int>() : 0,
                TsUtc = ParseDateTime(obj["tsUtc"]) ?? DateTime.UnixEpoch,
                Provider = obj["provider"]?.GetValue<string>() ?? string.Empty,
                Model = obj["model"]?.GetValue<string>() ?? string.Empty,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                StopReason = obj["stopReason"]?.GetValue<string>(),
                ToolCalls = toolCalls,
                SystemPrompt = obj["systemPrompt"]?.GetValue<string>(),
                ToolDefs = toolDefs,
            });
        }

        return turns;
    }

    private static DateTime? ParseDateTime(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        try
        {
            var raw = value.GetValue<string>();
            return DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind);
        }
        catch
        {
            // A malformed but schema-valid date must not abort the import (the import command's
            // catch filter does not include FormatException) — recover the conversation with a
            // sentinel timestamp instead of throwing an ugly stack trace out of ImportAsync.
            return DateTime.UnixEpoch;
        }
    }
}
