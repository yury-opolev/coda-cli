using Coda.Agent;
using Coda.Tui.Agent;
using Coda.Tui.Repl;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class TuiPlanApproverTests
{
    private static TestConsole InteractiveConsole()
    {
        var console = new TestConsole().Interactive();
        console.Profile.Width = 200;
        return console;
    }

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
        var console = InteractiveConsole();
        console.Input.PushTextWithEnter("y");
        var session = NewSession(PermissionMode.Plan);

        var approved = await new TuiPlanApprover(console, session).ApproveAsync("do the thing");

        Assert.True(approved);
        Assert.Equal(PermissionMode.AcceptEdits, session.PermissionMode);
    }

    [Fact]
    public async Task Approving_in_BypassPermissions_leaves_mode_unchanged()
    {
        var console = InteractiveConsole();
        console.Input.PushTextWithEnter("y");
        var session = NewSession(PermissionMode.BypassPermissions);

        var approved = await new TuiPlanApprover(console, session).ApproveAsync("do the thing");

        Assert.True(approved);
        Assert.Equal(PermissionMode.BypassPermissions, session.PermissionMode);
    }

    [Fact]
    public async Task Rejecting_leaves_mode_unchanged()
    {
        var console = InteractiveConsole();
        console.Input.PushTextWithEnter("n");
        var session = NewSession(PermissionMode.Plan);

        var approved = await new TuiPlanApprover(console, session).ApproveAsync("do the thing");

        Assert.False(approved);
        Assert.Equal(PermissionMode.Plan, session.PermissionMode);
    }
}
