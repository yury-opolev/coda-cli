using Coda.Sdk;
using LlmClient;

namespace Engine.Tests;

public sealed class SessionAuditStoreTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_audit_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    private static SessionAuditTurn Turn(int index, string system, string[] toolNames, string stop = "end_turn") => new()
    {
        TurnIndex = index,
        TsUtc = new DateTime(2026, 7, 13, 9, 0, index, DateTimeKind.Utc),
        Provider = "github-copilot",
        Model = "claude-opus-4.8",
        InputTokens = 100 + index,
        OutputTokens = 10 + index,
        StopReason = stop,
        ToolCalls = [new ToolCallRecord("read_file", "{\"path\":\"a\"}", "ok", false)],
        SystemPrompt = system,
        ToolDefs = [.. toolNames.Select(n => new ToolDefinition(n, $"{n} desc", "{}"))],
    };

    [Fact]
    public async Task AppendThenLoad_round_trips_a_single_turn()
    {
        var store = new SessionAuditStore(this.tempDir);
        await store.AppendTurnAsync("s1", Turn(0, "SYS-A", ["read_file"]));

        var loaded = await store.LoadAsync("s1");

        var t = Assert.Single(loaded);
        Assert.Equal(0, t.TurnIndex);
        Assert.Equal("github-copilot", t.Provider);
        Assert.Equal("claude-opus-4.8", t.Model);
        Assert.Equal(100, t.InputTokens);
        Assert.Equal("end_turn", t.StopReason);
        Assert.Equal("SYS-A", t.SystemPrompt);
        Assert.Equal("read_file", Assert.Single(t.ToolDefs).Name);
        Assert.Equal("read_file", Assert.Single(t.ToolCalls).Name);
    }

    [Fact]
    public async Task SystemPrompt_and_ToolDefs_emitted_only_on_change_but_carried_forward_on_load()
    {
        var store = new SessionAuditStore(this.tempDir);
        await store.AppendTurnAsync("s1", Turn(0, "SYS-A", ["read_file"]));
        await store.AppendTurnAsync("s1", Turn(1, "SYS-A", ["read_file"]));      // unchanged
        await store.AppendTurnAsync("s1", Turn(2, "SYS-B", ["read_file", "grep"])); // changed

        var path = Path.Combine(this.tempDir, ".coda", "sessions", "s1.audit.jsonl");
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(3, lines.Length);
        Assert.Contains("SYS-A", lines[0]);
        Assert.DoesNotContain("SYS-A", lines[1]);
        Assert.DoesNotContain("SYS-B", lines[1]);
        Assert.Contains("SYS-B", lines[2]);

        var loaded = await store.LoadAsync("s1");
        Assert.Equal("SYS-A", loaded[0].SystemPrompt);
        Assert.Equal("SYS-A", loaded[1].SystemPrompt);
        Assert.Equal("SYS-B", loaded[2].SystemPrompt);
        Assert.Single(loaded[1].ToolDefs);
        Assert.Equal(2, loaded[2].ToolDefs.Count);
    }

    [Fact]
    public async Task Append_in_fresh_process_recovers_last_emitted_and_still_omits_unchanged()
    {
        var first = new SessionAuditStore(this.tempDir);
        await first.AppendTurnAsync("s1", Turn(0, "SYS-A", ["read_file"]));

        var second = new SessionAuditStore(this.tempDir);
        await second.AppendTurnAsync("s1", Turn(1, "SYS-A", ["read_file"]));

        var path = Path.Combine(this.tempDir, ".coda", "sessions", "s1.audit.jsonl");
        var lines = await File.ReadAllLinesAsync(path);
        Assert.DoesNotContain("SYS-A", lines[1]);
    }

    [Fact]
    public async Task LoadAsync_tolerates_a_torn_final_line()
    {
        var store = new SessionAuditStore(this.tempDir);
        await store.AppendTurnAsync("s1", Turn(0, "SYS-A", ["read_file"]));
        var path = Path.Combine(this.tempDir, ".coda", "sessions", "s1.audit.jsonl");
        await File.AppendAllTextAsync(path, "{ this is a torn half-written line");

        var loaded = await store.LoadAsync("s1");
        Assert.Single(loaded);
    }

    [Fact]
    public async Task LoadAsync_returns_empty_for_missing_file()
    {
        var store = new SessionAuditStore(this.tempDir);
        Assert.Empty(await store.LoadAsync("nope"));
        Assert.False(store.Exists("nope"));
    }

    [Fact]
    public async Task AppendAsync_is_noop_for_invalid_id()
    {
        var store = new SessionAuditStore(this.tempDir);
        await store.AppendTurnAsync("../escape", Turn(0, "SYS-A", ["read_file"]));
        Assert.False(Directory.Exists(Path.Combine(this.tempDir, ".coda", "sessions")));
    }
}
