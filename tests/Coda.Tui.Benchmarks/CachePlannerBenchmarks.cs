using BenchmarkDotNet.Attributes;
using LlmClient;

namespace Coda.Tui.Benchmarks;

/// <summary>
/// Cache hit-rate regression benchmark for <see cref="PromptCachePlanner"/>.
/// Drives the planner over a scripted, growing tool-loop conversation representative
/// of a real agent run (user → assistant + tool calls → tool results → repeat) and
/// asserts that the fraction of turns with an anchor breakpoint (i.e. a planned
/// cache read) does not fall below a recorded baseline.
/// </summary>
/// <remarks>
/// Fully offline and deterministic: no real API calls.  Large system and tool
/// definitions are pre-computed in <see cref="GlobalSetup"/> so they comfortably
/// exceed the per-model minimum prefix required for a valid cache plan.
/// </remarks>
[MemoryDiagnoser]
public class CachePlannerBenchmarks
{
    // Minimum fraction of turns (from turn 2 onward) that must have an anchor breakpoint.
    // Turn 1 never has an anchor (only one user message), so it is excluded from the ratio.
    private const double HitRateBaseline = 0.80;

    /// <summary>Number of simulated agent turns (user + assistant + optional tool cycle).</summary>
    [Params(10, 50)]
    public int TurnCount { get; set; }

    private string model = "claude-sonnet-4-6";
    private string system = string.Empty;
    private List<ToolDefinition> tools = [];

    [GlobalSetup]
    public void Setup()
    {
        this.model = "claude-sonnet-4-6";

        // Build a large-enough system prompt to exceed the per-model cache minimum (~1 000 tokens).
        this.system = new string('S', 6_000);

        // Create a stable set of tool definitions (non-volatile across turns).
        this.tools =
        [
            new ToolDefinition("read_file",   "Read a file",   new string('T', 200)),
            new ToolDefinition("write_file",  "Write a file",  new string('T', 200)),
            new ToolDefinition("list_dir",    "List directory", new string('T', 200)),
            new ToolDefinition("run_command", "Run shell command", new string('T', 300)),
        ];
    }

    /// <summary>
    /// Measures planner throughput and validates the hit-rate baseline.
    /// Returns the number of turns that received an anchor breakpoint.
    /// </summary>
    [Benchmark(Description = "PromptCachePlanner over scripted tool loop — hit-rate must be ≥80 %")]
    public int RunToolLoopAndCountHits()
    {
        var history = new List<ChatMessage>(this.TurnCount * 4);
        var hits = 0;

        for (var turn = 0; turn < this.TurnCount; turn++)
        {
            // User turn.
            history.Add(ChatMessage.UserText($"turn {turn}: do some work"));

            var plan = PromptCachePlanner.Plan(
                this.model,
                this.system,
                this.tools,
                history,
                toolsVolatile: false);

            // A hit is any turn (after the first) where the planner set an anchor breakpoint.
            if (turn > 0 && plan.AnchorMessageIndex >= 0)
            {
                hits++;
            }

            // Assistant reply.
            history.Add(new ChatMessage(ChatRole.Assistant, [new TextBlock($"thinking about turn {turn}")]));

            // Simulate a tool call + result pair every other turn.
            if (turn % 2 == 0)
            {
                history.Add(ChatMessage.UserText($"[tool_result for turn {turn}]: output data"));
            }
        }

        // Baseline assertion: callers reading benchmark output can treat a failing run as a
        // regression.  The return value lets BenchmarkDotNet track the hit count over time.
        var eligibleTurns = this.TurnCount - 1; // exclude turn 0
        if (eligibleTurns > 0)
        {
            var hitRate = (double)hits / eligibleTurns;
            if (hitRate < HitRateBaseline)
            {
                throw new InvalidOperationException(
                    $"Cache hit-rate regression: {hitRate:P1} < baseline {HitRateBaseline:P0} " +
                    $"({hits}/{eligibleTurns} turns with anchor)");
            }
        }

        return hits;
    }
}
