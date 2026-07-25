using System.Collections.Immutable;
using System.Diagnostics;
using Coda.Agent;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Input;
using Coda.Tui.Ui.Prompts;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.Shells;
using Coda.Tui.Ui.State;
using Point = System.Drawing.Point;

namespace Coda.Tui.Tests;

// ---------------------------------------------------------------------------
// Helpers shared across link-interaction tests
// ---------------------------------------------------------------------------

file sealed class StubUrlOpener : IUrlOpener
{
    public List<string> OpenedUrls { get; } = [];
    public List<(string Url, PrivateBrowserInfo Browser)> PrivateOpens { get; } = [];
    public bool ReturnSuccess { get; init; } = true;

    public bool TryOpen(string url, out string? error)
    {
        OpenedUrls.Add(url);
        error = ReturnSuccess ? null : "stub failure";
        return ReturnSuccess;
    }

    public bool TryOpenPrivate(string url, PrivateBrowserInfo browser, out string? error)
    {
        PrivateOpens.Add((url, browser));
        error = ReturnSuccess ? null : "stub failure";
        return ReturnSuccess;
    }
}

file sealed class StubPrivateBrowserResolver : IPrivateBrowserResolver
{
    private readonly PrivateBrowserInfo? result;

    public StubPrivateBrowserResolver(PrivateBrowserInfo? result) => this.result = result;

    public PrivateBrowserInfo? Resolve() => this.result;
}

file static class LinkShellFactory
{
    /// <summary>
    /// Creates a formatter that produces a single row whose column 5..24 contains
    /// an honest link to <c>https://example.com</c>. Row 0 in the index is the
    /// separator; row 1 is this content row.
    /// </summary>
    public static IReadOnlyList<TranscriptRenderLine> HonestLinkFormatter(TranscriptBlock _, int __)
    {
        var link = new LinkSpan(5, 24, "https://example.com", TextMatchesUrl: true);
        return [new TranscriptRenderLine("See: https://example.com", TranscriptRole.Assistant) { Links = [link] }];
    }

    /// <summary>
    /// Like <see cref="HonestLinkFormatter"/> but with TextMatchesUrl=false (deceptive link).
    /// The visible text "click here" occupies columns 0..10; the real URL is https://example.com.
    /// </summary>
    public static IReadOnlyList<TranscriptRenderLine> DeceptiveLinkFormatter(TranscriptBlock _, int __)
    {
        var link = new LinkSpan(0, 10, "https://example.com", TextMatchesUrl: false);
        return [new TranscriptRenderLine("click here", TranscriptRole.Assistant) { Links = [link] }];
    }

    /// <summary>A formatter producing a plain row with no links.</summary>
    public static IReadOnlyList<TranscriptRenderLine> NoLinkFormatter(TranscriptBlock _, int __) =>
        [new TranscriptRenderLine("plain text no links", TranscriptRole.Assistant)];

    /// <summary>The single dummy block used by all link tests (content doesn't matter — the formatter controls output).</summary>
    public static TranscriptBlock DummyBlock() =>
        new AssistantTranscriptBlock(Guid.NewGuid(), "dummy", Complete: true);

    /// <summary>Creates a shell wired with link seams, using the given formatter.</summary>
    public static (IApplication App, FullscreenTuiShell Shell) Create(
        Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>> formatter,
        IUrlOpener? urlOpener = null,
        IPrivateBrowserResolver? privateBrowserResolver = null,
        IUiPromptService? linkPromptService = null,
        Func<string, bool>? clipboardWriter = null)
    {
        IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(80, 24);

        var shell = new FullscreenTuiShell(
            app,
            new ComposerController(new SlashCommandCompletion(new SlashCommandRegistry([]))),
            new RecordingUiEvents(),
            UiSessionSnapshot.Empty,
            transcriptFormatter: formatter,
            urlOpener: urlOpener,
            privateBrowserResolver: privateBrowserResolver,
            linkPromptService: linkPromptService,
            clipboardWriter: clipboardWriter);

        var _ = app.Begin(shell);
        app.LayoutAndDraw();

        return (app, shell);
    }
}

// ---------------------------------------------------------------------------
// 1. TryGetLinkAt — hit / miss
// ---------------------------------------------------------------------------

