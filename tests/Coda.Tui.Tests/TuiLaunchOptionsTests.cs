using Coda.Tui.Ui.Mode;

namespace Coda.Tui.Tests;

public sealed class TuiLaunchOptionsTests
{
    [Theory]
    [InlineData("--tui=auto", TuiPreference.Auto)]
    [InlineData("--tui=inline", TuiPreference.Inline)]
    [InlineData("--tui=fullscreen", TuiPreference.Fullscreen)]
    public void Parse_accepts_supported_tui_values(string arg, TuiPreference expected)
    {
        var parsed = TuiLaunchOptions.Parse([arg, "--continue"]);

        Assert.Null(parsed.Error);
        Assert.Equal(expected, parsed.Preference);
        Assert.False(parsed.Plain);
        Assert.Equal(["--continue"], parsed.RemainingArgs);
    }

    [Fact]
    public void Plain_overrides_tui_and_is_removed_from_session_args()
    {
        var parsed = TuiLaunchOptions.Parse(["--tui=fullscreen", "--plain", "--resume", "abc"]);

        Assert.Null(parsed.Error);
        Assert.True(parsed.Plain);
        Assert.Equal(["--resume", "abc"], parsed.RemainingArgs);
    }

    [Fact]
    public void Parse_rejects_unknown_tui_value()
    {
        var parsed = TuiLaunchOptions.Parse(["--tui=windowed"]);

        Assert.Equal("Invalid --tui value 'windowed'. Expected auto, inline, or fullscreen.", parsed.Error);
    }

    [Fact]
    public void Parse_accepts_explicit_mouse_disable()
    {
        var parsed = TuiLaunchOptions.Parse(["--no-mouse"]);

        Assert.True(parsed.MouseDisabled);
        Assert.Empty(parsed.RemainingArgs);
    }

    [Fact]
    public void Parse_extracts_inline_prompt_without_reordering_session_arguments()
    {
        var parsed = TuiLaunchOptions.Parse(
            ["--resume", "abc", "--system-prompt", "exact", "--plain", "--fork", "def", "--no-mouse"]);

        Assert.Null(parsed.Error);
        Assert.Equal("exact", Assert.IsType<Coda.Tui.SystemPromptSource.Inline>(parsed.SystemPromptSource).Text);
        Assert.True(parsed.Plain);
        Assert.True(parsed.MouseDisabled);
        Assert.Equal(["--resume", "abc", "--fork", "def"], parsed.RemainingArgs);
    }

    [Fact]
    public void Parse_extracts_file_prompt_without_reordering_session_arguments()
    {
        var parsed = TuiLaunchOptions.Parse(
            ["--fork", "def", "--system-prompt-file", "prompt.txt", "--resume", "abc", "--tui=inline"]);

        Assert.Null(parsed.Error);
        Assert.Equal("prompt.txt", Assert.IsType<Coda.Tui.SystemPromptSource.FilePath>(parsed.SystemPromptSource).Path);
        Assert.Equal(TuiPreference.Inline, parsed.Preference);
        Assert.Equal(["--fork", "def", "--resume", "abc"], parsed.RemainingArgs);
    }

    [Theory]
    [InlineData("--system-prompt", "--system-prompt requires a value.")]
    [InlineData("--system-prompt-file", "--system-prompt-file requires a value.")]
    [InlineData("--system-prompt=exact", "System prompt options require separate arguments for the flag and value.")]
    [InlineData("--system-prompt-file=prompt.txt", "System prompt options require separate arguments for the flag and value.")]
    public void Parse_rejects_missing_or_equals_prompt_sources(string argument, string expectedError)
    {
        var parsed = TuiLaunchOptions.Parse([argument]);

        Assert.Equal(expectedError, parsed.Error);
    }

    [Fact]
    public void Parse_rejects_duplicate_prompt_sources()
    {
        var parsed = TuiLaunchOptions.Parse(
            ["--system-prompt", "one", "--system-prompt-file", "prompt.txt"]);

        Assert.Equal("Specify only one of --system-prompt or --system-prompt-file, once.", parsed.Error);
    }

    [Fact]
    public void Parse_preserves_exact_dash_prefixed_prompt_value()
    {
        var parsed = TuiLaunchOptions.Parse(["--system-prompt", "--exact-value"]);

        Assert.Null(parsed.Error);
        Assert.Equal("--exact-value", Assert.IsType<Coda.Tui.SystemPromptSource.Inline>(parsed.SystemPromptSource).Text);
    }
}

/// <summary>
/// The interactive launcher never parsed --yolo at all: only `coda run` and `coda serve` understood it,
/// so starting the TUI with it left the session in Default mode and asked for permission as usual, with
/// nothing to say the flag had been ignored.
/// </summary>
public sealed class TuiLaunchPermissionOptionsTests
{
    [Fact]
    public void Yolo_selects_bypass_mode()
    {
        var options = TuiLaunchOptions.Parse(["--yolo"]);

        Assert.Equal(Coda.Agent.PermissionMode.BypassPermissions, options.PermissionMode);
        Assert.False(options.EnableBypassClassifier);
        Assert.Null(options.Error);
    }

