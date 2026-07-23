using System.Text.Json.Nodes;
using Coda.Sdk;
using Engine.Tests.TestSupport;
using LlmClient;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

public sealed class SessionTranscriptTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_transcript_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    // ── Round-trip ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trips_all_block_types()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var sessionId = "abc123";

        // Arrange: user text, assistant text+tool_use, user tool_result
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextBlock("What is 2+2?")]),
            new(ChatRole.Assistant, [
                new TextBlock("Let me calculate that."),
                new ToolUseBlock("tool-id-1", "calculator", "{\"expression\":\"2+2\"}")
                {
                    RootTurnId = "root-1",
                    ActivityId = "activity-1",
                    SourceId = "root:root-1",
                },
            ]),
            new(ChatRole.User, [
                new ToolResultBlock("tool-id-1", "4", IsError: false)
                {
                    RootTurnId = "root-1",
                    ActivityId = "activity-1",
                    SourceId = "root:root-1",
                    ToolStatus = "Succeeded",
                },
            ]),
        };

        await store.SaveAsync(sessionId, messages);
        var loaded = await store.LoadAsync(sessionId);

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.Count);

        // Message 0: user TextBlock
        Assert.Equal(ChatRole.User, loaded[0].Role);
        Assert.Single(loaded[0].Content);
        var tb0 = Assert.IsType<TextBlock>(loaded[0].Content[0]);
        Assert.Equal("What is 2+2?", tb0.Text);

        // Message 1: assistant TextBlock + ToolUseBlock
        Assert.Equal(ChatRole.Assistant, loaded[1].Role);
        Assert.Equal(2, loaded[1].Content.Count);
        var tb1 = Assert.IsType<TextBlock>(loaded[1].Content[0]);
        Assert.Equal("Let me calculate that.", tb1.Text);
        var tub = Assert.IsType<ToolUseBlock>(loaded[1].Content[1]);
        Assert.Equal("tool-id-1", tub.Id);
        Assert.Equal("calculator", tub.Name);
        Assert.Equal("{\"expression\":\"2+2\"}", tub.InputJson);
        Assert.Equal("root-1", tub.RootTurnId);
        Assert.Equal("activity-1", tub.ActivityId);
        Assert.Equal("root:root-1", tub.SourceId);

        // Message 2: user ToolResultBlock
        Assert.Equal(ChatRole.User, loaded[2].Role);
        Assert.Single(loaded[2].Content);
        var trb = Assert.IsType<ToolResultBlock>(loaded[2].Content[0]);
        Assert.Equal("tool-id-1", trb.ToolUseId);
        Assert.Equal("4", trb.Content);
        Assert.False(trb.IsError);
        Assert.Equal("root-1", trb.RootTurnId);
        Assert.Equal("activity-1", trb.ActivityId);
        Assert.Equal("root:root-1", trb.SourceId);
        Assert.Equal("Succeeded", trb.ToolStatus);
    }

    [Fact]
    public async Task SaveAsync_omits_null_correlation_metadata_and_loads_legacy_blocks()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        await store.SaveAsync(
            "legacy-correlation",
            [
                new(ChatRole.Assistant, [new ToolUseBlock("call-1", "probe", "{}")]),
                new(ChatRole.User, [new ToolResultBlock("call-1", "ok")]),
            ]);

        var file = Path.Combine(this.tempDir, ".coda", "sessions", "legacy-correlation.json");
        var document = Assert.IsType<JsonObject>(JsonNode.Parse(await File.ReadAllTextAsync(file)));
        var blocks = document["messages"]!.AsArray()[0]!["blocks"]!.AsArray();
        var toolUse = Assert.IsType<JsonObject>(blocks[0]);
        var toolResult = Assert.IsType<JsonObject>(document["messages"]!.AsArray()[1]!["blocks"]!.AsArray()[0]);
        Assert.False(toolUse.ContainsKey("rootTurnId"));
        Assert.False(toolUse.ContainsKey("activityId"));
        Assert.False(toolUse.ContainsKey("sourceId"));
        Assert.False(toolResult.ContainsKey("rootTurnId"));
        Assert.False(toolResult.ContainsKey("activityId"));
        Assert.False(toolResult.ContainsKey("sourceId"));
        Assert.False(toolResult.ContainsKey("toolStatus"));

        var loaded = await store.LoadAsync("legacy-correlation");

        var loadedUse = Assert.IsType<ToolUseBlock>(loaded![0].Content[0]);
        var loadedResult = Assert.IsType<ToolResultBlock>(loaded[1].Content[0]);
        Assert.Null(loadedUse.RootTurnId);
        Assert.Null(loadedUse.ActivityId);
        Assert.Null(loadedUse.SourceId);
        Assert.Null(loadedResult.RootTurnId);
        Assert.Null(loadedResult.ActivityId);
        Assert.Null(loadedResult.SourceId);
        Assert.Null(loadedResult.ToolStatus);
    }

    [Fact]
    public async Task LoadAsync_treats_missing_or_invalid_correlation_metadata_as_null()
    {
        var sessionsDir = Path.Combine(this.tempDir, ".coda", "sessions");
        Directory.CreateDirectory(sessionsDir);
        var document = new JsonObject
        {
            ["id"] = "invalid-correlation",
            ["createdUtc"] = DateTime.UtcNow.ToString("O"),
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["blocks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = "call-1",
                            ["name"] = "probe",
                            ["input"] = "{}",
                            ["rootTurnId"] = 42,
                            ["activityId"] = new JsonObject { ["unexpected"] = true },
                            ["sourceId"] = false,
                            ["futureCorrelationField"] = "ignored",
                        },
                    },
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["blocks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["toolUseId"] = "call-1",
                            ["content"] = "ok",
                            ["isError"] = false,
                            ["rootTurnId"] = new JsonArray("unexpected"),
                            ["activityId"] = 7,
                            ["sourceId"] = true,
                            ["toolStatus"] = new JsonObject { ["unexpected"] = true },
                            ["futureCorrelationField"] = "ignored",
                        },
                    },
                },
            },
        };
        await File.WriteAllTextAsync(
            Path.Combine(sessionsDir, "invalid-correlation.json"),
            document.ToJsonString());
        var store = new SessionTranscriptStore(this.tempDir);

        var loaded = await store.LoadAsync("invalid-correlation");

        var toolUse = Assert.IsType<ToolUseBlock>(loaded![0].Content[0]);
        var toolResult = Assert.IsType<ToolResultBlock>(loaded[1].Content[0]);
        Assert.Null(toolUse.RootTurnId);
        Assert.Null(toolUse.ActivityId);
        Assert.Null(toolUse.SourceId);
        Assert.Null(toolResult.RootTurnId);
        Assert.Null(toolResult.ActivityId);
        Assert.Null(toolResult.SourceId);
        Assert.Null(toolResult.ToolStatus);
    }

    [Fact]
    public async Task SaveAsync_then_LoadAsync_preserves_isError_true()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new ToolResultBlock("tid-2", "Error: something went wrong", IsError: true)]),
        };

        await store.SaveAsync("err-session", messages);
        var loaded = await store.LoadAsync("err-session");

        Assert.NotNull(loaded);
        var trb = Assert.IsType<ToolResultBlock>(loaded[0].Content[0]);
        Assert.True(trb.IsError);
        Assert.Equal("Error: something went wrong", trb.Content);
    }

    // ── Missing / corrupt ───────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_returns_null_for_missing_session()
    {
        var store = new SessionTranscriptStore(this.tempDir);

        var result = await store.LoadAsync("does-not-exist");

        Assert.Null(result);
    }

    [Fact]
    public async Task LoadAsync_returns_null_for_corrupt_file()
    {
        // Write a corrupt file manually.
        var sessionsDir = Path.Combine(this.tempDir, ".coda", "sessions");
        Directory.CreateDirectory(sessionsDir);
        await File.WriteAllTextAsync(Path.Combine(sessionsDir, "corrupt.json"), "{{ not valid json }}}");

        var store = new SessionTranscriptStore(this.tempDir);
        var result = await store.LoadAsync("corrupt");

        Assert.Null(result);
    }

    [Fact]
    public async Task LoadAsync_does_not_throw_on_corrupt_file()
    {
        var sessionsDir = Path.Combine(this.tempDir, ".coda", "sessions");
        Directory.CreateDirectory(sessionsDir);
        await File.WriteAllTextAsync(Path.Combine(sessionsDir, "bad.json"), "null");

        var store = new SessionTranscriptStore(this.tempDir);

        // Must not throw
        var result = await store.LoadAsync("bad");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListAsync_skips_corrupt_file_and_logs_at_debug()
    {
        // Plant one valid session and one corrupt transcript in the same dir.
        var sessionsDir = Path.Combine(this.tempDir, ".coda", "sessions");
        Directory.CreateDirectory(sessionsDir);
        await File.WriteAllTextAsync(Path.Combine(sessionsDir, "corrupt.json"), "{{ not valid json }}}");

        var logger = new CapturingLogger();
        var store = new SessionTranscriptStore(this.tempDir, logger);
        var messages = new List<ChatMessage> { new(ChatRole.User, [new TextBlock("hi")]) };
        await store.SaveAsync("good", messages);

        var summaries = await store.ListAsync();

        // Swallow semantics intact: the corrupt file is omitted, the valid one survives.
        Assert.Single(summaries);
        Assert.Equal("good", summaries[0].Id);

        // The corruption is now observable at Debug.
        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("corrupt session transcript"));
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("corrupt.json", entry.Message);
    }

    [Fact]
    public async Task ListAsync_skips_corrupt_file_without_logger_does_not_throw()
    {
        var sessionsDir = Path.Combine(this.tempDir, ".coda", "sessions");
        Directory.CreateDirectory(sessionsDir);
        await File.WriteAllTextAsync(Path.Combine(sessionsDir, "corrupt.json"), "{{ not valid json }}}");

        // Null logger (default) — the swallow must still hold with no logging.
        var store = new SessionTranscriptStore(this.tempDir);

        var summaries = await store.ListAsync();

        Assert.Empty(summaries);
    }

    // ── SaveAsync with empty messages ───────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_skips_write_when_messages_empty()
    {
        var store = new SessionTranscriptStore(this.tempDir);

        await store.SaveAsync("empty-session", []);

        var path = Path.Combine(this.tempDir, ".coda", "sessions", "empty-session.json");
        Assert.False(File.Exists(path));
    }

    // ── ListAsync ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_returns_summaries_newest_first()
    {
        var store = new SessionTranscriptStore(this.tempDir);

        // Save two sessions with different content; ordering is by createdUtc in the file.
        var older = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextBlock("First question")]),
        };
        var newer = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextBlock("Second question")]),
        };

        await store.SaveAsync("session-a", older);
        // Use 50 ms to exceed Windows DateTime.UtcNow resolution (~15 ms) reliably.
        await Task.Delay(50);
        await store.SaveAsync("session-b", newer);

        var summaries = await store.ListAsync();

        Assert.Equal(2, summaries.Count);
        // Newest first: session-b was saved after session-a
        Assert.Equal("session-b", summaries[0].Id);
        Assert.Equal("session-a", summaries[1].Id);
    }

    [Fact]
    public async Task ListAsync_returns_correct_message_count_and_preview()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextBlock("Hello, world!")]),
            new(ChatRole.Assistant, [new TextBlock("Hi there.")]),
        };

        await store.SaveAsync("preview-session", messages);
        var summaries = await store.ListAsync();

        Assert.Single(summaries);
        var summary = summaries[0];
        Assert.Equal("preview-session", summary.Id);
        Assert.Equal(2, summary.MessageCount);
        Assert.Equal("Hello, world!", summary.Preview);
    }

    [Fact]
    public async Task ListAsync_returns_empty_when_sessions_dir_missing()
    {
        var store = new SessionTranscriptStore(this.tempDir);

        var summaries = await store.ListAsync();

        Assert.Empty(summaries);
    }

    [Fact]
    public async Task ListAsync_truncates_long_preview_to_80_chars()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var longText = new string('x', 120);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextBlock(longText)]),
        };

        await store.SaveAsync("long-preview", messages);
        var summaries = await store.ListAsync();

        Assert.Single(summaries);
        Assert.Equal(80, summaries[0].Preview.Length);
    }

    [Fact]
    public async Task ListAsync_uses_first_user_text_as_preview()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        // First message is assistant — preview should come from first user message.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new TextBlock("Assistant goes first (unusual)")]),
            new(ChatRole.User, [new TextBlock("User reply")]),
        };

        await store.SaveAsync("user-preview", messages);
        var summaries = await store.ListAsync();

        Assert.Single(summaries);
        Assert.Equal("User reply", summaries[0].Preview);
    }

    // ── C1: Path traversal guard ────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_returns_null_for_dotdot_traversal_id()
    {
        var store = new SessionTranscriptStore(this.tempDir);

        // Must return null without reading anything outside the sessions dir.
        var result = await store.LoadAsync("../../foo");

        Assert.Null(result);
    }

    [Fact]
    public async Task LoadAsync_returns_null_for_slash_separated_id()
    {
        var store = new SessionTranscriptStore(this.tempDir);

        var result = await store.LoadAsync("a/b");

        Assert.Null(result);
    }

    [Fact]
    public async Task LoadAsync_returns_messages_for_normal_guid_id()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextBlock("hello")]),
        };

        await store.SaveAsync(sessionId, messages);
        var loaded = await store.LoadAsync(sessionId);

        Assert.NotNull(loaded);
        Assert.Single(loaded);
    }

    [Fact]
    public async Task LoadAsync_does_not_access_outside_sessions_dir_for_traversal_id()
    {
        // Place a sentinel file one level above the sessions dir that a traversal
        // could reach. Confirm LoadAsync("../../sentinel") returns null and the
        // sentinel file is never touched.
        var sentinel = Path.Combine(this.tempDir, "sentinel.json");
        await File.WriteAllTextAsync(sentinel, """{"id":"sentinel","createdUtc":"2024-01-01T00:00:00Z","messages":[]}""");

        var store = new SessionTranscriptStore(this.tempDir);
        var result = await store.LoadAsync("../../sentinel");

        Assert.Null(result);
        // Sentinel must still exist (was never deleted / accessed destructively).
        Assert.True(File.Exists(sentinel));
    }

    // ── I1: createdUtc preserved across saves ───────────────────────────────────

    [Fact]
    public async Task SaveAsync_preserves_createdUtc_on_subsequent_saves()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var sessionId = "persist-created-utc";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextBlock("first message")]),
        };

        await store.SaveAsync(sessionId, messages);

        // Read back the original createdUtc.
        var sessionsDir = Path.Combine(this.tempDir, ".coda", "sessions");
        var filePath = Path.Combine(sessionsDir, $"{sessionId}.json");
        var firstJson = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!;
        var firstCreated = firstJson["createdUtc"]!.GetValue<string>();

        // Wait long enough for a new DateTime.UtcNow call to produce a different value.
        await Task.Delay(50);

        // Second save with updated content.
        messages.Add(new(ChatRole.Assistant, [new TextBlock("reply")]));
        await store.SaveAsync(sessionId, messages);

        var secondJson = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!;
        var secondCreated = secondJson["createdUtc"]!.GetValue<string>();

        Assert.Equal(firstCreated, secondCreated);
    }

    // ── Optional live-session metadata ───────────────────────────────────────────

    [Fact]
    public async Task SaveSessionAsync_then_LoadSessionAsync_round_trips_exact_system_prompt_override()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var messages = new List<ChatMessage> { new(ChatRole.User, [new TextBlock("hello")]) };

        await store.SaveAsync("metadata-exact", messages, new SessionMetadata { SystemPromptOverride = "exact\n" });

        var stored = await store.LoadSessionAsync("metadata-exact");

        Assert.NotNull(stored);
        Assert.Equal("exact\n", stored.Metadata.SystemPromptOverride);
        Assert.Single(stored.Messages);
    }

    [Fact]
    public async Task SaveSessionAsync_with_empty_metadata_omits_system_prompt_override()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var messages = new List<ChatMessage> { new(ChatRole.User, [new TextBlock("hello")]) };

        await store.SaveAsync("metadata-default", messages, SessionMetadata.Empty);

        var path = Path.Combine(this.tempDir, ".coda", "sessions", "metadata-default.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Null(json["systemPromptOverride"]);
    }

    [Fact]
    public async Task SaveSessionAsync_with_empty_override_serializes_and_round_trips_it()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var messages = new List<ChatMessage> { new(ChatRole.User, [new TextBlock("hello")]) };

        await store.SaveAsync("metadata-empty", messages, new SessionMetadata { SystemPromptOverride = string.Empty });

        var path = Path.Combine(this.tempDir, ".coda", "sessions", "metadata-empty.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal(string.Empty, json["systemPromptOverride"]!.GetValue<string>());
        var stored = await store.LoadSessionAsync("metadata-empty");
        Assert.Equal(string.Empty, stored!.Metadata.SystemPromptOverride);
    }

    [Fact]
    public async Task Legacy_SaveAsync_preserves_existing_empty_system_prompt_override()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var initial = new List<ChatMessage> { new(ChatRole.User, [new TextBlock("first")]) };
        await store.SaveAsync("metadata-legacy", initial, new SessionMetadata { SystemPromptOverride = string.Empty });

        await store.SaveAsync("metadata-legacy", [new(ChatRole.User, [new TextBlock("second")])]);

        var stored = await store.LoadSessionAsync("metadata-legacy");
        Assert.Equal(string.Empty, stored!.Metadata.SystemPromptOverride);
        Assert.Equal("second", Assert.IsType<TextBlock>(stored.Messages[0].Content[0]).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadSessionAsync_accepts_legacy_or_non_string_metadata_and_unknown_fields(bool nonStringMetadata)
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var sessionId = nonStringMetadata ? "metadata-non-string" : "metadata-legacy-json";
        await store.SaveAsync(sessionId, [new(ChatRole.User, [new TextBlock("hello")])]);

        var path = Path.Combine(this.tempDir, ".coda", "sessions", $"{sessionId}.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json["unknownFutureField"] = new JsonObject { ["ignored"] = true };
        if (nonStringMetadata)
        {
            json["systemPromptOverride"] = new JsonObject { ["invalid"] = true };
        }

        await File.WriteAllTextAsync(path, json.ToJsonString());

        var stored = await store.LoadSessionAsync(sessionId);

        Assert.NotNull(stored);
        Assert.Null(stored.Metadata.SystemPromptOverride);
        Assert.Single(stored.Messages);
        Assert.Equal("hello", Assert.IsType<TextBlock>(stored.Messages[0].Content[0]).Text);
    }

    [Fact]
    public async Task Metadata_aware_and_legacy_saves_preserve_original_createdUtc()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        const string sessionId = "metadata-created-utc";
        var first = new List<ChatMessage> { new(ChatRole.User, [new TextBlock("first")]) };
        await store.SaveAsync(sessionId, first, new SessionMetadata { SystemPromptOverride = "override" });

        var path = Path.Combine(this.tempDir, ".coda", "sessions", $"{sessionId}.json");
        var firstCreated = JsonNode.Parse(await File.ReadAllTextAsync(path))!["createdUtc"]!.GetValue<string>();
        await Task.Delay(50);

        await store.SaveAsync(sessionId, [new(ChatRole.User, [new TextBlock("second")])]);

        var secondCreated = JsonNode.Parse(await File.ReadAllTextAsync(path))!["createdUtc"]!.GetValue<string>();
        Assert.Equal(firstCreated, secondCreated);
    }

    // ── Freeze invariant: minting/adopting a fresh id never touches the original transcript ─────

    [Fact]
    public async Task SaveAsync_under_a_new_id_never_overwrites_a_prior_sessions_transcript()
    {
        var store = new SessionTranscriptStore(this.tempDir);
        var originalId = "aaaaaaaaaaaa";
        var forkedId = "bbbbbbbbbbbb";

        var originalMessages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextBlock("original question")]),
            new(ChatRole.Assistant, [new TextBlock("original answer")]),
        };
        await store.SaveAsync(originalId, originalMessages);

        var sessionsDir = Path.Combine(this.tempDir, ".coda", "sessions");
        var originalPath = Path.Combine(sessionsDir, $"{originalId}.json");
        var originalBytesBefore = await File.ReadAllBytesAsync(originalPath);
        var originalWriteTimeBefore = File.GetLastWriteTimeUtc(originalPath);

        // Simulate /clear or /fork: a brand-new id is minted/adopted and a different
        // conversation is saved under it. This must never touch the original file.
        await Task.Delay(50);
        var forkedMessages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextBlock("forked question")]),
        };
        await store.SaveAsync(forkedId, forkedMessages);

        // The original transcript is frozen: byte-identical content and mtime.
        var originalBytesAfter = await File.ReadAllBytesAsync(originalPath);
        Assert.Equal(originalBytesBefore, originalBytesAfter);
        Assert.Equal(originalWriteTimeBefore, File.GetLastWriteTimeUtc(originalPath));

        // The new id's transcript exists with its own messages.
        var forkedPath = Path.Combine(sessionsDir, $"{forkedId}.json");
        Assert.True(File.Exists(forkedPath));
        var forkedLoaded = await store.LoadAsync(forkedId);
        Assert.NotNull(forkedLoaded);
        Assert.Single(forkedLoaded);
        var forkedText = Assert.IsType<TextBlock>(forkedLoaded[0].Content[0]);
        Assert.Equal("forked question", forkedText.Text);
    }
}
