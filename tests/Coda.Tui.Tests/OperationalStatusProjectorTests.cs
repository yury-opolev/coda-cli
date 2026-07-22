using Coda.Agent;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

public sealed class OperationalStatusProjectorTests
{
    [Fact]
    public void Pending_approval_has_highest_priority()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            Permission = new PermissionStatus(PermissionMode.Default, 1),
            ActiveOperation = new ActiveOperation("startup", "Starting…", null),
            RunningTasks = 2,
        };

        Assert.Equal(
            new OperationalStatus("Waiting for approval", OperationalTone.Approval, Animated: false),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Non_confirmation_prompt_waits_for_input_without_claiming_approval()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            PendingPrompt = UiPromptRequest.Select(
                "Choose model",
                [new UiPromptOption("one", "One")]),
        };

        Assert.Equal(
            new OperationalStatus("Waiting for input", OperationalTone.Waiting, Animated: false),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Startup_projects_initializing()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("startup", "Starting…", null),
        };

        Assert.Equal(
            new OperationalStatus("Initializing…", OperationalTone.Initializing, Animated: true),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Incomplete_tool_outranks_active_turn()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("turn", "answer", null),
            Transcript =
            [
                new ToolTranscriptBlock(
                    Guid.NewGuid(),
                    "dotnet test",
                    "{}",
                    null,
                    null,
                    IsError: false,
                    Complete: false),
            ],
        };

        Assert.Equal(
            new OperationalStatus("Working · dotnet test", OperationalTone.Working, Animated: true),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Tiny_mode_hides_incomplete_tool_name()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            ActiveOperation = new ActiveOperation("turn", "answer", null),
            Transcript =
            [
                new ToolTranscriptBlock(
                    Guid.NewGuid(), "dotnet test", "{}", null, null, IsError: false, Complete: false),
            ],
        };

        Assert.Equal(
            new OperationalStatus("Working", OperationalTone.Working, Animated: true),
            OperationalStatusProjector.Project(snapshot, ToolDisplayMode.Tiny));
    }

    [Theory]
    [InlineData("high")]
    [InlineData("max")]
    public void High_and_max_active_turns_project_intensive_thinking(string effort)
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            EffectiveEffort = effort,
            ActiveOperation = new ActiveOperation("turn", "answer", null),
        };

        Assert.Equal(
            new OperationalStatus("Thinking deeply", OperationalTone.Thinking, Animated: true),
            OperationalStatusProjector.Project(snapshot));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("low")]
    [InlineData("medium")]
    public void Other_active_turns_project_generic_working_without_echoing_the_prompt(string effort)
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            EffectiveEffort = effort,
            ActiveOperation = new ActiveOperation("turn", "running tests", null),
        };

        // A running turn shows a concise, generic status; it must never echo the last submitted prompt
        // (the turn's label) beside "Working".
        Assert.Equal(
            new OperationalStatus("Working", OperationalTone.Working, Animated: true),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Running_turn_never_echoes_a_multiline_prompt()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            EffectiveEffort = "low",
            ActiveOperation = new ActiveOperation("turn", "please summarize\nthe whole file", null),
        };

        Assert.Equal(
            new OperationalStatus("Working", OperationalTone.Working, Animated: true),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Background_tasks_project_waiting_count()
    {
        var snapshot = UiSessionSnapshot.Empty with { RunningTasks = 2 };

        Assert.Equal(
            new OperationalStatus(
                "Waiting for 2 background tasks",
                OperationalTone.Waiting,
                Animated: false),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Idle_error_projects_a_concise_error()
    {
        var snapshot = UiSessionSnapshot.Empty with
        {
            Notification = new UiNotification("Connection failed\nstack details", UiNotificationLevel.Error),
        };

        Assert.Equal(
            new OperationalStatus("Connection failed", OperationalTone.Error, Animated: false),
            OperationalStatusProjector.Project(snapshot));
    }

    [Fact]
    public void Idle_snapshot_is_ready()
    {
        Assert.Equal(
            new OperationalStatus("Ready", OperationalTone.Ready, Animated: false),
            OperationalStatusProjector.Project(UiSessionSnapshot.Empty));
    }
}