    [Fact]
    public void Yolo_safe_selects_bypass_mode_with_the_classifier()
    {
        var options = TuiLaunchOptions.Parse(["--yolo-safe"]);

        Assert.Equal(Coda.Agent.PermissionMode.BypassPermissions, options.PermissionMode);
        Assert.True(options.EnableBypassClassifier);
    }

    [Theory]
    [InlineData("default", Coda.Agent.PermissionMode.Default)]
    [InlineData("acceptEdits", Coda.Agent.PermissionMode.AcceptEdits)]
    [InlineData("plan", Coda.Agent.PermissionMode.Plan)]
    [InlineData("bypass", Coda.Agent.PermissionMode.BypassPermissions)]
    [InlineData("yolo", Coda.Agent.PermissionMode.BypassPermissions)]
    public void Permission_mode_accepts_each_mode(string value, Coda.Agent.PermissionMode expected)
    {
        Assert.Equal(expected, TuiLaunchOptions.Parse(["--permission-mode", value]).PermissionMode);
    }

    [Fact]
    public void Permission_mode_also_accepts_the_equals_form()
    {
        Assert.Equal(
            Coda.Agent.PermissionMode.Plan,
            TuiLaunchOptions.Parse(["--permission-mode=plan"]).PermissionMode);
    }

    [Fact]
    public void An_unknown_permission_mode_is_an_error_rather_than_a_silent_default()
    {
        var options = TuiLaunchOptions.Parse(["--permission-mode", "nonsense"]);

        Assert.NotNull(options.Error);
    }

    [Fact]
    public void A_permission_mode_with_no_value_is_an_error()
    {
        Assert.NotNull(TuiLaunchOptions.Parse(["--permission-mode"]).Error);
    }

    [Fact]
    public void No_permission_flag_leaves_the_mode_unset()
    {
        Assert.Null(TuiLaunchOptions.Parse([]).PermissionMode);
    }

    [Fact]
    public void The_permission_flags_are_consumed_rather_than_left_for_the_session_parser()
    {
        // A leftover --yolo at argv[0] is exactly what used to defeat the resume intent.
        var options = TuiLaunchOptions.Parse(["--yolo", "--resume", "abc123"]);

        Assert.Equal(["--resume", "abc123"], options.RemainingArgs);
    }

    [Fact]
    public void The_permission_mode_value_is_consumed_with_its_flag()
    {
        var options = TuiLaunchOptions.Parse(["--permission-mode", "plan", "--resume", "abc123"]);

        Assert.Equal(["--resume", "abc123"], options.RemainingArgs);
    }
}

/// <summary>
/// Parsing the flag is only half the job: the mode has to reach the session, or --yolo would still ask
/// for permission on every tool while claiming to have been understood.
/// </summary>
public sealed class LaunchPermissionModeWiringTests
{
    [Fact]
    public void Yolo_puts_the_session_in_bypass_mode()
    {
        var session = Coda.Tui.DefaultInteractiveSessionRunner.CreateSessionState("anthropic", TuiLaunchOptions.Parse(["--yolo"]));

        Assert.Equal(Coda.Agent.PermissionMode.BypassPermissions, session.PermissionMode);
        Assert.False(session.EnableBypassClassifier);
    }

    [Fact]
    public void Yolo_safe_puts_the_session_in_bypass_mode_with_the_classifier()
    {
        var session = Coda.Tui.DefaultInteractiveSessionRunner.CreateSessionState("anthropic", TuiLaunchOptions.Parse(["--yolo-safe"]));

        Assert.Equal(Coda.Agent.PermissionMode.BypassPermissions, session.PermissionMode);
        Assert.True(session.EnableBypassClassifier);
    }

    [Fact]
    public void Permission_mode_plan_reaches_the_session()
    {
        var session = Coda.Tui.DefaultInteractiveSessionRunner.CreateSessionState(
            "anthropic", TuiLaunchOptions.Parse(["--permission-mode", "plan"]));

        Assert.Equal(Coda.Agent.PermissionMode.Plan, session.PermissionMode);
    }

    [Fact]
    public void Without_a_flag_the_session_keeps_the_default_mode()
    {
        var session = Coda.Tui.DefaultInteractiveSessionRunner.CreateSessionState("anthropic", TuiLaunchOptions.Parse([]));

        Assert.Equal(Coda.Agent.PermissionMode.Default, session.PermissionMode);
        Assert.False(session.EnableBypassClassifier);
    }

    [Fact]
    public void Yolo_and_resume_together_keep_both_the_mode_and_the_intent()
    {
        // The reported bug: --yolo defeated the resume AND was itself ignored.
        var options = TuiLaunchOptions.Parse(["--yolo", "--resume", "abc123"]);
        var session = Coda.Tui.DefaultInteractiveSessionRunner.CreateSessionState("anthropic", options);
        var intent = SessionCli.ParseStartupIntent(options.RemainingArgs);

        Assert.Equal(Coda.Agent.PermissionMode.BypassPermissions, session.PermissionMode);
        Assert.Equal("abc123", intent.ResumeId);
        Assert.True(intent.HasIntent);
    }

