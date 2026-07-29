using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Coda.Agent.Hooks;

/// <summary>
/// Parses a hook's stdout into a <see cref="HookOutput"/>.
/// </summary>
/// <remarks>
/// The protocol is permissive:
/// <list type="bullet">
///   <item>Empty / whitespace → <see cref="HookOutput.NoOp"/>.</item>
///   <item>Valid JSON object → fields populated; unknown properties ignored.</item>
///   <item>Non-JSON text or malformed JSON → text is treated as the <see cref="HookOutput.Reason"/>.</item>
/// </list>
/// Field names on the wire are camelCase; matching is case-insensitive.
/// </remarks>
public static class HookOutputParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // System.Text.Json ignores unknown JSON properties by default; no extra option needed.
    };

    /// <summary>Parses <paramref name="stdout"/> into a <see cref="HookOutput"/>.</summary>
    public static HookOutput Parse(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return HookOutput.NoOp;
        }

        var trimmed = stdout.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<HookOutputDto>(trimmed, JsonOptions);
                if (dto is not null)
                {
                    return new HookOutput
                    {
                        Continue = dto.Continue,
                        StopReason = dto.StopReason,
                        SystemMessage = dto.SystemMessage,
                        SuppressOutput = dto.SuppressOutput,
                        Decision = dto.Decision,
                        Reason = dto.Reason,
                        HookSpecificOutput = dto.HookSpecificOutput,
                    };
                }
            }
            catch (JsonException)
            {
                // Malformed JSON → fall through to plain-text treatment.
            }
        }

        // Plain text (or malformed JSON): treat the whole string as the reason.
        return new HookOutput { Reason = stdout };
    }

    /// <summary>Internal DTO used only for JSON deserialization.</summary>
    private sealed class HookOutputDto
    {
        [JsonPropertyName("continue")]
        public bool Continue { get; set; } = true;

        [JsonPropertyName("stopReason")]
        public string? StopReason { get; set; }

        [JsonPropertyName("systemMessage")]
        public string? SystemMessage { get; set; }

        [JsonPropertyName("suppressOutput")]
        public bool SuppressOutput { get; set; }

        [JsonPropertyName("decision")]
        public string? Decision { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("hookSpecificOutput")]
        public JsonObject? HookSpecificOutput { get; set; }
    }
}
