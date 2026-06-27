using Coda.Agent.Teams;

namespace Engine.Tests.Teams;

public sealed class TeamMessagesTests
{
    // ── IdleNotification ──────────────────────────────────────────────────────

    [Fact]
    public void BuildAndParse_IdleNotification_RoundTrips()
    {
        var json = TeamMessages.BuildIdleNotification(
            from: "worker-1",
            idleReason: "available",
            summary: "done with analysis",
            completedTaskId: "t1",
            completedStatus: "resolved",
            failureReason: null);

        var msg = TeamMessages.TryParseIdleNotification(json);

        Assert.NotNull(msg);
        Assert.Equal("worker-1", msg!.From);
        Assert.Equal("available", msg.IdleReason);
        Assert.Equal("done with analysis", msg.Summary);
        Assert.Equal("t1", msg.CompletedTaskId);
        Assert.Equal("resolved", msg.CompletedStatus);
        Assert.Null(msg.FailureReason);
        Assert.NotEmpty(msg.Timestamp);
    }

    [Fact]
    public void BuildAndParse_IdleNotification_OptionalFieldsNull()
    {
        var json = TeamMessages.BuildIdleNotification("worker-2");
        var msg = TeamMessages.TryParseIdleNotification(json);

        Assert.NotNull(msg);
        Assert.Equal("worker-2", msg!.From);
        Assert.Null(msg.IdleReason);
        Assert.Null(msg.Summary);
        Assert.Null(msg.CompletedTaskId);
        Assert.Null(msg.CompletedStatus);
        Assert.Null(msg.FailureReason);
    }

    // ── ShutdownRequest ───────────────────────────────────────────────────────

    [Fact]
    public void BuildAndParse_ShutdownRequest_RoundTrips()
    {
        var json = TeamMessages.BuildShutdownRequest("r1", "team-lead", "done");

        var msg = TeamMessages.TryParseShutdownRequest(json);

        Assert.NotNull(msg);
        Assert.Equal("r1", msg!.RequestId);
        Assert.Equal("team-lead", msg.From);
        Assert.Equal("done", msg.Reason);
        Assert.NotEmpty(msg.Timestamp);
    }

    [Fact]
    public void BuildAndParse_ShutdownRequest_NullReason()
    {
        var json = TeamMessages.BuildShutdownRequest("r2", "team-lead");
        var msg = TeamMessages.TryParseShutdownRequest(json);

        Assert.NotNull(msg);
        Assert.Equal("r2", msg!.RequestId);
        Assert.Null(msg.Reason);
    }

    // ── ShutdownApproved ──────────────────────────────────────────────────────

    [Fact]
    public void BuildAndParse_ShutdownApproved_RoundTrips()
    {
        var json = TeamMessages.BuildShutdownApproved("r1", "worker-1");

        var msg = TeamMessages.TryParseShutdownApproved(json);

        Assert.NotNull(msg);
        Assert.Equal("r1", msg!.RequestId);
        Assert.Equal("worker-1", msg.From);
        Assert.NotEmpty(msg.Timestamp);
    }

    // ── ShutdownRejected ──────────────────────────────────────────────────────

    [Fact]
    public void BuildAndParse_ShutdownRejected_RoundTrips()
    {
        var json = TeamMessages.BuildShutdownRejected("r1", "worker-1", "still busy");

        var msg = TeamMessages.TryParseShutdownRejected(json);

        Assert.NotNull(msg);
        Assert.Equal("r1", msg!.RequestId);
        Assert.Equal("worker-1", msg.From);
        Assert.Equal("still busy", msg.Reason);
        Assert.NotEmpty(msg.Timestamp);
    }

    // ── PlanApprovalRequest ───────────────────────────────────────────────────

    [Fact]
    public void BuildAndParse_PlanApprovalRequest_RoundTrips()
    {
        var json = TeamMessages.BuildPlanApprovalRequest(
            from: "worker-1",
            planFilePath: "/plans/plan.md",
            planContent: "# Plan\nStep 1",
            requestId: "req-abc");

        var msg = TeamMessages.TryParsePlanApprovalRequest(json);

        Assert.NotNull(msg);
        Assert.Equal("worker-1", msg!.From);
        Assert.Equal("/plans/plan.md", msg.PlanFilePath);
        Assert.Equal("# Plan\nStep 1", msg.PlanContent);
        Assert.Equal("req-abc", msg.RequestId);
        Assert.NotEmpty(msg.Timestamp);
    }

    // ── PlanApprovalResponse ──────────────────────────────────────────────────

    [Fact]
    public void BuildAndParse_PlanApprovalResponse_Approved_RoundTrips()
    {
        var json = TeamMessages.BuildPlanApprovalResponse("req-abc", approved: true, feedback: "looks good");

        var msg = TeamMessages.TryParsePlanApprovalResponse(json);

        Assert.NotNull(msg);
        Assert.Equal("req-abc", msg!.RequestId);
        Assert.True(msg.Approved);
        Assert.Equal("looks good", msg.Feedback);
        Assert.NotEmpty(msg.Timestamp);
    }

