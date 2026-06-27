using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Teams;
using Coda.Agent.Tools;

namespace Engine.Tests.Teams;

/// <summary>
/// Tests for the send_message tool.
/// Note on IsReadOnly: Coda's ITool.IsReadOnly is a parameterless bool property, not a
/// per-input function. Since sending a message always performs an I/O action (writing to
/// a mailbox), SendMessageTool.IsReadOnly returns false (conservative). This deviates
/// from the TypeScript reference's isReadOnly(input) which returned true for plain-text
/// messages. The IsReadOnly_returns_false test documents this decision.
/// </summary>
public sealed class SendMessageToolTests : IDisposable
{
    private readonly string tempDir;
    private readonly Mailbox mailbox;
    private readonly TeamStore store;
    private readonly ToolContext teamLeadContext;
    private readonly SendMessageTool tool;

    public SendMessageToolTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(this.tempDir);

        this.mailbox = new Mailbox(this.tempDir);
        this.store = new TeamStore(this.tempDir);

        // Create a team with 3 members: team-lead, alice (Color "red"), bob
        var teamFile = new TeamFile(
            Name: "t",
            Description: null,
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LeadAgentId: "team-lead@t",
            Members:
            [
                new TeamMember("team-lead@t", "team-lead", null, null, null, null, 0, true, []),
                new TeamMember("alice@t", "alice", null, null, null, "red", 0, true, []),
                new TeamMember("bob@t", "bob", null, null, null, null, 0, true, []),
            ]);
        this.store.Write("t", teamFile);

        this.teamLeadContext = new ToolContext(this.tempDir)
        {
            TeamMailbox = this.mailbox,
            TeamStore = this.store,
            TeamName = "t",
            AgentName = "team-lead",
        };