public sealed class TryGetLinkAtTests
{
    [Fact]
    public void Returns_link_when_column_is_inside_span()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);

            // Row 0 is the content row (separator is the LAST row of each block = row 1).
            var found = shell.Transcript.TryGetLinkAt(0, 10, out var link);

            Assert.True(found);
            Assert.Equal("https://example.com", link.Url);
            Assert.True(link.TextMatchesUrl);
        }
    }

    [Fact]
    public void Returns_false_when_column_is_past_span_end()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);

            // EndColumn=24 is exclusive; column 24 must miss.
            var found = shell.Transcript.TryGetLinkAt(0, 24, out _);

            Assert.False(found);
        }
    }

    [Fact]
    public void Returns_false_when_column_is_before_span_start()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);

            // The link starts at column 5; column 4 must miss.
            var found = shell.Transcript.TryGetLinkAt(0, 4, out _);

            Assert.False(found);
        }
    }

    [Fact]
    public void Returns_false_on_separator_row()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);

            // Row 1 is the separator (TranscriptLayoutIndex adds the separator as the last row of each block).
            var found = shell.Transcript.TryGetLinkAt(1, 10, out _);

            Assert.False(found);
        }
    }

    [Fact]
    public void Returns_false_when_row_has_no_links()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.NoLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);

            var found = shell.Transcript.TryGetLinkAt(0, 5, out _);

            Assert.False(found);
        }
    }

    [Fact]
    public void Hit_at_start_column_exactly_inside_span()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);

            // StartColumn=5 is inclusive.
            var found = shell.Transcript.TryGetLinkAt(0, 5, out var link);

            Assert.True(found);
            Assert.Equal("https://example.com", link.Url);
        }
    }

    [Fact]
    public void Hit_at_end_column_minus_one_inside_span()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);

            // EndColumn=24 is exclusive; column 23 is the last valid hit.
            var found = shell.Transcript.TryGetLinkAt(0, 23, out var link);

            Assert.True(found);
            Assert.Equal("https://example.com", link.Url);
        }
    }
}

// ---------------------------------------------------------------------------
// 2. Left-click — event-level tests on VirtualizedTranscriptView
// ---------------------------------------------------------------------------

public sealed class TranscriptLinkMouseTests
{
    private static void ClickAt(VirtualizedTranscriptView view, int x, int y)
    {
        view.ProcessMouse(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(x, y) });
        view.ProcessMouse(new Mouse { Flags = MouseFlags.LeftButtonReleased, Position = new Point(x, y) });
    }

    private static void RightClickAt(VirtualizedTranscriptView view, int x, int y)
    {
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(x, y) });
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(x, y) });
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonClicked, Position = new Point(x, y) });
    }

    [Fact]
    public void Left_click_on_link_raises_LinkActivated_event()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            LinkSpan? activated = null;
            shell.Transcript.LinkActivated += link => activated = link;

            ClickAt(shell.Transcript, 10, 0); // row 0 in view = globalRow 1 (content)

            Assert.NotNull(activated);
            Assert.Equal("https://example.com", activated!.Value.Url);
        }
    }

    [Fact]
    public void Left_click_outside_link_span_does_not_raise_LinkActivated()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            var activated = false;
            shell.Transcript.LinkActivated += _ => activated = true;

            ClickAt(shell.Transcript, 0, 0); // col 0 is before the link [5,24)

            Assert.False(activated);
        }
    }

    [Fact]
    public void Right_click_on_link_raises_LinkContextMenuRequested_event()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            LinkSpan? menuLink = null;
            shell.Transcript.LinkContextMenuRequested += (link, _) => menuLink = link;

            RightClickAt(shell.Transcript, 10, 0);

            Assert.NotNull(menuLink);
            Assert.Equal("https://example.com", menuLink!.Value.Url);
        }
    }

    [Fact]
    public void Right_click_outside_link_span_does_not_raise_LinkContextMenuRequested()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            var raised = false;
            shell.Transcript.LinkContextMenuRequested += (_, _) => raised = true;

            RightClickAt(shell.Transcript, 0, 0); // col 0 is before the link

            Assert.False(raised);
        }
    }
}

// ---------------------------------------------------------------------------
// 3. Shell-level: honest left-click opens immediately via IUrlOpener
// ---------------------------------------------------------------------------

