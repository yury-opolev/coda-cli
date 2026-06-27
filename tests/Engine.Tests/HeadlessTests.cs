using Coda.Agent;
using Coda.Agent.Goals;
using Coda.Sdk;

namespace Engine.Tests;

public sealed class HeadlessOptionsTests
{
    private static HeadlessOptions Parse(params string[] args)
    {
        Assert.True(HeadlessOptions.TryParse(args, out var options, out var error), error);
        return options;
    }

    [Fact]
    public void Parses_prompt_and_defaults()
    {
        var o = Parse("-p", "do the thing");
        Assert.Equal("do the thing", o.Prompt);
        Assert.False(o.Json);
        Assert.Equal(PermissionMode.Default, o.PermissionMode);
    }

    [Fact]
    public void Parses_flags_and_options()
    {
        var o = Parse("--prompt", "x", "--json", "--yolo", "--provider", "copilot", "--model", "gpt-4o", "--cwd", "C:/w");
        Assert.True(o.Json);
        Assert.Equal(PermissionMode.BypassPermissions, o.PermissionMode);
        Assert.Equal("copilot", o.ProviderId);
        Assert.Equal("gpt-4o", o.Model);
        Assert.Equal("C:/w", o.WorkingDirectory);
    }

    [Fact]
    public void Permission_mode_parsing()
    {
        Assert.Equal(PermissionMode.Plan, Parse("-p", "x", "--permission-mode", "plan").PermissionMode);
        Assert.Equal(PermissionMode.AcceptEdits, Parse("-p", "x", "--permission-mode", "acceptEdits").PermissionMode);
        Assert.Equal(PermissionMode.BypassPermissions, Parse("-p", "x", "--permission-mode", "bypass").PermissionMode);
    }

    [Fact]
    public void Missing_prompt_is_an_error()
    {
        Assert.False(HeadlessOptions.TryParse(["--json"], out _, out var error));
        Assert.Contains("prompt is required", error);
    }

    [Fact]
    public void Unknown_argument_is_an_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "x", "--bogus"], out _, out var error));
        Assert.Contains("Unknown argument", error);
    }
}

public sealed class HeadlessSupervisorOptionsTests
{
    private static HeadlessOptions Parse(params string[] args)
    {
        Assert.True(HeadlessOptions.TryParse(args, out var options, out var error), error);
        return options;
    }

    // --- defaults ---

    [Fact]
    public void Defaults_supervisor_fields_are_off()
    {
        var o = Parse("-p", "x");
        Assert.False(o.EnableClassifier);
        Assert.Null(o.Goal);
        Assert.False(o.EnableSessionMemory);
        Assert.Equal(10, o.MaxStopContinuations);
    }

    // --- --yolo-safe ---

    [Fact]
    public void YoloSafe_sets_bypass_and_classifier()
    {
        var o = Parse("-p", "x", "--yolo-safe");
        Assert.Equal(PermissionMode.BypassPermissions, o.PermissionMode);
        Assert.True(o.EnableClassifier);
    }

    [Fact]
    public void Yolo_keeps_bypass_no_classifier()
    {
        var o = Parse("-p", "x", "--yolo");
        Assert.Equal(PermissionMode.BypassPermissions, o.PermissionMode);
        Assert.False(o.EnableClassifier);
    }

    // --- --goal ---

    [Fact]
    public void Goal_parses_text()
    {
        var o = Parse("-p", "x", "--goal", "ship it");
        Assert.Equal("ship it", o.Goal);
    }

    [Fact]
    public void Goal_missing_value_is_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "x", "--goal"], out _, out var error));
        Assert.Contains("--goal", error);
    }

    // --- --session-memory ---

    [Fact]
    public void SessionMemory_flag_sets_field()
    {
        var o = Parse("-p", "x", "--session-memory");
        Assert.True(o.EnableSessionMemory);
    }

    // --- --max-continuations ---

    [Fact]
    public void MaxContinuations_parses_int()
    {
        var o = Parse("-p", "x", "--max-continuations", "3");
        Assert.Equal(3, o.MaxStopContinuations);
    }

    [Fact]
    public void MaxContinuations_invalid_value_is_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "x", "--max-continuations", "abc"], out _, out var error));
        Assert.Contains("--max-continuations", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void MaxContinuations_non_positive_is_error(string value)
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "x", "--max-continuations", value], out _, out var error));
        Assert.Contains("--max-continuations", error);
    }

    [Fact]
    public void MaxContinuations_missing_value_is_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "x", "--max-continuations"], out _, out var error));
        Assert.Contains("--max-continuations", error);
    }
}

public sealed class HeadlessGoalTimeoutTests
{
    private static HeadlessOptions Parse(params string[] args)
    {
        Assert.True(HeadlessOptions.TryParse(args, out var options, out var error), error);
        return options;
    }

    // --- --goal-timeout parsing ---

    [Fact]
    public void GoalTimeout_parses_minutes()
    {
        var o = Parse("-p", "x", "--goal", "g", "--goal-timeout", "30m");
        Assert.Equal(TimeSpan.FromMinutes(30), o.GoalMaxDuration);
    }

    [Fact]
    public void GoalTimeout_parses_hours()
    {
        var o = Parse("-p", "x", "--goal", "g", "--goal-timeout", "2h");
        Assert.Equal(TimeSpan.FromHours(2), o.GoalMaxDuration);
    }

