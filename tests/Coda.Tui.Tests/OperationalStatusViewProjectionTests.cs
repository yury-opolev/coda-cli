using Coda.Agent;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class OperationalStatusViewProjectionTests
{
    [Fact]
    public void Pending_approval_projection_renders_a_single_bang_prefix()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            Permission = new PermissionStatus(PermissionMode.Default, 1),
        };

        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var view = new OperationalStatusView(app, TuiTheme.WarmEmber);

        view.SetStatus(OperationalStatusProjector.Project(snapshot));

        Assert.Equal("! Waiting for approval", view.RenderText());
    }

    [Fact]
    public void Pending_input_projection_renders_a_single_ring_prefix()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            PendingPrompt = UiPromptRequest.Select(
                "Choose model",
                [new UiPromptOption("one", "One")]),
        };

        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var view = new OperationalStatusView(app, TuiTheme.WarmEmber);

        view.SetStatus(OperationalStatusProjector.Project(snapshot));

        Assert.Equal("◌ Waiting for input", view.RenderText());
    }
}
