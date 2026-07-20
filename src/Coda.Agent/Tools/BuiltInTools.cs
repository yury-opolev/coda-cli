namespace Coda.Agent.Tools;

/// <summary>The default built-in tool set.</summary>
public static class BuiltInTools
{
    public static IReadOnlyList<ITool> All() =>
    [
        new ReadFileTool(),
        new ListDirTool(),
        new GlobTool(),
        new GrepTool(),
        new WriteFileTool(),
        new EditTool(),
        new RunCommandTool(),
        new WebFetchTool(),
        new WebSearchTool(new DuckDuckGoSearchBackend()),
        new TodoWriteTool(),
        new AskUserQuestionTool(),
        new ExitPlanModeTool(),
        new ScheduleCreateTool(() => DateTime.UtcNow),
        new ScheduleListTool(),
        new ScheduleDeleteTool(),
        new BackgroundTaskStartTool(),
        new BackgroundTaskOutputTool(),
        new BackgroundTaskStopTool(),
        new TaskListTool(),
        new TaskGetTool(),
        new TaskPeekTool(),
        new TaskSendTool(),
        new SleepTool(),
        new NotebookEditTool(),
        new GitWorktreeTool(),
    ];
}