public sealed class LinkActivationShellTests
{
    private static void ClickAt(VirtualizedTranscriptView view, int x, int y)
    {
        view.ProcessMouse(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(x, y) });
        view.ProcessMouse(new Mouse { Flags = MouseFlags.LeftButtonReleased, Position = new Point(x, y) });
    }

    [Fact]
    public void Honest_link_left_click_opens_immediately_no_prompt()
    {
        var opener = new StubUrlOpener();
        var promptService = new RecordingPromptService();
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.HonestLinkFormatter,
            urlOpener: opener,
            linkPromptService: promptService);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            ClickAt(shell.Transcript, 10, 0); // hits the honest link at cols [5,24)

            Assert.Single(opener.OpenedUrls);
            Assert.Equal("https://example.com", opener.OpenedUrls[0]);
            Assert.Empty(promptService.Requests); // no confirmation prompt for honest links
        }
    }

    [Fact]
    public void Deceptive_link_left_click_shows_prompt_and_opens_on_confirm()
    {
        var opener = new StubUrlOpener();
        // Respond "yes" to the confirmation.
        var promptService = new RecordingPromptService(
            new UiPromptResponse(false, ["yes"], null));
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.DeceptiveLinkFormatter,
            urlOpener: opener,
            linkPromptService: promptService);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            // Click inside the deceptive span [0,10).
            ClickAt(shell.Transcript, 5, 0);

            // Wait briefly for the async confirm-and-open to complete (it's awaited via Task.FromResult
            // in RecordingPromptService so it completes synchronously, but we may need an app.Invoke pump).
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!opener.OpenedUrls.Any() && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }

            Assert.Single(promptService.Requests); // one confirmation shown
            Assert.Single(opener.OpenedUrls);
            Assert.Equal("https://example.com", opener.OpenedUrls[0]);
        }
    }

    [Fact]
    public void Deceptive_link_left_click_does_not_open_on_cancel()
    {
        var opener = new StubUrlOpener();
        // Respond "no" to the confirmation.
        var promptService = new RecordingPromptService(
            new UiPromptResponse(false, ["no"], null));
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.DeceptiveLinkFormatter,
            urlOpener: opener,
            linkPromptService: promptService);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            ClickAt(shell.Transcript, 5, 0);

            // Allow time for the async to complete.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!promptService.Requests.Any() && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }

            Assert.Single(promptService.Requests);
            Assert.Empty(opener.OpenedUrls); // not opened on "no"
        }
    }

    [Fact]
    public void Left_click_outside_link_span_does_not_open_anything()
    {
        var opener = new StubUrlOpener();
        var promptService = new RecordingPromptService();
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.HonestLinkFormatter,
            urlOpener: opener,
            linkPromptService: promptService);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            ClickAt(shell.Transcript, 0, 0); // col 0 is outside the link [5,24)

            Assert.Empty(opener.OpenedUrls);
            Assert.Empty(promptService.Requests);
        }
    }
}

// ---------------------------------------------------------------------------
// 4. Shell-level: right-click context menu
// ---------------------------------------------------------------------------

