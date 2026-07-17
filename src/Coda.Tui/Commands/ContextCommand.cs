using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Shows how the model's context window is being used, broken down by category
/// with a grid visualization. Mirrors the reference client's <c>/context</c>.
/// </summary>
public sealed class ContextCommand : ISlashCommand
{
    private const int GridWidth = 10;
    private const int GridHeight = 10;
    private const int TotalSquares = GridWidth * GridHeight;

    // Distinct color AND glyph per category, so cells are distinguishable by shape
    // even when colors are hard to tell apart: used categories are a solid block in a
    // bright hue, the reserved buffer a medium shade, free space a light shade.
    private static readonly IReadOnlyDictionary<string, (string Color, char Glyph)> Styles =
        new Dictionary<string, (string, char)>(StringComparer.Ordinal)
        {
            ["System prompt"] = ("blue", '█'),
            ["System tools"] = ("yellow", '█'),
            ["MCP tools"] = ("aqua", '█'),
            ["Messages"] = ("magenta", '█'),
            ["Autocompact buffer"] = ("grey", '▒'),
            ["Free space"] = ("grey23", '░'),
        };

    private static (string Color, char Glyph) StyleFor(string categoryName) =>
        Styles.TryGetValue(categoryName, out var style) ? style : ("white", '█');

    public string Name => "context";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Show context-window usage for the current conversation";

    public CommandHelp Help => new(
        "/context",
        Description: "Displays a 10×10 grid and legend showing how the active model's context window is consumed, broken down by category: system prompt, system tools, MCP tools, messages, autocompact buffer, and free space. Token counts are exact when the provider supports counting; otherwise estimated.",
        Examples: ["/context"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        // Prefer the turn-scoped snapshot cache (forced fresh) so /context reflects the same analysis
        // the semantic UI uses; fall back to a one-shot analysis when no cache is wired (tests/legacy).
        var report = context.ContextSnapshots is { } cache
            ? await cache.GetAsync(force: true, cancellationToken).ConfigureAwait(false)
            : await AnalyzeOnceAsync(context, cancellationToken).ConfigureAwait(false);

        this.Render(context, report);
        return CommandResult.Continue;
    }

    private static async Task<ContextReport> AnalyzeOnceAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var options = new SessionOptions
        {
            ProviderId = context.Session.ActiveProviderId,
            Model = context.Session.Model,
            WorkingDirectory = context.Session.WorkingDirectory,
            OutputStyle = context.Session.OutputStyle,
            ExtraTools = context.ExtraTools,
        };

        using var session = new CodaSession(context.Credentials, options, history: context.Session.History);
        return await session.AnalyzeContextAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Render(CommandContext context, ContextReport report)
    {
        var console = context.Console;
        console.MarkupLine($"[bold]Context Usage[/] {Theme.DimMarkup($"· {report.Model}")}");

        var approx = report.IsExact ? string.Empty : "~";
        var header = $"{approx}{report.UsedTokens:N0} / {report.MaxTokens:N0} tokens ({report.Percentage}%) across {report.MessageCount} {(report.MessageCount == 1 ? "message" : "messages")}";
        console.MarkupLine(Theme.DimMarkup(header));
        if (!report.IsExact)
        {
            console.MarkupLine(Theme.DimMarkup("(estimated — provider has no token-counting API or it was unavailable)"));
        }

        console.WriteLine();
        this.RenderGrid(console, report);
        console.WriteLine();
        this.RenderLegend(console, report, approx);
    }

    private void RenderGrid(IAnsiConsole console, ContextReport report)
    {
        // Assign each square a category, proportional to its token share; free space
        // fills the remainder.
        var cellCategories = new List<string>(TotalSquares);
        foreach (var category in report.Categories)
        {
            if (category.Name == "Free space")
            {
                continue;
            }

            var count = (int)Math.Round(category.Tokens * (double)TotalSquares / report.MaxTokens);
            if (count == 0 && category.Tokens > 0)
            {
                count = 1;
            }

            for (var i = 0; i < count && cellCategories.Count < TotalSquares; i++)
            {
                cellCategories.Add(category.Name);
            }
        }

        while (cellCategories.Count < TotalSquares)
        {
            cellCategories.Add("Free space");
        }

        for (var row = 0; row < GridHeight; row++)
        {
            var cells = cellCategories.Skip(row * GridWidth).Take(GridWidth).Select(Cell);
            console.MarkupLine("  " + string.Join(" ", cells));
        }
    }

    private void RenderLegend(IAnsiConsole console, ContextReport report, string approx)
    {
        foreach (var category in report.Categories)
        {
            var pct = report.MaxTokens <= 0 ? 0 : (int)Math.Round(category.Tokens * 100.0 / report.MaxTokens);
            var label = Markup.Escape(category.Name);
            console.MarkupLine($"  {Cell(category.Name)} {label} {Theme.DimMarkup($"{approx}{category.Tokens:N0} tokens ({pct}%)")}");
        }
    }

    private static string Cell(string categoryName)
    {
        var (color, glyph) = StyleFor(categoryName);
        return $"[{color}]{glyph}[/]";
    }
}