    // The permission flag TRAILING the session intent — `coda --resume "xxx" --yolo` — is the other
    // half of the same report, and the ordering most people actually type.

    [Fact]
    public void Resume_with_id_then_trailing_yolo_keeps_both()
    {
        var options = TuiLaunchOptions.Parse(["--resume", "abc123", "--yolo"]);
        var session = Coda.Tui.DefaultInteractiveSessionRunner.CreateSessionState("anthropic", options);
        var intent = SessionCli.ParseStartupIntent(options.RemainingArgs);

        Assert.Equal(Coda.Agent.PermissionMode.BypassPermissions, session.PermissionMode);
        Assert.Equal("abc123", intent.ResumeId);
        Assert.True(intent.HasIntent);
    }

    [Fact]
    public void Resume_with_id_then_trailing_yolo_safe_keeps_the_classifier_too()
    {
        var options = TuiLaunchOptions.Parse(["--resume", "abc123", "--yolo-safe"]);
        var session = Coda.Tui.DefaultInteractiveSessionRunner.CreateSessionState("anthropic", options);
        var intent = SessionCli.ParseStartupIntent(options.RemainingArgs);

        Assert.Equal(Coda.Agent.PermissionMode.BypassPermissions, session.PermissionMode);
        Assert.True(session.EnableBypassClassifier);
        Assert.Equal("abc123", intent.ResumeId);
    }

    [Fact]
    public void Continue_then_trailing_yolo_keeps_both()
    {
        var options = TuiLaunchOptions.Parse(["--continue", "--yolo"]);
        var session = Coda.Tui.DefaultInteractiveSessionRunner.CreateSessionState("anthropic", options);
        var intent = SessionCli.ParseStartupIntent(options.RemainingArgs);

        Assert.Equal(Coda.Agent.PermissionMode.BypassPermissions, session.PermissionMode);
        Assert.True(intent.ContinueLatest);
    }

    [Fact]
    public void Short_resume_with_id_then_trailing_permission_mode_keeps_both()
    {
        var options = TuiLaunchOptions.Parse(["-r", "abc123", "--permission-mode", "bypass"]);
        var session = Coda.Tui.DefaultInteractiveSessionRunner.CreateSessionState("anthropic", options);
        var intent = SessionCli.ParseStartupIntent(options.RemainingArgs);

        Assert.Equal(Coda.Agent.PermissionMode.BypassPermissions, session.PermissionMode);
        Assert.Equal("abc123", intent.ResumeId);
    }

    [Fact]
    public void Fork_with_id_then_trailing_yolo_keeps_both()
    {
        var options = TuiLaunchOptions.Parse(["--fork", "abc123", "--yolo"]);
        var session = Coda.Tui.DefaultInteractiveSessionRunner.CreateSessionState("anthropic", options);
        var intent = SessionCli.ParseStartupIntent(options.RemainingArgs);

        Assert.Equal(Coda.Agent.PermissionMode.BypassPermissions, session.PermissionMode);
        Assert.Equal("abc123", intent.ResumeId);
        Assert.True(intent.Fork);
    }

    // A resume that resolves to nothing must SAY so. Starting empty with a vague, information-level
    // "No session to continue." is indistinguishable from --resume being ignored outright.

    [Fact]
    public void Missing_resume_id_names_the_id_and_the_directory_and_warns()
    {
        var intent = SessionCli.ParseStartupIntent(["--resume", "abc123"]);

        var (message, isWarning) = SessionCli.DescribeMissingTarget(intent, @"C:\work\repo");

        Assert.True(isWarning);
        Assert.Contains("abc123", message);
        Assert.Contains(@"C:\work\repo", message);
        Assert.Contains("not found", message);
        Assert.Contains("resume", message);
        Assert.DoesNotContain("No session to continue.", message);
    }

    [Fact]
    public void Missing_fork_id_reports_forking_rather_than_resuming()
    {
        var intent = SessionCli.ParseStartupIntent(["--fork", "abc123"]);

        var (message, isWarning) = SessionCli.DescribeMissingTarget(intent, @"C:\work\repo");

        Assert.True(isWarning);
        Assert.Contains("abc123", message);
        Assert.Contains("fork", message);
    }

    [Fact]
    public void Missing_continue_target_stays_informational_and_names_the_directory()
    {
        var intent = SessionCli.ParseStartupIntent(["--continue"]);

        var (message, isWarning) = SessionCli.DescribeMissingTarget(intent, @"C:\work\repo");

        // Nothing specific was asked for, so an empty directory is not a warning.
        Assert.False(isWarning);
        Assert.Contains("No session to continue", message);
        Assert.Contains(@"C:\work\repo", message);
    }

    [Fact]
    public void Missing_fork_latest_target_reports_forking()
    {
        var intent = SessionCli.ParseStartupIntent(["--fork"]);

        var (message, isWarning) = SessionCli.DescribeMissingTarget(intent, @"C:\work\repo");

        Assert.False(isWarning);
        Assert.Contains("No session to fork", message);
    }
}