public sealed class LinkContextMenuShellTests
{
    private static void RightClickAt(VirtualizedTranscriptView view, int x, int y)
    {
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(x, y) });
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(x, y) });
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonClicked, Position = new Point(x, y) });
    }

    [Fact]
    public void Right_click_on_link_creates_popover_menu()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            RightClickAt(shell.Transcript, 10, 0);

            Assert.NotNull(shell.TranscriptLinkMenuForTest);
            Assert.NotNull(shell.TranscriptLinkMenuItemsForTest);
        }
    }

    [Fact]
    public void Right_click_outside_link_does_not_create_menu()
    {
        var (app, shell) = LinkShellFactory.Create(LinkShellFactory.HonestLinkFormatter);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            RightClickAt(shell.Transcript, 0, 0); // outside the link span

            Assert.Null(shell.TranscriptLinkMenuForTest);
        }
    }

    [Fact]
    public void Context_menu_Copy_link_writes_url_to_clipboard()
    {
        string? copied = null;
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.HonestLinkFormatter,
            clipboardWriter: text => { copied = text; return true; });
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            RightClickAt(shell.Transcript, 10, 0);

            var items = shell.TranscriptLinkMenuItemsForTest;
            Assert.NotNull(items);

            // Find the "Copy link" item and invoke it.
            var copyItem = items!.FirstOrDefault(m => m.Title.Contains("Copy link", StringComparison.Ordinal));
            Assert.NotNull(copyItem);
            copyItem!.Action?.Invoke();

            Assert.Equal("https://example.com", copied);
        }
    }

    [Fact]
    public void Context_menu_Open_calls_url_opener()
    {
        var opener = new StubUrlOpener();
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.HonestLinkFormatter,
            urlOpener: opener);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            RightClickAt(shell.Transcript, 10, 0);

            var items = shell.TranscriptLinkMenuItemsForTest;
            Assert.NotNull(items);

            var openItem = items!.FirstOrDefault(m =>
                m.Title.Contains("Open", StringComparison.Ordinal) &&
                !m.Title.Contains("private", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(openItem);
            openItem!.Action?.Invoke();

            Assert.Contains("https://example.com", opener.OpenedUrls);
        }
    }

    [Fact]
    public void Open_in_private_window_hidden_when_resolver_returns_null()
    {
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.HonestLinkFormatter,
            privateBrowserResolver: new StubPrivateBrowserResolver(null));
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            RightClickAt(shell.Transcript, 10, 0);

            var items = shell.TranscriptLinkMenuItemsForTest;
            Assert.NotNull(items);

            var privateItem = items!.FirstOrDefault(m =>
                m.Title.Contains("private", StringComparison.OrdinalIgnoreCase));
            Assert.Null(privateItem);
        }
    }

    [Fact]
    public void Open_in_private_window_shown_when_resolver_returns_browser()
    {
        var browser = new PrivateBrowserInfo(@"C:\browsers\chrome.exe", "--incognito");
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.HonestLinkFormatter,
            privateBrowserResolver: new StubPrivateBrowserResolver(browser));
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            RightClickAt(shell.Transcript, 10, 0);

            var items = shell.TranscriptLinkMenuItemsForTest;
            Assert.NotNull(items);

            var privateItem = items!.FirstOrDefault(m =>
                m.Title.Contains("private", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(privateItem);
        }
    }

    [Fact]
    public void Open_in_private_window_passes_url_as_separate_arg()
    {
        ProcessStartInfo? captured = null;
        var browser = new PrivateBrowserInfo(@"C:\browsers\chrome.exe", "--incognito");
        var opener = new DefaultUrlOpener { ProcessStarterOverride = psi => { captured = psi; return true; } };
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.HonestLinkFormatter,
            urlOpener: opener,
            privateBrowserResolver: new StubPrivateBrowserResolver(browser));
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            RightClickAt(shell.Transcript, 10, 0);

            var items = shell.TranscriptLinkMenuItemsForTest;
            Assert.NotNull(items);

            var privateItem = items!.FirstOrDefault(m =>
                m.Title.Contains("private", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(privateItem);
            privateItem!.Action?.Invoke();

            Assert.NotNull(captured);
            // UseShellExecute must be false when using an explicit exe path with ArgumentList.
            Assert.False(captured!.UseShellExecute);
            // URL must appear as a separate entry in ArgumentList, never in Arguments.
            Assert.Contains("https://example.com", captured.ArgumentList);
            // The private flag must be a separate entry too (no shell interpolation).
            Assert.Contains("--incognito", captured.ArgumentList);
            // The raw Arguments string must be empty (not shell-interpolated).
            Assert.Equal(string.Empty, captured.Arguments);
        }
    }
}

// ---------------------------------------------------------------------------
// 5. IUrlOpener validation — DefaultUrlOpener
// ---------------------------------------------------------------------------

