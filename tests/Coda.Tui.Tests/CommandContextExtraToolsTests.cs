using Coda.Mcp;

namespace Coda.Tui.Tests;

public sealed class CommandContextExtraToolsTests
{
    [Fact]
    public void ExtraTools_is_empty_when_no_provider()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();

        Assert.Empty(context.ExtraTools);
    }

    [Fact]
    public void ExtraTools_reflects_the_provider_live()
    {
        var (_, context, _, _) = TestAppBuilder.BuildApp();
        var tool = new ListMcpPromptsTool(new McpClientManager());
        var count = 0;

        // Provider returns a growing list on each call → ExtraTools is recomputed (not snapshotted).
        context.ExtraToolsProvider = () => Enumerable.Repeat<Coda.Agent.ITool>(tool, ++count).ToList();

        Assert.Single(context.ExtraTools);
        Assert.Equal(2, context.ExtraTools.Count);
    }
}
