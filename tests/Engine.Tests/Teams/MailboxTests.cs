using System.Text;
using Coda.Agent.Teams;

namespace Engine.Tests.Teams;

public sealed class MailboxTests : IDisposable
{
    private readonly string teamsBaseDir;
    private readonly Mailbox mailbox;

    public MailboxTests()
    {
        this.teamsBaseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        this.mailbox = new Mailbox(this.teamsBaseDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.teamsBaseDir))
        {
            Directory.Delete(this.teamsBaseDir, recursive: true);
        }
    }

    private static TeammateMessage MakeMessage(string from = "alice", string text = "hello") =>
        new TeammateMessage(
            From: from,
            Text: text,
            Timestamp: "2026-06-01T00:00:00Z",
            Read: false,
            Color: "blue",
            Summary: "A greeting");

    // ─── Write + Read ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_then_Read_returns_message_with_correct_fields_and_read_false()
    {
        var msg = MakeMessage("alice", "hello");

        await this.mailbox.WriteAsync("bob", "my-team", msg).WaitAsync(TimeSpan.FromSeconds(5));

        var messages = await this.mailbox.ReadAsync("bob", "my-team").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(messages);
        Assert.Equal("alice", messages[0].From);
        Assert.Equal("hello", messages[0].Text);
        Assert.False(messages[0].Read);
    }

    [Fact]
    public async Task Write_forces_Read_false_even_when_message_had_Read_true()
    {
        var msg = MakeMessage() with { Read = true };

        await this.mailbox.WriteAsync("bob", "my-team", msg).WaitAsync(TimeSpan.FromSeconds(5));

        var messages = await this.mailbox.ReadAsync("bob", "my-team").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(messages);
        Assert.False(messages[0].Read);
    }

    // ─── ReadUnread ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadUnread_filters_read_messages()
    {
        await this.mailbox.WriteAsync("bob", "my-team", MakeMessage("alice", "msg0")).WaitAsync(TimeSpan.FromSeconds(5));
        await this.mailbox.WriteAsync("bob", "my-team", MakeMessage("carol", "msg1")).WaitAsync(TimeSpan.FromSeconds(5));

        await this.mailbox.MarkReadByIndexAsync("bob", "my-team", 0).WaitAsync(TimeSpan.FromSeconds(5));

        var unread = await this.mailbox.ReadUnreadAsync("bob", "my-team").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(unread);
        Assert.Equal("msg1", unread[0].Text);
    }

    // ─── MarkReadByIndex ──────────────────────────────────────────────────────

    [Fact]
    public async Task MarkReadByIndex_sets_read_flag_on_correct_message()
    {
        await this.mailbox.WriteAsync("bob", "my-team", MakeMessage("alice", "msg0")).WaitAsync(TimeSpan.FromSeconds(5));
        await this.mailbox.WriteAsync("bob", "my-team", MakeMessage("carol", "msg1")).WaitAsync(TimeSpan.FromSeconds(5));

        await this.mailbox.MarkReadByIndexAsync("bob", "my-team", 0).WaitAsync(TimeSpan.FromSeconds(5));

        var messages = await this.mailbox.ReadAsync("bob", "my-team").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(messages[0].Read);
        Assert.False(messages[1].Read);
    }

    [Fact]
    public async Task MarkReadByIndex_out_of_range_does_not_throw_or_change_messages()
    {
        await this.mailbox.WriteAsync("bob", "my-team", MakeMessage()).WaitAsync(TimeSpan.FromSeconds(5));

        await this.mailbox.MarkReadByIndexAsync("bob", "my-team", 99).WaitAsync(TimeSpan.FromSeconds(5));
        await this.mailbox.MarkReadByIndexAsync("bob", "my-team", -1).WaitAsync(TimeSpan.FromSeconds(5));

        var messages = await this.mailbox.ReadAsync("bob", "my-team").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(messages);
        Assert.False(messages[0].Read);
    }

    // ─── MarkAllRead ──────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkAllRead_marks_all_messages_as_read()
    {
        await this.mailbox.WriteAsync("bob", "my-team", MakeMessage("alice", "msg0")).WaitAsync(TimeSpan.FromSeconds(5));
        await this.mailbox.WriteAsync("bob", "my-team", MakeMessage("carol", "msg1")).WaitAsync(TimeSpan.FromSeconds(5));

        await this.mailbox.MarkAllReadAsync("bob", "my-team").WaitAsync(TimeSpan.FromSeconds(5));

        var messages = await this.mailbox.ReadAsync("bob", "my-team").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(messages, m => Assert.True(m.Read));
    }

    // ─── Clear ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_empties_existing_inbox()
    {
        await this.mailbox.WriteAsync("bob", "my-team", MakeMessage()).WaitAsync(TimeSpan.FromSeconds(5));

        await this.mailbox.ClearAsync("bob", "my-team").WaitAsync(TimeSpan.FromSeconds(5));

        var messages = await this.mailbox.ReadAsync("bob", "my-team").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(messages);
    }

    [Fact]
    public async Task Clear_on_missing_inbox_does_not_create_file()
    {
        await this.mailbox.ClearAsync("ghost", "my-team").WaitAsync(TimeSpan.FromSeconds(5));

        var inboxPath = Path.Combine(
            this.teamsBaseDir,
            AgentId.SanitizeName("my-team"),
            "inboxes",
            AgentId.SanitizeName("ghost") + ".json");

        Assert.False(File.Exists(inboxPath));
    }

    // ─── Missing / corrupt ────────────────────────────────────────────────────

    [Fact]
    public async Task Read_missing_inbox_returns_empty()
    {
        var messages = await this.mailbox.ReadAsync("nobody", "no-team").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(messages);
    }

    [Fact]
    public async Task Read_corrupt_json_returns_empty()
    {
        var inboxDir = Path.Combine(
            this.teamsBaseDir,
            AgentId.SanitizeName("my-team"),
            "inboxes");
        Directory.CreateDirectory(inboxDir);
        var inboxPath = Path.Combine(inboxDir, AgentId.SanitizeName("bob") + ".json");
        File.WriteAllBytes(inboxPath, Encoding.UTF8.GetBytes("{ not valid json !!"));

        var messages = await this.mailbox.ReadAsync("bob", "my-team").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(messages);
    }

    // ─── Concurrency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_writes_all_land_no_lost_updates()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(i => this.mailbox.WriteAsync("bob", "my-team", MakeMessage("writer", $"msg{i}")))
            .ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        var messages = await this.mailbox.ReadAsync("bob", "my-team").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(20, messages.Count);

        var texts = messages.Select(m => m.Text).ToHashSet();
        for (var i = 0; i < 20; i++)
        {
            Assert.Contains($"msg{i}", texts);
        }
    }
}
