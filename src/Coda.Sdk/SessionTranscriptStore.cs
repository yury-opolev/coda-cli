using System.Text.Json;
using System.Text.Json.Nodes;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Coda.Sdk;

/// <summary>
/// Persists and loads conversation transcripts under
/// <c>&lt;workingDirectory&gt;/.coda/sessions/&lt;id&gt;.json</c>.
/// </summary>
public sealed partial class SessionTranscriptStore(string workingDirectory, ILogger? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    [LoggerMessage(Level = LogLevel.Debug, Message = "skipping corrupt session transcript (best-effort); it is omitted from the session list: file={file}")]
    private static partial void LogCorruptTranscriptSkipped(ILogger logger, string file, Exception ex);

    private string SessionsDir => Path.Combine(workingDirectory, ".coda", "sessions");

    private string FilePath(string sessionId) => Path.Combine(this.SessionsDir, $"{sessionId}.json");

    /// <summary>
    /// Returns <c>true</c> when <paramref name="sessionId"/> is safe to use as a
    /// file name: non-empty, contains no invalid file-name characters, and contains
    /// no path separator components (guards against traversal like "../../secret").
    /// </summary>
    private static bool IsValidId(string sessionId)
        => !string.IsNullOrEmpty(sessionId)
           && sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && Path.GetFileName(sessionId) == sessionId;

    /// <summary>
    /// Persists <paramref name="messages"/> to disk. Creates the sessions directory
    /// if it does not exist. If <paramref name="messages"/> is empty, or
    /// <paramref name="sessionId"/> is invalid, skips writing.
    /// </summary>
    public async Task SaveAsync(
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default)
    {
        if (messages.Count == 0 || !IsValidId(sessionId))
        {
            return;
        }

        Directory.CreateDirectory(this.SessionsDir);

        // Preserve the original createdUtc if the file already exists; only
        // use DateTime.UtcNow when creating the session for the first time.
        var filePath = this.FilePath(sessionId);
        var createdUtc = DateTime.UtcNow;
        if (File.Exists(filePath))
        {
            try
            {
                var existing = JsonNode.Parse(await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false));
                var raw = existing?["createdUtc"]?.GetValue<string>();
                if (raw is not null)
                {
                    createdUtc = DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind);
                }
            }
            catch
            {
                // Corrupt existing file — fall back to DateTime.UtcNow already set above.
            }
        }

        var root = new JsonObject
        {
            ["id"] = sessionId,
            ["createdUtc"] = createdUtc.ToString("O"),
            ["messages"] = SerializeMessages(messages),
        };

        await File.WriteAllTextAsync(filePath, root.ToJsonString(JsonOptions), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Loads a persisted session. Returns <c>null</c> if <paramref name="sessionId"/>
    /// is invalid, the file does not exist, or the file is corrupt (never throws).
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>?> LoadAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        if (!IsValidId(sessionId))
        {
            return null;
        }

        var path = this.FilePath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var root = JsonNode.Parse(json);
            if (root is null)
            {
                return null;
            }

            var messagesArray = root["messages"]?.AsArray();
            if (messagesArray is null)
            {
                return null;
            }

            return DeserializeMessages(messagesArray);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns summaries of all persisted sessions, ordered newest first.
    /// </summary>
    public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(this.SessionsDir))
        {
            return Task.FromResult<IReadOnlyList<SessionSummary>>([]);
        }

        var summaries = new List<SessionSummary>();
        foreach (var file in Directory.EnumerateFiles(this.SessionsDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var root = JsonNode.Parse(json);
                if (root is null)
                {
                    continue;
                }

                var id = root["id"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file);
                var createdUtcRaw = root["createdUtc"]?.GetValue<string>();
                var createdUtc = createdUtcRaw is not null
                    ? DateTime.Parse(createdUtcRaw, null, System.Globalization.DateTimeStyles.RoundtripKind)
                    : File.GetLastWriteTimeUtc(file);

                var messagesArray = root["messages"]?.AsArray();
                var messageCount = messagesArray?.Count ?? 0;

                var preview = ExtractPreview(messagesArray);
                summaries.Add(new SessionSummary(id, createdUtc, messageCount, preview));
            }
            catch (Exception ex)
            {
                // Skip corrupt files.
                if (logger is not null)
                {
                    LogCorruptTranscriptSkipped(logger, file, ex);
                }
            }
        }

        summaries.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return Task.FromResult<IReadOnlyList<SessionSummary>>(summaries);
    }

    // ── Serialization ──────────────────────────────────────────────────────────

    private static JsonArray SerializeMessages(IReadOnlyList<ChatMessage> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            var msgObj = new JsonObject
            {
                ["role"] = message.Role == ChatRole.User ? "user" : "assistant",
                ["blocks"] = SerializeBlocks(message.Content),
            };
            array.Add(msgObj);
        }

        return array;
    }

    private static JsonArray SerializeBlocks(IReadOnlyList<ContentBlock> blocks)
    {
        var array = new JsonArray();
        foreach (var block in blocks)
        {
            JsonObject obj = block switch
            {
                TextBlock tb => new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = tb.Text,
                },
                ToolUseBlock tub => new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = tub.Id,
                    ["name"] = tub.Name,
                    ["input"] = tub.InputJson,
                },
                ToolResultBlock trb => new JsonObject
                {
                    ["type"] = "tool_result",
                    ["toolUseId"] = trb.ToolUseId,
                    ["content"] = trb.Content,
                    ["isError"] = trb.IsError,
                },
                _ => new JsonObject { ["type"] = "unknown" },
            };
            array.Add(obj);
        }

        return array;
    }

    private static IReadOnlyList<ChatMessage> DeserializeMessages(JsonArray array)
    {
        var messages = new List<ChatMessage>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject msgObj)
            {
                continue;
            }

            var roleStr = msgObj["role"]?.GetValue<string>();
            var role = string.Equals(roleStr, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;

            var blocksArray = msgObj["blocks"]?.AsArray();
            var blocks = blocksArray is not null ? DeserializeBlocks(blocksArray) : (IReadOnlyList<ContentBlock>)[];
            messages.Add(new ChatMessage(role, blocks));
        }

        return messages;
    }

    private static IReadOnlyList<ContentBlock> DeserializeBlocks(JsonArray array)
    {
        var blocks = new List<ContentBlock>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var type = obj["type"]?.GetValue<string>();
            ContentBlock? block = type switch
            {
                "text" => new TextBlock(obj["text"]?.GetValue<string>() ?? string.Empty),
                "tool_use" => new ToolUseBlock(
                    obj["id"]?.GetValue<string>() ?? string.Empty,
                    obj["name"]?.GetValue<string>() ?? string.Empty,
                    obj["input"]?.GetValue<string>() ?? string.Empty),
                "tool_result" => new ToolResultBlock(
                    obj["toolUseId"]?.GetValue<string>() ?? string.Empty,
                    obj["content"]?.GetValue<string>() ?? string.Empty,
                    obj["isError"]?.GetValue<bool>() ?? false),
                _ => null,
            };

            if (block is not null)
            {
                blocks.Add(block);
            }
        }

        return blocks;
    }

    private static string ExtractPreview(JsonArray? messagesArray)
    {
        if (messagesArray is null)
        {
            return string.Empty;
        }

        foreach (var item in messagesArray)
        {
            if (item is not JsonObject msgObj)
            {
                continue;
            }

            var roleStr = msgObj["role"]?.GetValue<string>();
            if (!string.Equals(roleStr, "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var blocksArray = msgObj["blocks"]?.AsArray();
            if (blocksArray is null)
            {
                continue;
            }

            foreach (var blockItem in blocksArray)
            {
                if (blockItem is not JsonObject blockObj)
                {
                    continue;
                }

                if (string.Equals(blockObj["type"]?.GetValue<string>(), "text", StringComparison.OrdinalIgnoreCase))
                {
                    var text = blockObj["text"]?.GetValue<string>() ?? string.Empty;
                    return text.Length <= 80 ? text : text[..80];
                }
            }
        }

        return string.Empty;
    }
}
