using System.Collections.Immutable;
using Coda.Agent;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;

namespace Coda.Tui.Tests;

/// <summary>
/// Verifies that the tool-outcome and permission-outcome colour rules produce the correct
/// <see cref="TranscriptRole"/> assignments: green only when everything succeeded, red only when
/// everything failed or a permission was rejected, orange/yellow for partial failures or approvals.
/// </summary>
public sealed class ToolOutcomeColourTests
{
    // -----------------------------------------------------------------------
    // SummaryRole unit tests
    // -----------------------------------------------------------------------

    [Fact]
    public void SummaryRole_all_succeeded_returns_ToolSuccess()
    {
        var summary = MakeSummary(totalCalls: 3, failedCalls: 0, cancelledCalls: 0);

        Assert.Equal(TranscriptRole.ToolSuccess, TranscriptBlockFormatter.SummaryRole(summary));
    }

    [Fact]
    public void SummaryRole_one_of_three_failed_returns_ToolPartialFailure()
    {
        var summary = MakeSummary(totalCalls: 3, failedCalls: 1, cancelledCalls: 0);

        Assert.Equal(TranscriptRole.ToolPartialFailure, TranscriptBlockFormatter.SummaryRole(summary));
    }

    [Fact]
    public void SummaryRole_all_three_failed_returns_Error()
    {
        var summary = MakeSummary(totalCalls: 3, failedCalls: 3, cancelledCalls: 0);

        Assert.Equal(TranscriptRole.Error, TranscriptBlockFormatter.SummaryRole(summary));
    }

    [Fact]
    public void SummaryRole_cancelled_with_no_failures_returns_Warning()
    {
        var summary = MakeSummary(totalCalls: 2, failedCalls: 0, cancelledCalls: 1);

        Assert.Equal(TranscriptRole.Warning, TranscriptBlockFormatter.SummaryRole(summary));
    }

    [Fact]
    public void SummaryRole_some_failed_and_cancelled_failure_classification_wins()
    {
        var summary = MakeSummary(totalCalls: 3, failedCalls: 1, cancelledCalls: 1);

        // Failure classification wins over cancellation: partial failure, not warning.
        Assert.Equal(TranscriptRole.ToolPartialFailure, TranscriptBlockFormatter.SummaryRole(summary));
    }

    [Fact]
    public void SummaryRole_empty_batch_does_not_return_Error()
    {
        var summary = MakeSummary(totalCalls: 0, failedCalls: 0, cancelledCalls: 0);

        // An empty batch must never be classified as "all failed".
        Assert.NotEqual(TranscriptRole.Error, TranscriptBlockFormatter.SummaryRole(summary));
    }

    // -----------------------------------------------------------------------
    // PermissionRole unit tests
    // -----------------------------------------------------------------------

    [Fact]
    public void PermissionRole_approved_returns_PermissionApproved()
    {
        Assert.Equal(TranscriptRole.PermissionApproved, TranscriptBlockFormatter.PermissionRole(true));
    }

    [Fact]
    public void PermissionRole_rejected_returns_Permission()
    {
        Assert.Equal(TranscriptRole.Permission, TranscriptBlockFormatter.PermissionRole(false));
    }

    [Fact]
    public void PermissionRole_null_returns_Question()
    {
        Assert.Equal(TranscriptRole.Question, TranscriptBlockFormatter.PermissionRole(null));
    }

    // -----------------------------------------------------------------------
    // End-to-end: TranscriptBlockFormatter.Format
    // -----------------------------------------------------------------------

    [Fact]
    public void Format_completed_all_succeeded_activity_has_ToolSuccess_summary_row()
    {
        var block = MakeActivity(ToolActivityCompletionState.Completed,
            MakeCall("read_file", ToolCallStatus.Succeeded),
            MakeCall("write_file", ToolCallStatus.Succeeded));

        var lines = TranscriptBlockFormatter.Format(block, width: 80, ToolDisplayMode.Summary);

        Assert.Equal(TranscriptRole.ToolSuccess, lines[0].Role);
    }

    [Fact]
    public void Format_completed_partial_failure_activity_has_ToolPartialFailure_summary_row()
    {
        var block = MakeActivity(ToolActivityCompletionState.Completed,
            MakeCall("read_file", ToolCallStatus.Succeeded),
            MakeCall("write_file", ToolCallStatus.Failed),
            MakeCall("grep", ToolCallStatus.Succeeded));

        var lines = TranscriptBlockFormatter.Format(block, width: 80, ToolDisplayMode.Summary);

        Assert.Equal(TranscriptRole.ToolPartialFailure, lines[0].Role);
    }

