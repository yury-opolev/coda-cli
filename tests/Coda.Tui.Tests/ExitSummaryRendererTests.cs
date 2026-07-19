using System.Text;
using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using LlmClient;
using Spectre.Console.Testing;

namespace Coda.Tui.Tests;

public sealed class ExitSummaryRendererTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    // Rates deliberately differ from the built-in Sonnet fallback (3 / 15) so any test that
    // observes these numbers proves the ModelCatalog was consulted rather than the fallback table.
    private static ModelCatalog TestCatalog() => new(
        new Dictionary<string, IReadOnlyDictionary<string, CatalogModel>>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new Dictionary<string, CatalogModel>(StringComparer.OrdinalIgnoreCase)
            {
                ["test-model"] = new CatalogModel("test-model", InputPerMTok: 7m, OutputPerMTok: 21m),
            },
        });

    private static SessionState BuildSession()
    {
        var session = new SessionState("claude-ai", "C:\\work")
        {
            SessionId = "sess-123",
            Model = "test-model",
            Effort = "high",
            SessionUsage = new TokenUsage(1_500, 300),
        };
        session.History.Add(ChatMessage.UserText("one"));
        session.History.Add(ChatMessage.UserText("two"));
        session.History.Add(ChatMessage.UserText("three"));
        return session;
    }

    private static ContextReport BuildContext(bool isExact) => new()
    {
        Model = "test-model",
        MaxTokens = 200_000,
        UsedTokens = 12_000,
        Categories = [],
        IsExact = isExact,
        MessageCount = 3,
    };

    private static TestConsole NewConsole()
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        return console;
    }

    // ---- Projection (SessionExitSnapshot.Create) ----

    [Fact]
    public void Create_computes_duration()
    {
        var snapshot = SessionExitSnapshot.Create(
            BuildSession(), BuildContext(isExact: true), Start, Start.AddMinutes(2).AddSeconds(5), TestCatalog());

        Assert.Equal(TimeSpan.FromSeconds(125), snapshot.Duration);
    }

    [Fact]
    public void Create_clamps_negative_duration_to_zero()
    {
        var snapshot = SessionExitSnapshot.Create(
            BuildSession(), null, Start, Start.AddSeconds(-10), TestCatalog());

        Assert.Equal(TimeSpan.Zero, snapshot.Duration);
    }

    [Fact]
    public void Create_projects_identity_provider_model_effort()
    {
        var snapshot = SessionExitSnapshot.Create(
            BuildSession(), null, Start, Start.AddMinutes(1), TestCatalog());

        Assert.Equal("sess-123", snapshot.SessionId);
        Assert.Equal("C:\\work", snapshot.WorkingDirectory);
        Assert.Equal("claude-ai", snapshot.ProviderId);
        Assert.Equal("test-model", snapshot.Model);
        Assert.Equal("high", snapshot.Effort);
        Assert.True(snapshot.HasSession);
    }

    [Fact]
    public void Create_projects_message_and_token_totals()
    {
        var snapshot = SessionExitSnapshot.Create(
            BuildSession(), null, Start, Start.AddMinutes(1), TestCatalog());

        Assert.Equal(3, snapshot.MessageCount);
        Assert.Equal(1_500, snapshot.InputTokens);
        Assert.Equal(300, snapshot.OutputTokens);
        Assert.Equal(1_800, snapshot.TotalTokens);
    }

    [Fact]
    public void Create_estimates_cost_via_pricing_and_catalog()
    {
        var catalog = TestCatalog();
        var session = BuildSession();

        var snapshot = SessionExitSnapshot.Create(session, null, Start, Start.AddMinutes(1), catalog);

        var expected = Pricing.EstimateUsd(
            session.Model, session.SessionUsage, catalog.Get(session.ActiveProviderId, session.Model));
        Assert.Equal(expected, snapshot.EstimatedUsd);
        Assert.Equal(0.0168m, snapshot.EstimatedUsd);

        // Prove the catalog rates (7 / 21) — not the Sonnet fallback (3 / 15) — produced the cost.
        var fallback = Pricing.EstimateUsd(session.Model, session.SessionUsage, catalog: null);
        Assert.Equal(0.0090m, fallback);
        Assert.NotEqual(fallback, snapshot.EstimatedUsd);
    }

    [Fact]
    public void Create_projects_cached_context_exact()
    {
        var snapshot = SessionExitSnapshot.Create(
            BuildSession(), BuildContext(isExact: true), Start, Start.AddMinutes(1), TestCatalog());

        Assert.NotNull(snapshot.Context);
        Assert.Equal(12_000, snapshot.Context!.UsedTokens);
        Assert.Equal(200_000, snapshot.Context.MaxTokens);
        Assert.Equal(6, snapshot.Context.Percentage);
        Assert.True(snapshot.Context.IsExact);
    }

    [Fact]
    public void Create_projects_cached_context_estimated()
    {
        var snapshot = SessionExitSnapshot.Create(
            BuildSession(), BuildContext(isExact: false), Start, Start.AddMinutes(1), TestCatalog());

        Assert.NotNull(snapshot.Context);
        Assert.False(snapshot.Context!.IsExact);
    }

    [Fact]
    public void Create_without_context_report_has_no_context()
    {
        var snapshot = SessionExitSnapshot.Create(
            BuildSession(), null, Start, Start.AddMinutes(1), TestCatalog());

        Assert.Null(snapshot.Context);
    }

    [Fact]
    public void Create_without_session_id_is_not_persisted()
    {
        var session = BuildSession();
        session.SessionId = null;

        var snapshot = SessionExitSnapshot.Create(session, null, Start, Start.AddMinutes(1), TestCatalog());

        Assert.Null(snapshot.SessionId);
        Assert.False(snapshot.HasSession);
    }

    // ---- Rendering (ExitSummaryRenderer.Render) ----

    private static SessionExitSnapshot Snapshot(
        bool withSession = true, ContextReport? context = null, TimeSpan? duration = null)
    {
        var session = BuildSession();
        if (!withSession)
        {
            session.SessionId = null;
        }

        var end = Start + (duration ?? TimeSpan.FromSeconds(125));
        return SessionExitSnapshot.Create(session, context, Start, end, TestCatalog());
    }

    [Fact]
    public void Render_shows_logo_rows()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot());

        Assert.Contains(" ┌───┐      ┌┐", console.Output);
        Assert.Contains(" └───┘└──┘└──┘└───┘", console.Output);
    }

    [Fact]
    public void Render_shows_duration()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot(duration: TimeSpan.FromSeconds(125)));

        Assert.Contains("2m 05s", console.Output);
    }

    [Fact]
    public void Render_shows_provider_model_and_effort()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot());

        Assert.Contains("claude-ai", console.Output);
        Assert.Contains("test-model", console.Output);
        Assert.Contains("high", console.Output);
    }

    [Fact]
    public void Render_shows_message_token_totals_and_cost()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot());

        Assert.Contains("1,500", console.Output);
        Assert.Contains("300", console.Output);
        Assert.Contains("1,800", console.Output);
        Assert.Contains("$0.0168", console.Output);
    }

    [Fact]
    public void Render_shows_exact_context_usage()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot(context: BuildContext(isExact: true)));

        Assert.Contains("12,000", console.Output);
        Assert.Contains("200,000", console.Output);
        Assert.Contains("6%", console.Output);
        Assert.Contains("exact", console.Output);
    }

    [Fact]
    public void Render_shows_estimated_context_usage()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot(context: BuildContext(isExact: false)));

        Assert.Contains("estimated", console.Output);
    }

    [Fact]
    public void Render_without_context_says_not_measured()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot(context: null));

        Assert.Contains("Context: not measured", console.Output);
    }

    private static SessionExitSnapshot SnapshotWithCwd(string cwd)
    {
        var session = BuildSession();
        session.WorkingDirectory = cwd;
        return SessionExitSnapshot.Create(session, null, Start, Start.AddSeconds(125), TestCatalog());
    }

    private static SessionExitSnapshot SnapshotWithSessionId(string id)
    {
        var session = BuildSession();
        session.SessionId = id;
        return SessionExitSnapshot.Create(session, null, Start, Start.AddSeconds(125), TestCatalog());
    }

    private static string FindCommandLine(string output, string prefix)
    {
        foreach (var raw in output.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return line;
            }
        }

        throw new Xunit.Sdk.XunitException($"No line starting with '{prefix}' in output:\n{output}");
    }

    // Split a rendered command line into argv following the same double-quote convention
    // (Windows CommandLineToArgvW) that FormatCommandArgument targets, so the round-trip tests
    // exercise the exact tokens a shell would hand to Coda.
    private static List<string> Tokenize(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        var i = 0;

        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 0)
                    {
                        inQuotes = !inQuotes;
                    }
                    else
                    {
                        current.Append('"');
                    }

                    hasToken = true;
                    i++;
                }
                else
                {
                    current.Append('\\', backslashes);
                    hasToken = true;
                }
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                i++;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                i++;
            }
            else
            {
                current.Append(c);
                hasToken = true;
                i++;
            }
        }

        if (hasToken)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    [Fact]
    public void Render_with_session_id_shows_directory_step_and_valid_commands()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot(withSession: true));

        // A `cd` into the session directory, then plain Coda commands (no unsupported --cwd flag,
        // which the interactive launcher never consumes and would silently drop the resume intent).
        Assert.Contains("Resume from this directory:", console.Output);
        Assert.Contains("cd \"C:\\work\"", console.Output);
        Assert.Contains("coda --resume \"sess-123\"", console.Output);
        Assert.Contains("coda --continue", console.Output);
        Assert.DoesNotContain("--cwd", console.Output);
    }

    [Fact]
    public void Render_resume_command_round_trips_through_startup_intent()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot(withSession: true));

        var args = Tokenize(FindCommandLine(console.Output, "coda --resume"));
        Assert.Equal("coda", args[0]);
        Assert.DoesNotContain("--cwd", args);

        var intent = SessionCli.ParseStartupIntent(args.Skip(1).ToList());
        Assert.Equal("sess-123", intent.ResumeId);
        Assert.False(intent.ContinueLatest);
    }

    [Fact]
    public void Render_continue_command_round_trips_through_startup_intent()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot(withSession: true));

        var args = Tokenize(FindCommandLine(console.Output, "coda --continue"));
        Assert.Equal("coda", args[0]);
        Assert.DoesNotContain("--cwd", args);

        var intent = SessionCli.ParseStartupIntent(args.Skip(1).ToList());
        Assert.True(intent.ContinueLatest);
        Assert.Null(intent.ResumeId);
    }

    [Theory]
    [InlineData("sess-123")]
    [InlineData("id with spaces")]
    [InlineData("weird\"id\\with\\slashes")]
    public void Render_resume_id_round_trips_even_when_it_needs_escaping(string id)
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, SnapshotWithSessionId(id));

        var args = Tokenize(FindCommandLine(console.Output, "coda --resume"));
        var intent = SessionCli.ParseStartupIntent(args.Skip(1).ToList());
        Assert.Equal(id, intent.ResumeId);
    }

    [Fact]
    public void Render_root_cwd_argument_doubles_trailing_backslash()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, SnapshotWithCwd("C:\\"));

        // A root path must render as "C:\\" so the closing quote is not escaped and the argument
        // is copy-paste parseable; the buggy single-backslash form "C:\" must never appear.
        Assert.Contains("cd \"C:\\\\\"", console.Output);
        Assert.DoesNotContain("cd \"C:\\\"\r", console.Output);
        Assert.DoesNotContain("cd \"C:\\\"\n", console.Output);
    }

    [Fact]
    public void Render_trailing_separator_cwd_argument_doubles_trailing_backslash()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, SnapshotWithCwd("C:\\work\\"));

        Assert.Contains("cd \"C:\\work\\\\\"", console.Output);
    }

    [Fact]
    public void Render_cwd_argument_escapes_embedded_quote()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, SnapshotWithCwd("C:\\a\"b"));

        Assert.Contains("cd \"C:\\a\\\"b\"", console.Output);
    }

    [Fact]
    public void Render_shows_seconds_only_duration()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot(duration: TimeSpan.FromSeconds(45)));

        Assert.Contains("Duration: 45s", console.Output);
        Assert.DoesNotContain("m 45s", console.Output);
    }

    [Fact]
    public void Render_shows_hours_duration()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot(duration: new TimeSpan(1, 2, 3)));

        Assert.Contains("1h 02m 03s", console.Output);
    }

    [Fact]
    public void Render_without_session_id_omits_commands()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot(withSession: false));

        Assert.DoesNotContain("--resume", console.Output);
        Assert.DoesNotContain("--continue", console.Output);
        Assert.DoesNotContain("Resume from this directory:", console.Output);
        Assert.DoesNotContain("cd \"", console.Output);
    }

    [Fact]
    public void Render_without_session_id_says_not_saved()
    {
        var console = NewConsole();

        ExitSummaryRenderer.Render(console, Snapshot(withSession: false));

        Assert.Contains("not saved", console.Output);
    }
}
