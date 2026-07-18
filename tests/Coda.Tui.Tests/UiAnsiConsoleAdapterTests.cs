using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Rendering;
using Spectre.Console;

namespace Coda.Tui.Tests;

public sealed class UiAnsiConsoleAdapterTests
{
    [Fact]
    public void Spectre_renderables_become_plain_typed_command_output()
    {
        var events = new List<UiEvent>();
        var console = new UiAnsiConsoleAdapter(new CollectingPublisher(events), 80, 24);

        console.Write(new Panel(new Markup("[red]hello[/]")));

        var output = Assert.IsType<CommandOutputEvent>(Assert.Single(events));
        Assert.Contains("hello", output.Text);
        Assert.DoesNotContain("\u001b[", output.Text);
    }

    [Fact]
    public void Clear_publishes_semantic_clear_instead_of_escape_sequences()
    {
        var events = new List<UiEvent>();
        var console = new UiAnsiConsoleAdapter(new CollectingPublisher(events), 80, 24);

        console.Clear(home: true);

        Assert.IsType<ConsoleClearRequestedEvent>(Assert.Single(events));
    }

    [Fact]
    public void Sequential_writes_drain_independently()
    {
        var events = new List<UiEvent>();
        var console = new UiAnsiConsoleAdapter(new CollectingPublisher(events), 80, 24);

        console.Write(new Markup("alpha"));
        console.Write(new Markup("beta"));

        Assert.Equal(2, events.Count);
        var first = Assert.IsType<CommandOutputEvent>(events[0]);
        var second = Assert.IsType<CommandOutputEvent>(events[1]);
        Assert.Contains("alpha", first.Text);
        Assert.DoesNotContain("beta", first.Text);
        Assert.Contains("beta", second.Text);
        Assert.DoesNotContain("alpha", second.Text);
    }

    [Fact]
    public void Interior_whitespace_and_newlines_are_preserved_and_normalized()
    {
        var events = new List<UiEvent>();
        var console = new UiAnsiConsoleAdapter(new CollectingPublisher(events), 80, 24);

        console.Write(new Text("a b\nc d"));

        var output = Assert.IsType<CommandOutputEvent>(Assert.Single(events));
        Assert.Contains("a b", output.Text);
        Assert.Contains("c d", output.Text);
        Assert.Contains(Environment.NewLine, output.Text);
        Assert.True(
            output.Text.IndexOf("a b", StringComparison.Ordinal) <
            output.Text.IndexOf("c d", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', output.Text);
    }

    [Fact]
    public void Truly_empty_render_publishes_no_event()
    {
        var events = new List<UiEvent>();
        var console = new UiAnsiConsoleAdapter(new CollectingPublisher(events), 80, 24);

        console.Write(new Text(string.Empty));

        Assert.Empty(events);
    }

    [Fact]
    public void Delegated_members_expose_the_inner_offscreen_console()
    {
        var console = new UiAnsiConsoleAdapter(new CollectingPublisher(new List<UiEvent>()), 80, 24);

        Assert.NotNull(console.Profile);
        Assert.NotNull(console.Cursor);
        Assert.NotNull(console.Input);
        Assert.NotNull(console.ExclusivityMode);
        Assert.NotNull(console.Pipeline);
        Assert.Equal(80, console.Profile.Width);
        Assert.False(console.Profile.Out.IsTerminal);
    }

    [Fact]
    public void Constructor_validates_its_arguments()
    {
        Assert.Throws<ArgumentNullException>(
            () => new UiAnsiConsoleAdapter(null!, 80, 24));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new UiAnsiConsoleAdapter(new CollectingPublisher(new List<UiEvent>()), 0, 24));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new UiAnsiConsoleAdapter(new CollectingPublisher(new List<UiEvent>()), 80, 0));
    }

    private sealed class CollectingPublisher(List<UiEvent> events) : IUiEventPublisher
    {
        public void Publish(UiEvent uiEvent) => events.Add(uiEvent);
    }
}