    [Fact]
    public void Format_completed_all_failed_activity_has_Error_summary_row()
    {
        var block = MakeActivity(ToolActivityCompletionState.Completed,
            MakeCall("read_file", ToolCallStatus.Failed),
            MakeCall("write_file", ToolCallStatus.Failed),
            MakeCall("grep", ToolCallStatus.Failed));

        var lines = TranscriptBlockFormatter.Format(block, width: 80, ToolDisplayMode.Summary);

        Assert.Equal(TranscriptRole.Error, lines[0].Role);
    }

    [Fact]
    public void Format_permission_approved_block_has_PermissionApproved_role()
    {
        var block = new PermissionTranscriptBlock(Guid.NewGuid(), "write_file", "path/x", Allowed: true);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.All(lines, line => Assert.Equal(TranscriptRole.PermissionApproved, line.Role));
    }

    [Fact]
    public void Format_permission_rejected_block_has_Permission_role()
    {
        var block = new PermissionTranscriptBlock(Guid.NewGuid(), "write_file", "path/x", Allowed: false);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.All(lines, line => Assert.Equal(TranscriptRole.Permission, line.Role));
    }

    [Fact]
    public void Format_permission_pending_block_has_Question_role()
    {
        var block = new PermissionTranscriptBlock(Guid.NewGuid(), "write_file", "path/x", Allowed: null);

        var lines = TranscriptBlockFormatter.Format(block, width: 80);

        Assert.All(lines, line => Assert.Equal(TranscriptRole.Question, line.Role));
    }

    // -----------------------------------------------------------------------
    // Color-distinction guard: ToolSuccess / ToolPartialFailure / PermissionApproved != Error
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("default")]
    [InlineData("warm-ember")]
    [InlineData("cool-dark")]
    public void ToolSuccess_color_is_distinct_from_Error_in_all_built_in_themes(string themeName)
    {
        CodaThemes.TryGet(themeName, out var theme);

        Assert.NotEqual(theme.Tui.Error.TrueColor, theme.Tui.ToolSuccess.TrueColor);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("warm-ember")]
    [InlineData("cool-dark")]
    public void ToolPartialFailure_color_is_distinct_from_Error_in_all_built_in_themes(string themeName)
    {
        CodaThemes.TryGet(themeName, out var theme);

        Assert.NotEqual(theme.Tui.Error.TrueColor, theme.Tui.ToolPartialFailure.TrueColor);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("warm-ember")]
    [InlineData("cool-dark")]
    public void PermissionApproved_color_is_distinct_from_Error_in_all_built_in_themes(string themeName)
    {
        CodaThemes.TryGet(themeName, out var theme);

        Assert.NotEqual(theme.Tui.Error.TrueColor, theme.Tui.PermissionApproved.TrueColor);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ToolActivitySummary MakeSummary(int totalCalls, int failedCalls, int cancelledCalls) =>
        new("root", "activity", totalCalls, failedCalls, cancelledCalls, SkippedCalls: 0, HomogeneousToolName: null);

    private static ToolActivityTranscriptBlock MakeActivity(
        ToolActivityCompletionState completionState,
        params ToolActivityCall[] calls) =>
        new(Guid.NewGuid(), "root", "activity", calls.ToImmutableArray(), completionState);

    private static ToolActivityCall MakeCall(string toolName, ToolCallStatus status) =>
        new(
            Guid.NewGuid().ToString("N"),
            "root:root",
            toolName,
            "{}",
            "preview",
            status,
            ElapsedMs: 10,
            Result: null,
            Error: null);

    // -------------------------------------------------------------------------
    // Red is reserved for failure and rejection
    // -------------------------------------------------------------------------

    public static TheoryData<string> BuiltInThemeNames() => new() { "default", "warm-ember", "cool-dark" };

    [Theory]
    [MemberData(nameof(BuiltInThemeNames))]
    public void PermissionApproved_is_never_the_theme_success_green(string themeName)
    {
        Assert.True(CodaThemes.TryGet(themeName, out var theme));

        // An approved tool is noteworthy, not "all clear": it must not borrow the success green, or the
        // transcript would read an approval as a completed batch.
        Assert.NotEqual(theme.Tui.ToolSuccess, theme.Tui.PermissionApproved);
    }

    [Theory]
    [MemberData(nameof(BuiltInThemeNames))]
    public void PermissionApproved_and_partial_failure_are_warm_not_cool(string themeName)
    {
        Assert.True(CodaThemes.TryGet(themeName, out var theme));

        // Orange/yellow means red > green and red > blue. This is what §3 asks for, and it is what keeps
        // the role legible as "noteworthy" in every palette rather than drifting to the theme's accent hue.
        AssertWarm(theme.Tui.PermissionApproved);
        AssertWarm(theme.Tui.ToolPartialFailure);

        static void AssertWarm(TuiThemeColor role)
        {
            Assert.True(role.TrueColor.R > role.TrueColor.G, "expected red channel above green");
            Assert.True(role.TrueColor.G > role.TrueColor.B, "expected green channel above blue");
        }
    }}
