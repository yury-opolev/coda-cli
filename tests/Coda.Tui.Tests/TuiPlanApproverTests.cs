using System.Collections.Immutable;
using Coda.Agent;
using Coda.Tui.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Tests;

public sealed class TuiPlanApproverTests
{
    private static SessionState NewSession(PermissionMode mode)
    {
        var session = new SessionState("anthropic", Directory.GetCurrentDirectory())
        {
            PermissionMode = mode,
        };
        return session;
    }

    [Fact]
    public async Task Approving_in_Plan_mode_promotes_to_AcceptEdits()
    {
        var session = NewSession(PermissionMode.Plan);

        var approved = await CreateApprover(session, approved: true).ApproveAsync("do the thing");

        Assert.True(approved);
        Assert.Equal(PermissionMode.AcceptEdits, session.PermissionMode);
    }

    [Fact]
    public async Task Approving_in_BypassPermissions_leaves_mode_unchanged()
    {
        var session = NewSession(PermissionMode.BypassPermissions);

        var approved = await CreateApprover(session, approved: true).ApproveAsync("do the thing");

        Assert.True(approved);
        Assert.Equal(PermissionMode.BypassPermissions, session.PermissionMode);
    }

    [Fact]
    public async Task Rejecting_leaves_mode_unchanged()
    {
        var session = NewSession(PermissionMode.Plan);

        var approved = await CreateApprover(session, approved: false).ApproveAsync("do the thing");

        Assert.False(approved);
        Assert.Equal(PermissionMode.Plan, session.PermissionMode);
    }

    private static TuiPlanApprover CreateApprover(SessionState session, bool approved)
    {
        ImmutableArray<string> selectedIds = approved ? ["yes"] : ["no"];
        return new TuiPlanApprover(
            new RecordingPromptService(new UiPromptResponse(false, selectedIds, null)),
            new RecordingUiEvents(),
            session);
    }
}