public sealed class UrlOpenerTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/path?q=1#anchor")]
    public void ValidateHttpScheme_accepts_http_and_https(string url)
    {
        var result = DefaultUrlOpener.ValidateHttpScheme(url, out var error);

        Assert.True(result);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void ValidateHttpScheme_rejects_non_http_schemes_and_malformed(string url)
    {
        var result = DefaultUrlOpener.ValidateHttpScheme(url, out var error);

        Assert.False(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryOpen_rejects_non_https_url_without_launching_process()
    {
        ProcessStartInfo? captured = null;
        var opener = new DefaultUrlOpener
        {
            ProcessStarterOverride = psi => { captured = psi; return true; }
        };

        var result = opener.TryOpen("ftp://example.com", out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Null(captured); // process was never launched
    }

    [Fact]
    public void TryOpen_uses_shell_execute_true_for_valid_url()
    {
        ProcessStartInfo? captured = null;
        var opener = new DefaultUrlOpener
        {
            ProcessStarterOverride = psi => { captured = psi; return true; }
        };

        var result = opener.TryOpen("https://example.com", out _);

        Assert.True(result);
        Assert.NotNull(captured);
        Assert.True(captured!.UseShellExecute);
        Assert.Equal("https://example.com", captured.FileName);
    }

    [Fact]
    public void TryOpenPrivate_passes_flag_and_url_as_separate_arguments()
    {
        ProcessStartInfo? captured = null;
        var opener = new DefaultUrlOpener
        {
            ProcessStarterOverride = psi => { captured = psi; return true; }
        };
        var browser = new PrivateBrowserInfo(@"C:\browsers\chrome.exe", "--incognito");

        var result = opener.TryOpenPrivate("https://example.com", browser, out _);

        Assert.True(result);
        Assert.NotNull(captured);
        Assert.False(captured!.UseShellExecute);  // explicit exe, not shell
        Assert.Equal(@"C:\browsers\chrome.exe", captured.FileName);
        Assert.Contains("--incognito", captured.ArgumentList);
        Assert.Contains("https://example.com", captured.ArgumentList);
        // Confirm no shell interpolation: Arguments must be empty, not a string.
        Assert.Equal(string.Empty, captured.Arguments);
    }

    [Fact]
    public void TryOpenPrivate_rejects_non_https_without_launching()
    {
        ProcessStartInfo? captured = null;
        var opener = new DefaultUrlOpener
        {
            ProcessStarterOverride = psi => { captured = psi; return true; }
        };
        var browser = new PrivateBrowserInfo(@"C:\browsers\chrome.exe", "--incognito");

        var result = opener.TryOpenPrivate("javascript:evil()", browser, out var error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Null(captured); // process was never launched
    }

    [Fact]
    public void TryOpen_returns_false_and_error_when_process_start_fails()
    {
        var opener = new DefaultUrlOpener
        {
            ProcessStarterOverride = _ => false
        };

        var result = opener.TryOpen("https://example.com", out var error);

        Assert.False(result);
        Assert.NotNull(error);
    }
}

// ---------------------------------------------------------------------------
// 6. Copy link writes URL + status text
// ---------------------------------------------------------------------------

public sealed class CopyLinkShellTests
{
    private static void RightClickAt(VirtualizedTranscriptView view, int x, int y)
    {
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(x, y) });
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(x, y) });
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonClicked, Position = new Point(x, y) });
    }

    [Fact]
    public void Copy_link_writes_url_to_clipboard_writer_and_shows_copied_status()
    {
        string? copied = null;
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.HonestLinkFormatter,
            clipboardWriter: text => { copied = text; return true; });
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            RightClickAt(shell.Transcript, 10, 0);

            var items = shell.TranscriptLinkMenuItemsForTest;
            Assert.NotNull(items);

            items!.First(m => m.Title.Contains("Copy link", StringComparison.Ordinal))
                .Action?.Invoke();

            Assert.Equal("https://example.com", copied);
            // The status row should reflect a successful copy.
            Assert.Contains("copied", shell.Operational.Status.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Copy_link_shows_unavailable_status_when_clipboard_fails()
    {
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.HonestLinkFormatter,
            clipboardWriter: _ => false,
            urlOpener: new StubUrlOpener());
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            RightClickAt(shell.Transcript, 10, 0);

            var items = shell.TranscriptLinkMenuItemsForTest;
            Assert.NotNull(items);

            items!.First(m => m.Title.Contains("Copy link", StringComparison.Ordinal))
                .Action?.Invoke();

            Assert.Contains("Clipboard unavailable", shell.Operational.Status.Text, StringComparison.OrdinalIgnoreCase);
        }
    }
}

// ---------------------------------------------------------------------------
// 7. Security hardening: userinfo forces confirm; host always visible in display
// ---------------------------------------------------------------------------

