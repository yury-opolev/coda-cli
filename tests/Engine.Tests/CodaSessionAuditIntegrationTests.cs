using Coda.Sdk;
using Engine.Tests.TestSupport;
using LlmClient;

namespace Engine.Tests;

public sealed class CodaSessionAuditIntegrationTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_audit_int_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task RunAsync_writes_transcript_and_audit_sidecar_with_turn_fields()
    {
        using var session = FakeSession.New(this.tempDir);
        await session.RunAsync("hello");

        Assert.True(File.Exists(Path.Combine(this.tempDir, ".coda", "sessions", session.SessionId + ".json")));

        var audit = await new SessionAuditStore(this.tempDir).LoadAsync(session.SessionId);
        var t = Assert.Single(audit);
        Assert.Equal(session.SessionId, session.SessionId);
        Assert.Equal("claude-sonnet-4-6", t.Model);
        Assert.False(string.IsNullOrEmpty(t.SystemPrompt));
        Assert.NotEmpty(t.ToolDefs);
        // ConfigurableLoop reports TokenUsage(7, 11) + stopReason "end_turn".
        Assert.Equal(7, t.InputTokens);
        Assert.Equal(11, t.OutputTokens);
        Assert.Equal("end_turn", t.StopReason);
        Assert.Equal(0, t.TurnIndex);
    }

    [Fact]
    public async Task Two_turns_append_two_audit_lines_with_incrementing_index()
    {
        using var session = FakeSession.New(this.tempDir);
        await session.RunAsync("one");
        await session.RunAsync("two");

        var audit = await new SessionAuditStore(this.tempDir).LoadAsync(session.SessionId);
        Assert.Equal(2, audit.Count);
        Assert.Equal(0, audit[0].TurnIndex);
        Assert.Equal(1, audit[1].TurnIndex);
    }

    [Fact]
    public async Task Resumed_session_continues_the_turn_index_from_the_existing_sidecar()
    {
        // Pre-seed a sidecar for id "resumed" with two turns.
        var store = new SessionAuditStore(this.tempDir);
        for (var i = 0; i < 2; i++)
        {
            await store.AppendTurnAsync("resumed", new SessionAuditTurn
            {
                TurnIndex = i,
                TsUtc = new DateTime(2026, 7, 13, 9, 0, i, DateTimeKind.Utc),
                Provider = "github-copilot",
                Model = "claude-opus-4.8",
                InputTokens = 1,
                OutputTokens = 1,
                SystemPrompt = "SEED",
                ToolDefs = [new ToolDefinition("read_file", "reads", "{}")],
            });
        }

        // A session that adopts id "resumed" must continue numbering at 2.
        using var session = FakeSession.New(this.tempDir, sessionId: "resumed");
        await session.RunAsync("next");

        var audit = await new SessionAuditStore(this.tempDir).LoadAsync("resumed");
        Assert.Equal(3, audit.Count);
        Assert.Equal(2, audit[^1].TurnIndex);
    }
}
