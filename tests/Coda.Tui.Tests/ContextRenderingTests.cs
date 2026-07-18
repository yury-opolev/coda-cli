using Coda.Sdk;
using Coda.Tui.Commands;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Rendering;
using Coda.Tui.Ui.State;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using Spectre.Console;

namespace Coda.Tui.Tests;

/// <summary>
/// Regression coverage for the semantic <c>/context</c> render path: the offscreen
/// <see cref="UiAnsiConsoleAdapter"/> must not leave a terminal newline on each <c>MarkupLine</c> row
/// (which the <see cref="TranscriptBlockFormatter"/> would otherwise split into a trailing blank row),
/// and <see cref="ContextCommand"/> must map every context category to a distinct, glyph-legible marker
/// so the grid and legend stay readable once Spectre colors are stripped by the semantic adapter.
/// </summary>
public sealed class ContextRenderingTests
{
    private static readonly string[] CategoryNames =
    [
        "System prompt", "System tools", "MCP tools", "Messages", "Autocompact buffer", "Free space",
    ];

    [Fact]
    public void Adapter_row_formats_to_a_single_line_with_no_trailing_blank_row()
    {
        var events = new List<UiEvent>();
        var console = new UiAnsiConsoleAdapter(new CollectingPublisher(events), 80, 24);

        console.MarkupLine("grid row");

        var output = Assert.IsType<CommandOutputEvent>(Assert.Single(events));
        var block = new CommandOutputTranscriptBlock(Guid.NewGuid(), output.Text);
        var lines = TranscriptBlockFormatter.Format(block, 80);

        var line = Assert.Single(lines);
        Assert.Equal("grid row", line.Text);
    }

    [Fact]
    public void Context_category_glyphs_cover_all_six_categories_and_are_distinct()
    {
        var glyphs = ContextCommand.CategoryGlyphs;

        Assert.Equal(CategoryNames.Length, glyphs.Count);
        foreach (var name in CategoryNames)
        {
            Assert.True(glyphs.ContainsKey(name), $"missing glyph for category '{name}'");
        }

        Assert.Equal(glyphs.Count, glyphs.Values.Distinct().Count());
    }

    [Fact]
    public async Task Context_grid_renders_ten_rows_with_no_blank_rows_between_events()
    {
        var report = new ContextReport
        {
            Model = "glyph-model",
            MaxTokens = 200_000,
            Categories =
            [
                new ContextCategory("System prompt", 20_000),
                new ContextCategory("System tools", 20_000),
                new ContextCategory("MCP tools", 20_000),
                new ContextCategory("Messages", 20_000),
                new ContextCategory("Autocompact buffer", 20_000),
                new ContextCategory("Free space", 100_000),
            ],
            UsedTokens = 100_000,
            IsExact = true,
            MessageCount = 4,
        };

        var events = new List<UiEvent>();
        var console = new UiAnsiConsoleAdapter(new CollectingPublisher(events), 80, 24);
        var context = BuildContext(console);
        context.ContextSnapshots = new ContextSnapshotCache(_ => Task.FromResult(report));

        await new ContextCommand().ExecuteAsync(context, Array.Empty<string>(), CancellationToken.None);

        // Fold every published command-output event through the reducer and format each block: no rendered
        // line may be blank, so the grid and legend never show a blank row between semantic events.
        var snapshot = events.Aggregate(UiSessionSnapshot.Empty, UiReducer.Reduce);
        var lines = snapshot.Transcript
            .SelectMany(block => TranscriptBlockFormatter.Format(block, 80))
            .ToList();

        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, line => string.IsNullOrWhiteSpace(line.Text));

        var glyphSet = ContextCommand.CategoryGlyphs.Values.ToHashSet();
        var gridRows = lines.Count(line => IsGridRow(line.Text, glyphSet));
        Assert.Equal(10, gridRows);
    }

    private static bool IsGridRow(string text, IReadOnlySet<char> glyphs)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        foreach (var token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length != 1 || !glyphs.Contains(token[0]))
            {
                return false;
            }
        }

        return true;
    }

    private static CommandContext BuildContext(UiAnsiConsoleAdapter console)
    {
        var store = new InMemoryTokenStore();
        var credentials = new CredentialManager(
            store,
            new ICredentialProvider[] { new ClaudeAiProvider(), new GitHubCopilotProvider(), new ApiKeyProvider() });
        var providers = new List<ProviderDescriptor>
        {
            new("claude-ai", "Claude.ai", LoginKind.OAuthLoopback, "claude-sonnet-4-6"),
        };
        var session = new SessionState("claude-ai");
        var registry = new SlashCommandRegistry(Array.Empty<ISlashCommand>());
        return new CommandContext(console, credentials, session, providers, registry, semanticUiEnabled: true);
    }

    private sealed class CollectingPublisher(List<UiEvent> events) : IUiEventPublisher
    {
        public void Publish(UiEvent uiEvent) => events.Add(uiEvent);
    }
}