file sealed class ThrowingPromptService : IUiPromptService
{
    public bool IsInteractive => true;

    public Task<UiPromptResponse> RequestAsync(UiPromptRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Simulated unexpected failure from prompt service");
}

public sealed class LinkSecurityTests
{
    private static void ClickAt(VirtualizedTranscriptView view, int x, int y)
    {
        view.ProcessMouse(new Mouse { Flags = MouseFlags.LeftButtonPressed, Position = new Point(x, y) });
        view.ProcessMouse(new Mouse { Flags = MouseFlags.LeftButtonReleased, Position = new Point(x, y) });
    }

    private static void RightClickAt(VirtualizedTranscriptView view, int x, int y)
    {
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonPressed, Position = new Point(x, y) });
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonReleased, Position = new Point(x, y) });
        view.ProcessMouse(new Mouse { Flags = MouseFlags.RightButtonClicked, Position = new Point(x, y) });
    }

    /// <summary>Creates a formatter that returns one row with the given LinkSpan covering col 0..url.Length.</summary>
    private static Func<TranscriptBlock, int, IReadOnlyList<TranscriptRenderLine>> FormatterWithSpan(
        string url, bool textMatchesUrl)
    {
        return (_, __) =>
        {
            var link = new LinkSpan(0, url.Length, url, textMatchesUrl);
            return [new TranscriptRenderLine(url, TranscriptRole.Assistant) { Links = [link] }];
        };
    }

    // Fix 2 — Part B: userinfo URL forces confirmation even when TextMatchesUrl=true
    [Fact]
    public void Userinfo_url_forces_confirm_even_when_TextMatchesUrl_is_true()
    {
        const string userinfoUrl = "https://user@evil.com/";
        var opener = new StubUrlOpener();
        var promptService = new RecordingPromptService(new UiPromptResponse(false, ["yes"], null));

        // TextMatchesUrl=true but the URL has userinfo → must still show confirm.
        var (app, shell) = LinkShellFactory.Create(
            FormatterWithSpan(userinfoUrl, textMatchesUrl: true),
            urlOpener: opener,
            linkPromptService: promptService);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            ClickAt(shell.Transcript, 5, 0);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!promptService.Requests.Any() && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }

            Assert.Single(promptService.Requests); // confirm was shown
            Assert.Single(opener.OpenedUrls);       // opened after "yes"
            Assert.Equal(userinfoUrl, opener.OpenedUrls[0]);
        }
    }

    // Fix 2 — Part A: confirm title always shows real host, not elided by front truncation
    [Fact]
    public void Confirm_title_always_shows_real_host_not_elided_by_truncation()
    {
        // Crafted URL: long-enough userinfo to push the real host past the 60-char front truncation.
        // "https://www.paypal.com.aaa...@evil.com/" — old code would hide "evil.com" in the "…".
        var userinfo = new string('a', 50);
        var craftedUrl = $"https://www.paypal.com.{userinfo}@evil.com/path";

        var promptService = new RecordingPromptService(new UiPromptResponse(true, [], null)); // cancelled

        var (app, shell) = LinkShellFactory.Create(
            FormatterWithSpan(craftedUrl, textMatchesUrl: false),
            linkPromptService: promptService);
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            ClickAt(shell.Transcript, 5, 0);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!promptService.Requests.Any() && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }

            Assert.Single(promptService.Requests);
            var title = promptService.Requests[0].Title;
            // The real destination host "evil.com" must be visible in the confirm title.
            Assert.Contains("evil.com", title, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Fix 2 — Part A: right-click menu header always shows real host
    [Fact]
    public void Menu_header_always_shows_real_host_not_elided_by_truncation()
    {
        var userinfo = new string('a', 50);
        var craftedUrl = $"https://www.paypal.com.{userinfo}@evil.com/path";

        var (app, shell) = LinkShellFactory.Create(
            FormatterWithSpan(craftedUrl, textMatchesUrl: false));
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            RightClickAt(shell.Transcript, 5, 0);

            var items = shell.TranscriptLinkMenuItemsForTest;
            Assert.NotNull(items);

            // The first item is the disabled URL header.
            var header = items![0].Title;
            Assert.Contains("evil.com", header, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Fix 3: ConfirmAndOpenLinkAsync swallows unexpected exceptions and shows a status notice
    [Fact]
    public void ConfirmAndOpenLinkAsync_swallows_unexpected_exception_and_shows_status()
    {
        var opener = new StubUrlOpener();
        var (app, shell) = LinkShellFactory.Create(
            LinkShellFactory.DeceptiveLinkFormatter,
            urlOpener: opener,
            linkPromptService: new ThrowingPromptService());
        using (app) using (shell)
        {
            shell.Transcript.ReplaceAll([LinkShellFactory.DummyBlock()]);
            app.Mouse.IsMouseDisabled = false;

            // Click the deceptive link to trigger ConfirmAndOpenLinkAsync.
            ClickAt(shell.Transcript, 5, 0);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!shell.Operational.Status.Text.Contains("Failed", StringComparison.OrdinalIgnoreCase) &&
                   DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }

            // URL must NOT have been opened.
            Assert.Empty(opener.OpenedUrls);
            // A transient failure notice must be visible.
            Assert.Contains("Failed", shell.Operational.Status.Text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
