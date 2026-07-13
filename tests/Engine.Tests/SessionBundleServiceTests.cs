using Coda.Sdk;
using LlmClient;

namespace Engine.Tests;

public sealed class SessionBundleServiceTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_bundle_").FullName;
    private static readonly DateTime FixedExport = new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    private async Task SeedSessionAsync(string id)
    {
        var transcript = new SessionTranscriptStore(this.tempDir);
        await transcript.SaveAsync(id,
        [
            new(ChatRole.User, [new TextBlock("hello")]),
            new(ChatRole.Assistant, [new TextBlock("hi there")]),
        ]);
        var audit = new SessionAuditStore(this.tempDir);
        await audit.AppendTurnAsync(id, new SessionAuditTurn
        {
            TurnIndex = 0,
            TsUtc = new DateTime(2026, 7, 13, 9, 0, 0, DateTimeKind.Utc),
            Provider = "github-copilot",
            Model = "claude-opus-4.8",
            InputTokens = 200,
            OutputTokens = 20,
            StopReason = "end_turn",
            SystemPrompt = "SYSTEM-PROMPT-TEXT",
            ToolDefs = [new ToolDefinition("read_file", "reads", "{}")],
        });
    }

    [Fact]
    public async Task ExportAsync_includes_system_prompt_usage_and_turns()
    {
        await this.SeedSessionAsync("s1");
        var svc = new SessionBundleService(this.tempDir, "0.1.63");

        var bundle = await svc.ExportAsync("s1", FixedExport);

        Assert.NotNull(bundle);
        Assert.Equal("coda.session/1", bundle.Schema);
        Assert.True(bundle.AuditAvailable);
        Assert.Equal("SYSTEM-PROMPT-TEXT", bundle.SystemPrompt);
        Assert.Equal("github-copilot", bundle.Provider);
        Assert.Equal(2, bundle.Turns.Count);
        var assistant = bundle.Turns[1];
        Assert.Equal("assistant", assistant.Role);
        Assert.Equal("hi there", Assert.IsType<TextBlock>(Assert.Single(assistant.Blocks)).Text);
        var auditTurn = Assert.Single(bundle.AuditTurns);
        Assert.Equal(200, auditTurn.InputTokens);
        Assert.Equal("end_turn", auditTurn.StopReason);
    }

    [Fact]
    public async Task ExportAsync_returns_null_for_missing_session()
    {
        var svc = new SessionBundleService(this.tempDir, "0.1.63");
        Assert.Null(await svc.ExportAsync("nope", FixedExport));
    }

    [Fact]
    public async Task ExportAsync_without_sidecar_sets_auditAvailable_false()
    {
        var transcript = new SessionTranscriptStore(this.tempDir);
        await transcript.SaveAsync("s2", [new(ChatRole.User, [new TextBlock("q")])]);
        var svc = new SessionBundleService(this.tempDir, "0.1.63");

        var bundle = await svc.ExportAsync("s2", FixedExport);

        Assert.NotNull(bundle);
        Assert.False(bundle.AuditAvailable);
        Assert.Null(bundle.SystemPrompt);
    }

    [Fact]
    public async Task Export_Write_Import_round_trips_and_preserves_history()
    {
        await this.SeedSessionAsync("s1");
        var svc = new SessionBundleService(this.tempDir, "0.1.63");
        var bundle = await svc.ExportAsync("s1", FixedExport);
        var outPath = Path.Combine(this.tempDir, "s1.coda-session.json");
        await svc.WriteAsync(bundle!, outPath, pretty: false);

        var otherDir = Directory.CreateTempSubdirectory("coda_bundle_dst_").FullName;
        try
        {
            var svc2 = new SessionBundleService(otherDir, "0.1.63");
            var importedId = await svc2.ImportAsync(outPath);

            Assert.Equal("s1", importedId);
            var transcript = new SessionTranscriptStore(otherDir);
            var loaded = await transcript.LoadAsync("s1");
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.Count);
            var audit = await new SessionAuditStore(otherDir).LoadAsync("s1");
            Assert.Equal("SYSTEM-PROMPT-TEXT", Assert.Single(audit).SystemPrompt);
        }
        finally
        {
            try { Directory.Delete(otherDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task ImportAsync_mints_new_id_on_collision()
    {
        await this.SeedSessionAsync("s1");
        var svc = new SessionBundleService(this.tempDir, "0.1.63");
        var bundle = await svc.ExportAsync("s1", FixedExport);
        var outPath = Path.Combine(this.tempDir, "s1.coda-session.json");
        await svc.WriteAsync(bundle!, outPath, pretty: false);

        var importedId = await svc.ImportAsync(outPath);

        Assert.NotEqual("s1", importedId);
        Assert.NotNull(await new SessionTranscriptStore(this.tempDir).LoadAsync(importedId));
    }

    [Fact]
    public async Task ImportAsync_rejects_unknown_schema_major()
    {
        var bad = Path.Combine(this.tempDir, "bad.json");
        await File.WriteAllTextAsync(bad, """{"schema":"coda.session/9","id":"x","turns":[]}""");
        var svc = new SessionBundleService(this.tempDir, "0.1.63");

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ImportAsync(bad));
    }

    [Fact]
    public async Task ImportAsync_carries_tool_defs_and_system_prompt_forward_to_later_turns()
    {
        var transcript = new SessionTranscriptStore(this.tempDir);
        await transcript.SaveAsync("m1",
        [
            new(ChatRole.User, [new TextBlock("q1")]),
            new(ChatRole.Assistant, [new TextBlock("a1")]),
            new(ChatRole.User, [new TextBlock("q2")]),
            new(ChatRole.Assistant, [new TextBlock("a2")]),
        ]);
        var audit = new SessionAuditStore(this.tempDir);
        for (var i = 0; i < 2; i++)
        {
            await audit.AppendTurnAsync("m1", new SessionAuditTurn
            {
                TurnIndex = i,
                TsUtc = new DateTime(2026, 7, 13, 9, 0, i, DateTimeKind.Utc),
                Provider = "github-copilot",
                Model = "claude-opus-4.8",
                InputTokens = 100 + i,
                OutputTokens = 10 + i,
                StopReason = "end_turn",
                SystemPrompt = "SYSTEM-PROMPT-TEXT",
                ToolDefs = [new ToolDefinition("read_file", "reads", "{}")],
            });
        }

        var svc = new SessionBundleService(this.tempDir, "0.1.63");
        var bundle = await svc.ExportAsync("m1", FixedExport);
        var outPath = Path.Combine(this.tempDir, "m1.coda-session.json");
        await svc.WriteAsync(bundle!, outPath, pretty: false);

        var otherDir = Directory.CreateTempSubdirectory("coda_bundle_dst_").FullName;
        try
        {
            var svc2 = new SessionBundleService(otherDir, "0.1.63");
            var importedId = await svc2.ImportAsync(outPath);

            var turns = await new SessionAuditStore(otherDir).LoadAsync(importedId);
            Assert.Equal(2, turns.Count);
            foreach (var turn in turns)
            {
                Assert.Equal("SYSTEM-PROMPT-TEXT", turn.SystemPrompt);
                Assert.Equal("read_file", Assert.Single(turn.ToolDefs).Name);
            }
        }
        finally
        {
            try { Directory.Delete(otherDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Export_import_of_a_tool_using_turn_keeps_one_audit_turn()
    {
        // One user turn that used a tool: the agent loop appends TWO assistant messages
        // (tool_use, then the final text) but the sidecar records exactly ONE audit turn.
        var transcript = new SessionTranscriptStore(this.tempDir);
        await transcript.SaveAsync("tu1",
        [
            new(ChatRole.User, [new TextBlock("go")]),
            new(ChatRole.Assistant, [new ToolUseBlock("call-1", "read_file", "{}")]),
            new(ChatRole.User, [new ToolResultBlock("call-1", "file contents", false)]),
            new(ChatRole.Assistant, [new TextBlock("done")]),
        ]);
        var audit = new SessionAuditStore(this.tempDir);
        await audit.AppendTurnAsync("tu1", new SessionAuditTurn
        {
            TurnIndex = 0,
            TsUtc = new DateTime(2026, 7, 13, 9, 0, 0, DateTimeKind.Utc),
            Provider = "github-copilot",
            Model = "claude-opus-4.8",
            InputTokens = 200,
            OutputTokens = 20,
            StopReason = "end_turn",
            ToolCalls = [new ToolCallRecord("read_file", "{}", "file contents", false)],
            SystemPrompt = "SYSTEM-PROMPT-TEXT",
            ToolDefs = [new ToolDefinition("read_file", "reads", "{}")],
        });

        var svc = new SessionBundleService(this.tempDir, "0.1.63");
        var bundle = await svc.ExportAsync("tu1", FixedExport);
        var outPath = Path.Combine(this.tempDir, "tu1.coda-session.json");
        await svc.WriteAsync(bundle!, outPath, pretty: false);

        var otherDir = Directory.CreateTempSubdirectory("coda_bundle_dst_").FullName;
        try
        {
            var svc2 = new SessionBundleService(otherDir, "0.1.63");
            var importedId = await svc2.ImportAsync(outPath);

            var auditTurns = await new SessionAuditStore(otherDir).LoadAsync(importedId);
            Assert.Single(auditTurns);
            var loaded = await new SessionTranscriptStore(otherDir).LoadAsync(importedId);
            Assert.NotNull(loaded);
            Assert.Equal(4, loaded.Count);
        }
        finally
        {
            try { Directory.Delete(otherDir, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task ImportAsync_tolerates_a_malformed_date()
    {
        var bundlePath = Path.Combine(this.tempDir, "malformed-date.json");
        await File.WriteAllTextAsync(bundlePath,
            """{"schema":"coda.session/1","id":"md1","exportedUtc":"not-a-date","turns":[{"role":"user","blocks":[{"type":"text","text":"hi"}]}]}""");
        var svc = new SessionBundleService(this.tempDir, "0.1.63");

        var importedId = await svc.ImportAsync(bundlePath);

        var loaded = await new SessionTranscriptStore(this.tempDir).LoadAsync(importedId);
        Assert.NotNull(loaded);
        Assert.Single(loaded);
    }
}
