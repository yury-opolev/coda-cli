using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmClient;

namespace Coda.Sdk;

/// <summary>
/// Persists an append-only, per-turn audit trail under
/// <c>&lt;workingDirectory&gt;/.coda/sessions/&lt;id&gt;.audit.jsonl</c> — one JSON object per line.
/// </summary>
/// <remarks>
/// This store is only ever invoked from a swallowed seam: it never throws. A bad line on read is
/// skipped, not thrown; a failure while appending is swallowed so the caller's turn is not lost
/// over an audit-trail write error.
/// </remarks>
public sealed class SessionAuditStore(string workingDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    // Tracks, per session id, the most recently emitted SystemPrompt/ToolDefs so repeated turns
    // with unchanged values omit them from the line (change-only emission). Recovered from disk
    // on the first append in a fresh process (the resume case) — see RecoverState.
    private readonly ConcurrentDictionary<string, EmittedState> emittedStateBySession = new(StringComparer.Ordinal);

    private string SessionsDir => Path.Combine(workingDirectory, ".coda", "sessions");

    private string FilePath(string sessionId) => Path.Combine(this.SessionsDir, $"{sessionId}.audit.jsonl");

    /// <summary>Returns <c>true</c> when an audit sidecar file exists for <paramref name="sessionId"/>.</summary>
    public bool Exists(string sessionId) => SessionIds.IsValid(sessionId) && File.Exists(this.FilePath(sessionId));

    /// <summary>
    /// Appends one turn to the audit sidecar. <see cref="SessionAuditTurn.SystemPrompt"/> and
    /// <see cref="SessionAuditTurn.ToolDefs"/> are written only when they differ from the last
    /// emitted values for this session (the first append always emits both). No-op for an invalid
    /// <paramref name="sessionId"/>; swallows any I/O failure (never throws).
    /// </summary>
    public async Task AppendTurnAsync(string sessionId, SessionAuditTurn turn, CancellationToken ct = default)
    {
        if (!SessionIds.IsValid(sessionId))
        {
            return;
        }

        try
        {
            var filePath = this.FilePath(sessionId);

            if (!this.emittedStateBySession.TryGetValue(sessionId, out var state))
            {
                state = this.emittedStateBySession.GetOrAdd(sessionId, RecoverState(filePath));
            }

            var toolDefsArray = AuditJson.SerializeToolDefs(turn.ToolDefs);
            var toolDefsJson = toolDefsArray.ToJsonString(JsonOptions);

            var emitSystemPrompt = !state.HasSystemPrompt
                || !string.Equals(state.SystemPrompt, turn.SystemPrompt, StringComparison.Ordinal);
            var emitToolDefs = !state.HasToolDefs
                || !string.Equals(state.ToolDefsJson, toolDefsJson, StringComparison.Ordinal);

            var lineObj = new JsonObject
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
            };

            if (emitSystemPrompt)
            {
                lineObj["systemPrompt"] = turn.SystemPrompt;
                state.SystemPrompt = turn.SystemPrompt;
                state.HasSystemPrompt = true;
            }

            if (emitToolDefs)
            {
                lineObj["toolDefs"] = toolDefsArray;
                state.ToolDefsJson = toolDefsJson;
                state.HasToolDefs = true;
            }

            Directory.CreateDirectory(this.SessionsDir);
            await File.AppendAllTextAsync(filePath, lineObj.ToJsonString(JsonOptions) + Environment.NewLine, ct).ConfigureAwait(false);
        }
        catch
        {
            // Never throw out of persistence — this is called from a swallowed seam.
        }
    }

    /// <summary>
    /// Copies the audit sidecar from <paramref name="sourceId"/> to <paramref name="targetId"/>,
    /// so a forked session carries the source's full turn-by-turn audit trail. No-op when either
    /// id is invalid or the source has no sidecar file; swallows any I/O failure (never throws) —
    /// this is called from a swallowed seam. Does not touch the change-only emission cache.
    /// </summary>
    public Task CopyAsync(string sourceId, string targetId, CancellationToken ct = default)
    {
        if (!SessionIds.IsValid(sourceId) || !SessionIds.IsValid(targetId))
        {
            return Task.CompletedTask;
        }

        try
        {
            var sourcePath = this.FilePath(sourceId);
            if (!File.Exists(sourcePath))
            {
                return Task.CompletedTask;
            }

            var targetPath = this.FilePath(targetId);
            Directory.CreateDirectory(this.SessionsDir);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }
        catch
        {
            // Never throw out of persistence — this is called from a swallowed seam.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Loads all turns for <paramref name="sessionId"/>, reconstructing the effective
    /// <see cref="SessionAuditTurn.SystemPrompt"/>/<see cref="SessionAuditTurn.ToolDefs"/> for each
    /// turn by carrying forward the most recent emitted value. Tolerates a torn/corrupt line
    /// (skipped, not thrown) and a missing or fully corrupt file (returns an empty list).
    /// </summary>
    public async Task<IReadOnlyList<SessionAuditTurn>> LoadAsync(string sessionId, CancellationToken ct = default)
    {
        if (!SessionIds.IsValid(sessionId))
        {
            return [];
        }

        var filePath = this.FilePath(sessionId);
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(filePath, ct).ConfigureAwait(false);
            var turns = new List<SessionAuditTurn>(lines.Length);
            string? currentSystemPrompt = null;
            IReadOnlyList<ToolDefinition> currentToolDefs = [];

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    turns.Add(ParseLine(line, ref currentSystemPrompt, ref currentToolDefs));
                }
                catch
                {
                    // Torn or corrupt line (e.g. an interrupted write) — skip, do not throw.
                }
            }

            return turns;
        }
        catch
        {
            return [];
        }
    }

    // ── Serialization ──────────────────────────────────────────────────────────
    // Tool-call / tool-def JSON shape is shared with SessionBundleService via AuditJson so the
    // bundle round-trips the sidecar exactly.

    private static SessionAuditTurn ParseLine(string line, ref string? currentSystemPrompt, ref IReadOnlyList<ToolDefinition> currentToolDefs)
    {
        var obj = (JsonObject)(JsonNode.Parse(line) ?? throw new JsonException("empty line"));

        var turnIndex = obj["turnIndex"]!.GetValue<int>();
        var tsUtc = DateTime.Parse(obj["tsUtc"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind);
        var provider = obj["provider"]!.GetValue<string>();
        var model = obj["model"]!.GetValue<string>();
        var usage = obj["usage"]!.AsObject();
        var inputTokens = usage["in"]!.GetValue<int>();
        var outputTokens = usage["out"]!.GetValue<int>();
        var stopReason = obj["stopReason"]?.GetValue<string>();

        var toolCallsArray = obj["toolCalls"]?.AsArray();
        var toolCalls = toolCallsArray is not null ? AuditJson.DeserializeToolCalls(toolCallsArray) : (IReadOnlyList<ToolCallRecord>)[];

        if (obj["systemPrompt"] is JsonValue systemPromptValue)
        {
            currentSystemPrompt = systemPromptValue.GetValue<string>();
        }

        if (obj["toolDefs"] is JsonArray toolDefsArray)
        {
            currentToolDefs = AuditJson.DeserializeToolDefs(toolDefsArray);
        }

        return new SessionAuditTurn
        {
            TurnIndex = turnIndex,
            TsUtc = tsUtc,
            Provider = provider,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            StopReason = stopReason,
            ToolCalls = toolCalls,
            SystemPrompt = currentSystemPrompt,
            ToolDefs = currentToolDefs,
        };
    }

    /// <summary>
    /// Recovers the most recently emitted SystemPrompt/ToolDefs by scanning an existing audit
    /// file (the resume case: a fresh process appending to a session that already has a sidecar).
    /// Tolerant of corrupt/torn lines; returns an empty state for a missing or unreadable file.
    /// </summary>
    private static EmittedState RecoverState(string filePath)
    {
        var state = new EmittedState();
        if (!File.Exists(filePath))
        {
            return state;
        }

        try
        {
            foreach (var line in File.ReadAllLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonNode.Parse(line) is not JsonObject obj)
                    {
                        continue;
                    }

                    if (obj["systemPrompt"] is JsonValue systemPromptValue)
                    {
                        state.SystemPrompt = systemPromptValue.GetValue<string>();
                        state.HasSystemPrompt = true;
                    }

                    if (obj["toolDefs"] is JsonArray toolDefsArray)
                    {
                        state.ToolDefsJson = toolDefsArray.ToJsonString(JsonOptions);
                        state.HasToolDefs = true;
                    }
                }
                catch
                {
                    // Corrupt/torn line — skip, keep scanning for the most recent good value.
                }
            }
        }
        catch
        {
            // Unreadable file — treat as no prior state.
        }

        return state;
    }

    private sealed class EmittedState
    {
        public bool HasSystemPrompt { get; set; }

        public string? SystemPrompt { get; set; }

        public bool HasToolDefs { get; set; }

        public string? ToolDefsJson { get; set; }
    }
}
