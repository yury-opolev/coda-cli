using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coda.Agent;
using Coda.Common;

namespace Coda.Tui.Ui.Rendering;

/// <summary>Builds bounded, terminal-safe descriptions for correlated tool activity.</summary>
internal static class ToolActivityPreview
{
    public static string Create(string? toolName, string? inputJson)
    {
        var safeToolName = TruncateToCells(
            TerminalTextSanitizer.SanitizeSingleLine(toolName),
            ToolDisplayModeResolver.CompactInputPreviewMax);
        if (safeToolName.Length == 0)
        {
            safeToolName = "tool";
        }

        var redacted = SecretRedactor.RedactJson(inputJson ?? string.Empty);
        var root = Parse(redacted);
        RedactAdditionalSecrets(root);
        var objectRoot = root as JsonObject;
        var argumentPreview = root is null
            ? "[invalid arguments]"
            : ToolDisplayModeText.ArgumentPreview(SanitizeFreeText(root.ToJsonString()));

        var preview = safeToolName switch
        {
            "run_command" => "$ " + Field(objectRoot, "command", safeToolName),
            "read_file" => "Reading " + Field(objectRoot, "path", safeToolName),
            "write_file" => "Writing " + Field(objectRoot, "path", safeToolName),
            "edit_file" => "Editing " + Field(objectRoot, "path", safeToolName),
            "notebook_edit" => "Editing " + Field(objectRoot, "notebook_path", safeToolName),
            "grep" or "glob" => "Searching for " + Field(objectRoot, "pattern", safeToolName),
            "web_search" or "tool_search" => "Searching for " + Field(objectRoot, "query", safeToolName),
            _ => string.IsNullOrEmpty(argumentPreview)
                ? safeToolName
                : $"{safeToolName} {argumentPreview}",
        };

        return TruncateToCells(preview, ToolDisplayModeResolver.CompactInputPreviewMax);
    }

    public static string CompletedText(ToolActivitySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var noun = summary.HomogeneousToolName == "run_command"
            ? summary.TotalCalls == 1 ? "shell command" : "shell commands"
            : summary.TotalCalls == 1 ? "tool" : "tools";
        var suffix = (summary.FailedCalls, summary.Cancelled) switch
        {
            (0, false) => string.Empty,
            (> 0, false) => $" - {summary.FailedCalls} failed",
            (0, true) => " - cancelled",
            _ => $" - {summary.FailedCalls} failed, cancelled",
        };
        return $"Ran {summary.TotalCalls} {noun}{suffix}";
    }

    internal static string TruncateToCells(string? value, int maxCells)
    {
        var text = TerminalTextSanitizer.SanitizeSingleLine(value);
        var limit = Math.Max(0, maxCells);
        if (text.Length == 0 || limit == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, limit));
        var cells = 0;
        foreach (var element in TerminalCellText.Enumerate(text))
        {
            if (builder.Length > 0 &&
                (builder.Length + element.Text.Length > limit || cells + element.CellWidth > limit))
            {
                break;
            }

            if (builder.Length == 0 && element.CellWidth > limit)
            {
                break;
            }

            builder.Append(element.Text);
            cells += element.CellWidth;
            if (builder.Length >= limit || cells >= limit)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private static JsonNode? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Field(JsonObject? root, string name, string fallback)
    {
        try
        {
            var value = root?[name]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(value)
                ? fallback
                : TruncateToCells(SanitizeFreeText(value), ToolDisplayModeResolver.CompactInputPreviewMax);
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static void RedactAdditionalSecrets(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                {
                    if (IsAdditionalSecretKey(key))
                    {
                        obj[key] = JsonValue.Create(SecretRedactor.Placeholder);
                    }
                    else
                    {
                        RedactAdditionalSecrets(obj[key]);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    RedactAdditionalSecrets(item);
                }

                break;
        }
    }

    private static bool IsAdditionalSecretKey(string key) =>
        SecretRedactor.IsSecretHeader(key) ||
        key.Equals("token", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeFreeText(string? value) =>
        SecretRedactor.Redact(TerminalTextSanitizer.SanitizeSingleLine(SecretRedactor.Redact(value)));
}