    [Fact]
    public void BuildAndParse_PlanApprovalResponse_Rejected_RoundTrips()
    {
        var json = TeamMessages.BuildPlanApprovalResponse("req-abc", approved: false);

        var msg = TeamMessages.TryParsePlanApprovalResponse(json);

        Assert.NotNull(msg);
        Assert.False(msg!.Approved);
        Assert.Null(msg.Feedback);
    }

    // ── Wrong-type and plain-text nulls ───────────────────────────────────────

    [Fact]
    public void TryParse_Returns_Null_For_Wrong_Type()
    {
        var idleJson = TeamMessages.BuildIdleNotification("x");

        Assert.Null(TeamMessages.TryParseShutdownRequest(idleJson));
        Assert.Null(TeamMessages.TryParseShutdownApproved(idleJson));
        Assert.Null(TeamMessages.TryParseShutdownRejected(idleJson));
        Assert.Null(TeamMessages.TryParsePlanApprovalRequest(idleJson));
        Assert.Null(TeamMessages.TryParsePlanApprovalResponse(idleJson));
    }

    [Fact]
    public void TryParse_Returns_Null_For_Plain_Text()
    {
        const string text = "just text";

        Assert.Null(TeamMessages.TryParseIdleNotification(text));
        Assert.Null(TeamMessages.TryParseShutdownRequest(text));
        Assert.Null(TeamMessages.TryParseShutdownApproved(text));
        Assert.Null(TeamMessages.TryParseShutdownRejected(text));
        Assert.Null(TeamMessages.TryParsePlanApprovalRequest(text));
        Assert.Null(TeamMessages.TryParsePlanApprovalResponse(text));
    }

    // ── IsStructuredProtocolMessage ───────────────────────────────────────────

    [Fact]
    public void IsStructuredProtocolMessage_True_For_Protocol_Messages()
    {
        Assert.True(TeamMessages.IsStructuredProtocolMessage(
            TeamMessages.BuildIdleNotification("x")));
        Assert.True(TeamMessages.IsStructuredProtocolMessage(
            TeamMessages.BuildShutdownRequest("r", "x")));
        Assert.True(TeamMessages.IsStructuredProtocolMessage(
            TeamMessages.BuildShutdownApproved("r", "x")));
        Assert.True(TeamMessages.IsStructuredProtocolMessage(
            TeamMessages.BuildShutdownRejected("r", "x", "reason")));
        Assert.True(TeamMessages.IsStructuredProtocolMessage(
            TeamMessages.BuildPlanApprovalRequest("x", "/p", "content", "r")));
        Assert.True(TeamMessages.IsStructuredProtocolMessage(
            TeamMessages.BuildPlanApprovalResponse("r", true)));
    }

    [Fact]
    public void IsStructuredProtocolMessage_True_For_Other_Protocol_Types()
    {
        // These types are in the reference set even though we don't have Build... for them
        Assert.True(TeamMessages.IsStructuredProtocolMessage(
            """{"type":"permission_request","x":1}"""));
        Assert.True(TeamMessages.IsStructuredProtocolMessage(
            """{"type":"permission_response","x":1}"""));
        Assert.True(TeamMessages.IsStructuredProtocolMessage(
            """{"type":"mode_set_request","x":1}"""));
        Assert.True(TeamMessages.IsStructuredProtocolMessage(
            """{"type":"team_permission_update","x":1}"""));
    }

    [Fact]
    public void IsStructuredProtocolMessage_False_For_PlainText_And_Malformed()
    {
        Assert.False(TeamMessages.IsStructuredProtocolMessage("hello"));
        Assert.False(TeamMessages.IsStructuredProtocolMessage("{not json"));
        Assert.False(TeamMessages.IsStructuredProtocolMessage("""{"type":"chat"}"""));
        Assert.False(TeamMessages.IsStructuredProtocolMessage(""));
    }

    // ── FormatTeammateMessage ─────────────────────────────────────────────────

    [Fact]
    public void FormatTeammateMessage_Emits_Tag_And_Escapes_Attributes()
    {
        var result = TeamMessages.FormatTeammateMessage(
            from: "a&b",
            text: "hello",
            color: "red",
            summary: "a \"quote\"");

        Assert.Contains(@"teammate_id=""a&amp;b""", result);
        Assert.Contains(@"color=""red""", result);
        Assert.Contains(@"summary=""a &quot;quote&quot;""", result);
        Assert.Contains("hello", result);
        Assert.StartsWith("<teammate_message ", result);
        Assert.EndsWith("</teammate_message>", result);
    }

    [Fact]
    public void FormatTeammateMessage_Omits_Optional_Attributes_When_Null()
    {
        var result = TeamMessages.FormatTeammateMessage("sender", "body");

        Assert.Contains(@"teammate_id=""sender""", result);
        Assert.DoesNotContain("color=", result);
        Assert.DoesNotContain("summary=", result);
    }

    [Fact]
    public void FormatTeammateMessage_Body_Is_Not_Escaped()
    {
        var result = TeamMessages.FormatTeammateMessage("sender", "<raw> & body", "blue");

        // The text body should be present as-is (no escaping of body)
        Assert.Contains("<raw> & body", result);
    }

    [Fact]
    public void FormatTeammateMessage_Escapes_LessThan_In_Attributes()
    {
        var result = TeamMessages.FormatTeammateMessage("a<b", "text");

        Assert.Contains(@"teammate_id=""a&lt;b""", result);
    }
}
