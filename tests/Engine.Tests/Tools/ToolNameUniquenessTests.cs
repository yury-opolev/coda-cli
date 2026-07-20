using System.Reflection;
using Coda.Agent;
using Coda.Agent.Tools;

namespace Engine.Tests.Tools;

/// <summary>
/// Regression guard for the Teams removal: the built-in and execution tool set must have
/// unique tool names, and the execution <c>task_stop</c> name must resolve to
/// <see cref="BackgroundTaskStopTool"/> (not the removed Teams task-board stop tool).
/// </summary>
public sealed class ToolNameUniquenessTests
{
    private static IReadOnlyList<ITool> InstantiableTools()
    {
        var toolInterface = typeof(ITool);
        return [.. toolInterface.Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && toolInterface.IsAssignableFrom(t))
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (ITool)Activator.CreateInstance(t)!)];
    }

    [Fact]
    public void All_instantiable_tools_have_unique_names()
    {
        var duplicates = InstantiableTools()
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => t.GetType().Name))}")
            .ToList();

        Assert.True(duplicates.Count == 0, $"Duplicate tool names found: {string.Join("; ", duplicates)}");
    }

    [Fact]
    public void task_stop_resolves_to_background_task_stop_tool()
    {
        var taskStopTools = InstantiableTools().Where(t => t.Name == "task_stop").ToList();

        var single = Assert.Single(taskStopTools);
        Assert.IsType<BackgroundTaskStopTool>(single);
    }
}