        this.tool = new SendMessageTool();
    }

    public void Dispose()
    {
        if (Directory.Exists(this.tempDir))
        {
            Directory.Delete(this.tempDir, recursive: true);
        }
    }

    private static JsonElement ParseInput(string json) =>
        JsonDocument.Parse(json).RootElement;

    // ---------- Direct message lands in recipient inbox ----------

    [Fact]
    public async Task Dm_text_lands_in_recipient_inbox()
    {
        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"alice","summary":"hi","message":"hello"}"""),
            this.teamLeadContext);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("alice", result.Content);

        var inbox = await new Mailbox(this.tempDir).ReadAsync("alice", "t");
        Assert.Single(inbox);
        Assert.Equal("team-lead", inbox[0].From);
        Assert.Equal("hello", inbox[0].Text);
        Assert.False(inbox[0].Read);
    }

    // ---------- Broadcast goes to all but sender ----------

    [Fact]
    public async Task Broadcast_text_goes_to_all_but_sender()
    {
        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"*","summary":"x","message":"hi"}"""),
            this.teamLeadContext);

        Assert.False(result.IsError, result.Content);

        var aliceInbox = await new Mailbox(this.tempDir).ReadAsync("alice", "t");
        var bobInbox = await new Mailbox(this.tempDir).ReadAsync("bob", "t");
        var leadInbox = await new Mailbox(this.tempDir).ReadAsync("team-lead", "t");

        Assert.Single(aliceInbox);
        Assert.Single(bobInbox);
        Assert.Empty(leadInbox);
    }

    // ---------- Missing summary on text → error ----------

    [Fact]
    public async Task Missing_summary_on_text_errors()
    {
        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"alice","message":"hello"}"""),
            this.teamLeadContext);

        Assert.True(result.IsError);
        Assert.Contains("summary", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- @ sign in to → error ----------

    [Fact]
    public async Task At_sign_in_to_errors()
    {
        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"alice@team","summary":"hi","message":"hello"}"""),
            this.teamLeadContext);

        Assert.True(result.IsError);
        Assert.Contains("bare teammate", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Structured broadcast → error ----------

    [Fact]
    public async Task Structured_broadcast_errors()
    {
        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"*","message":{"type":"shutdown_request"}}"""),
            this.teamLeadContext);

        Assert.True(result.IsError);
        Assert.Contains("broadcast", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Shutdown request writes structured message to target ----------

    [Fact]
    public async Task Shutdown_request_writes_structured()
    {
        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"alice","message":{"type":"shutdown_request","reason":"done"}}"""),
            this.teamLeadContext);

        Assert.False(result.IsError, result.Content);

        var aliceInbox = await new Mailbox(this.tempDir).ReadAsync("alice", "t");
        Assert.Single(aliceInbox);

        var parsed = TeamMessages.TryParseShutdownRequest(aliceInbox[0].Text);
        Assert.NotNull(parsed);
        Assert.Equal("done", parsed.Reason);
        Assert.Equal("team-lead", parsed.From);
    }

    // ---------- Shutdown response approve writes shutdown_approved to team-lead ----------

    [Fact]
    public async Task Shutdown_response_approve_writes_approved_to_team_lead()
    {
        var aliceContext = new ToolContext(this.tempDir)
        {
            TeamMailbox = this.mailbox,
            TeamStore = this.store,
            TeamName = "t",
            AgentName = "alice",
        };

        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"team-lead","message":{"type":"shutdown_response","request_id":"r1","approve":true}}"""),
            aliceContext);

        Assert.False(result.IsError, result.Content);

        var leadInbox = await new Mailbox(this.tempDir).ReadAsync("team-lead", "t");
        Assert.Single(leadInbox);

        var parsed = TeamMessages.TryParseShutdownApproved(leadInbox[0].Text);
        Assert.NotNull(parsed);
        Assert.Equal("r1", parsed.RequestId);
    }

    // ---------- Shutdown response targeting non-lead → error ----------

    [Fact]
    public async Task Shutdown_response_to_non_lead_errors()
    {
        var aliceContext = new ToolContext(this.tempDir)
        {
            TeamMailbox = this.mailbox,
            TeamStore = this.store,
            TeamName = "t",
            AgentName = "alice",
        };

        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"bob","message":{"type":"shutdown_response","request_id":"r1","approve":true}}"""),
            aliceContext);

        Assert.True(result.IsError);
        Assert.Contains("team-lead", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Shutdown reject without reason → error ----------

    [Fact]
    public async Task Shutdown_reject_without_reason_errors()
    {
        var aliceContext = new ToolContext(this.tempDir)
        {
            TeamMailbox = this.mailbox,
            TeamStore = this.store,
            TeamName = "t",
            AgentName = "alice",
        };

        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"team-lead","message":{"type":"shutdown_response","request_id":"r1","approve":false}}"""),
            aliceContext);

        Assert.True(result.IsError);
        Assert.Contains("reason", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- IsReadOnly returns false (documented deviation from TS reference) ----------

    [Fact]
    public void IsReadOnly_returns_false()
    {
        // Coda's ITool.IsReadOnly is parameterless; since send_message always writes to
        // a mailbox, we conservatively return false regardless of message type.
        // The TypeScript reference returned true for plain-text messages — that distinction
        // cannot be represented in Coda's ITool interface without per-input ReadOnly support.
        Assert.False(this.tool.IsReadOnly);
    }

    // ---------- No team context → error ----------

    [Fact]
    public async Task No_team_context_returns_error()
    {
        var noTeamContext = new ToolContext(this.tempDir);

        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"alice","summary":"hi","message":"hello"}"""),
            noTeamContext);

        Assert.True(result.IsError);
        Assert.Contains("team context", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Empty "to" → error ----------

    [Fact]
    public async Task Empty_to_errors()
    {
        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"","summary":"hi","message":"hello"}"""),
            this.teamLeadContext);

        Assert.True(result.IsError);
        Assert.Contains("to", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Plan approval response writes to recipient ----------

    [Fact]
    public async Task Plan_approval_response_approve_writes_to_recipient()
    {
        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"alice","message":{"type":"plan_approval_response","request_id":"p1","approve":true,"feedback":"looks good"}}"""),
            this.teamLeadContext);

        Assert.False(result.IsError, result.Content);

        var aliceInbox = await new Mailbox(this.tempDir).ReadAsync("alice", "t");
        Assert.Single(aliceInbox);

        var parsed = TeamMessages.TryParsePlanApprovalResponse(aliceInbox[0].Text);
        Assert.NotNull(parsed);
        Assert.Equal("p1", parsed.RequestId);
        Assert.True(parsed.Approved);
    }

    // ---------- Unknown structured type → error ----------

    [Fact]
    public async Task Unknown_structured_type_errors()
    {
        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"alice","message":{"type":"some_future_type","data":"x"}}"""),
            this.teamLeadContext);

        Assert.True(result.IsError);
        Assert.Contains("Unknown", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Broadcast with 0 non-sender teammates ----------

    [Fact]
    public async Task Broadcast_with_no_other_members_returns_no_teammates_message()
    {
        // Write a team file with only the sender
        var soloTeamFile = new TeamFile(
            Name: "solo",
            Description: null,
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LeadAgentId: "team-lead@solo",
            Members: [new TeamMember("team-lead@solo", "team-lead", null, null, null, null, 0, true, [])]);
        this.store.Write("solo", soloTeamFile);

        var soloContext = new ToolContext(this.tempDir)
        {
            TeamMailbox = this.mailbox,
            TeamStore = this.store,
            TeamName = "solo",
            AgentName = "team-lead",
        };

        var result = await this.tool.ExecuteAsync(
            ParseInput("""{"to":"*","summary":"x","message":"hi"}"""),
            soloContext);

        Assert.False(result.IsError, result.Content);
        Assert.Contains("No teammate", result.Content, StringComparison.OrdinalIgnoreCase);
    }
}
