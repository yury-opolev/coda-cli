using Coda.Sdk;
using LlmClient;

namespace Engine.Tests;

public sealed class SessionForkingTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("coda_fork_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch { /* ignore */ }
    }

    private static SessionAuditTurn MakeTurn(int i) => new()
    {
        TurnIndex = i,
        TsUtc = new DateTime(2026, 7, 13, 9, 0, i, DateTimeKind.Utc),
        Provider = "p",
        Model = "m",
        InputTokens = 10 + i,
        OutputTokens = 5 + i,
        SystemPrompt = "sys-" + i,
        ToolCalls = [],
        ToolDefs = [],
    };

    [Fact]
    public async Task CopyAsync_duplicates_the_source_sidecar_to_the_target()
    {
        var store = new SessionAuditStore(this.dir);
        await store.AppendTurnAsync("aaaaaaaaaaaa", MakeTurn(0));
        await store.AppendTurnAsync("aaaaaaaaaaaa", MakeTurn(1));

        await store.CopyAsync("aaaaaaaaaaaa", "bbbbbbbbbbbb");

        var src = await store.LoadAsync("aaaaaaaaaaaa");
        var dst = await store.LoadAsync("bbbbbbbbbbbb");
        Assert.Equal(2, dst.Count);
        Assert.Equal(src.Count, dst.Count);
        Assert.Equal(src[0].SystemPrompt, dst[0].SystemPrompt);
    }

    [Fact]
    public async Task CopyAsync_is_a_noop_when_source_has_no_sidecar()
    {
        var store = new SessionAuditStore(this.dir);
        await store.CopyAsync("aaaaaaaaaaaa", "bbbbbbbbbbbb"); // no source file
        Assert.Empty(await store.LoadAsync("bbbbbbbbbbbb"));
    }

    [Fact]
    public async Task ForkAsync_creates_a_new_session_with_copied_transcript_and_audit()
    {
        await new SessionTranscriptStore(this.dir).SaveAsync("aaaaaaaaaaaa",
            [new ChatMessage(ChatRole.User, [new TextBlock("hi")])]);
        await new SessionAuditStore(this.dir).AppendTurnAsync("aaaaaaaaaaaa", MakeTurn(0));

        var newId = await SessionForking.ForkAsync(this.dir, "aaaaaaaaaaaa",
            [new ChatMessage(ChatRole.User, [new TextBlock("hi")])]);

        Assert.NotEqual("aaaaaaaaaaaa", newId);
        Assert.Matches("^[0-9a-f]{12}$", newId);
        // new transcript exists with the seeded messages
        var t = await new SessionTranscriptStore(this.dir).LoadAsync(newId);
        Assert.NotNull(t);
        Assert.Single(t!);
        // new audit carries the source's turns
        Assert.Single(await new SessionAuditStore(this.dir).LoadAsync(newId));
        // source is untouched
        Assert.Single(await new SessionAuditStore(this.dir).LoadAsync("aaaaaaaaaaaa"));
    }

    [Fact]
    public async Task ForkAsync_with_null_source_still_persists_the_transcript()
    {
        var newId = await SessionForking.ForkAsync(this.dir, null,
            [new ChatMessage(ChatRole.User, [new TextBlock("hi")])]);
        Assert.NotNull(await new SessionTranscriptStore(this.dir).LoadAsync(newId));
        Assert.Empty(await new SessionAuditStore(this.dir).LoadAsync(newId));
    }
}