    [Fact]
    public void GoalTimeout_parses_days()
    {
        var o = Parse("-p", "x", "--goal", "g", "--goal-timeout", "1d");
        Assert.Equal(TimeSpan.FromDays(1), o.GoalMaxDuration);
    }

    [Fact]
    public void GoalTimeout_parses_hhmmss()
    {
        var o = Parse("-p", "x", "--goal", "g", "--goal-timeout", "00:45:00");
        Assert.Equal(TimeSpan.FromMinutes(45), o.GoalMaxDuration);
    }

    [Fact]
    public void GoalTimeout_invalid_value_is_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "x", "--goal", "g", "--goal-timeout", "xyz"], out _, out var error));
        Assert.Contains("--goal-timeout", error);
        Assert.Contains("xyz", error);
    }

    [Fact]
    public void GoalTimeout_missing_value_is_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "x", "--goal-timeout"], out _, out var error));
        Assert.Contains("--goal-timeout", error);
    }

    [Fact]
    public void GoalTimeout_without_goal_is_error()
    {
        Assert.False(HeadlessOptions.TryParse(["-p", "x", "--goal-timeout", "30m"], out _, out var error));
        Assert.Contains("--goal", error);
    }

    [Fact]
    public void GoalTimeout_unspecified_leaves_null()
    {
        var o = Parse("-p", "x");
        Assert.Null(o.GoalMaxDuration);
    }

    // --- GoalMaxContinuationsOverride ---

    [Fact]
    public void MaxContinuations_sets_goal_override()
    {
        var o = Parse("-p", "x", "--max-continuations", "500");
        Assert.Equal(500, o.GoalMaxContinuationsOverride);
    }

    [Fact]
    public void MaxContinuations_unspecified_leaves_override_null()
    {
        var o = Parse("-p", "x");
        Assert.Null(o.GoalMaxContinuationsOverride);
    }
}

public sealed class HeadlessLogLevelTests
{
    [Fact]
    public void Parses_log_level_flag()
    {
        var ok = HeadlessOptions.TryParse(["-p", "hi", "--log-level", "debug"], out var options, out var error);

        Assert.True(ok, error);
        Assert.Equal("debug", options.LogLevel);
    }

    [Fact]
    public void Parses_log_level_off()
    {
        var ok = HeadlessOptions.TryParse(["-p", "hi", "--log-level", "off"], out var options, out _);

        Assert.True(ok);
        Assert.Equal("off", options.LogLevel);
    }

    [Fact]
    public void Rejects_invalid_log_level()
    {
        var ok = HeadlessOptions.TryParse(["-p", "hi", "--log-level", "bogus"], out _, out var error);

        Assert.False(ok);
        Assert.Contains("log-level", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Log_level_missing_value_errors()
    {
        var ok = HeadlessOptions.TryParse(["-p", "hi", "--log-level"], out _, out var error);

        Assert.False(ok);
        Assert.Contains("log-level", error!, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class JsonStreamSinkTests
{
    [Fact]
    public void Emits_text_tool_and_result_events()
    {
        var writer = new StringWriter();
        var sink = new JsonStreamSink(writer);

        sink.OnAssistantText("hello");
        sink.OnToolCall("read_file", "{\"path\":\"a\"}");
        sink.OnToolResult("read_file", new ToolResult("contents", IsError: false));
        sink.EmitResult(new RunResult(true, "done", [], "end_turn", null));

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, l => l.Contains("\"type\":\"text\"") && l.Contains("hello"));
        // input must be a parsed JSON object, not a truncated string.
        Assert.Contains(lines, l => l.Contains("\"type\":\"tool_use\"") && l.Contains("read_file") && l.Contains("\"input\":{\"path\":\"a\"}"));
        Assert.Contains(lines, l => l.Contains("\"type\":\"tool_result\"") && l.Contains("contents"));
        Assert.Contains(lines, l => l.Contains("\"type\":\"result\"") && l.Contains("\"subtype\":\"success\"") && l.Contains("done"));
    }

    [Fact]
    public void EmitResult_includes_goalStatus_when_goal_ran()
    {
        var writer = new StringWriter();
        var sink = new JsonStreamSink(writer);
        var goalStatus = new GoalStatus(GoalOutcome.Met, null, 5, TimeSpan.FromMinutes(10), false, false);
        var result = new RunResult(true, "done", [], "end_turn", null) { Goal = goalStatus };
        sink.EmitResult(result);

        var line = writer.ToString().Trim();
        Assert.Contains("\"goalStatus\"", line);
        Assert.Contains("\"outcome\":\"Met\"", line);
        Assert.Contains("\"continuations\":5", line);
    }

    [Fact]
    public void EmitResult_omits_goalStatus_when_no_goal_ran()
    {
        var writer = new StringWriter();
        var sink = new JsonStreamSink(writer);
        var result = new RunResult(true, "done", [], "end_turn", null);
        sink.EmitResult(result);

        var line = writer.ToString().Trim();
        Assert.DoesNotContain("goalStatus", line);
    }

    [Fact]
    public void EmitResult_omits_goalStatus_when_outcome_is_none()
    {
        var writer = new StringWriter();
        var sink = new JsonStreamSink(writer);
        var goalStatus = GoalStatus.None; // Outcome = None
        var result = new RunResult(true, "done", [], "end_turn", null) { Goal = goalStatus };
        sink.EmitResult(result);

        var line = writer.ToString().Trim();
        Assert.DoesNotContain("goalStatus", line);
    }
}
