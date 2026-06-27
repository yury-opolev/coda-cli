using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>Waits for a specified number of milliseconds. Interruptible via cancellation.</summary>
public sealed class SleepTool : ITool
{
    private const int MaxDurationMs = 60_000;

    public string Name => "sleep";

    public string Description => "Wait for a number of milliseconds before continuing. Use when you need to wait for an external event. Interruptible.";

    public string InputSchemaJson => """
        {"type":"object","properties":{"duration_ms":{"type":"integer","description":"Number of milliseconds to wait (clamped to [0, 60000])"}},"required":["duration_ms"]}
        """;

    public bool IsReadOnly => true;

    /// <summary>Clamps a raw duration to the allowed [0, 60000] range.</summary>
    public static int ClampDuration(int ms) => Math.Clamp(ms, 0, MaxDurationMs);

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("duration_ms", out var durationElement)
            || durationElement.ValueKind != JsonValueKind.Number
            || !durationElement.TryGetInt32(out var rawMs))
        {
            return new ToolResult("Missing or invalid required parameter 'duration_ms' (must be an integer).", IsError: true);
        }

        var clamped = ClampDuration(rawMs);
        await Task.Delay(clamped, cancellationToken).ConfigureAwait(false);
        return new ToolResult($"Waited {clamped} ms.");
    }
}
