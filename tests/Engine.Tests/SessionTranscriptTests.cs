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
                new ToolUseBlock("tool-id-1", "calculator", "{\"expression\":\"2+2\"}"),
            ]),
            new(ChatRole.User, [
                new ToolResultBlock("tool-id-1", "4", IsError: false),
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

        // Message 2: user ToolResultBlock
        Assert.Equal(ChatRole.User, loaded[2].Role);
        Assert.Single(loaded[2].Content);
        var trb = Assert.IsType<ToolResultBlock>(loaded[2].Content[0]);
        Assert.Equal("tool-id-1", trb.ToolUseId);
        Assert.Equal("4", trb.Content);
        Assert.False(trb.IsError);
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
}
