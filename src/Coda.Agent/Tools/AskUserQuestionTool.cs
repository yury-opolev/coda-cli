using System.Text.Json;

namespace Coda.Agent.Tools;

/// <summary>
/// Lets the agent ask the user a structured multiple-choice question and receive the answer.
/// When no interactive user is available (headless mode) the tool returns a graceful no-op
/// message so the agent can proceed with its best judgment instead of failing.
/// </summary>
public sealed class AskUserQuestionTool : ITool
{
    public string Name => "ask_user_question";

    public string Description =>
        "Ask the user a structured multiple-choice question and get their answer. " +
        "Use when you need clarification or a decision from the user before proceeding. " +
        "Provide clear options. For multiSelect, the user may choose more than one option.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "question": {
              "type": "string",
              "description": "The question to ask the user."
            },
            "options": {
              "type": "array",
              "items": { "type": "string" },
              "description": "The list of choices to present to the user."
            },
            "multiSelect": {
              "type": "boolean",
              "description": "When true the user may select multiple options (default false)."
            }
          },
          "required": ["question", "options"]
        }
        """;

    // Read-only: the tool causes no file or network mutations; the interactive block is
    // intentional and host-gated (null UserQuestion = headless, handled gracefully below).
    public bool IsReadOnly => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken cancellationToken = default)
    {
        if (!input.TryGetProperty("question", out var questionEl) || questionEl.ValueKind != JsonValueKind.String)
        {
            return new ToolResult("ask_user_question requires a 'question' string.", IsError: true);
        }

        var question = questionEl.GetString() ?? string.Empty;

        if (!input.TryGetProperty("options", out var optionsEl) || optionsEl.ValueKind != JsonValueKind.Array)
        {
            return new ToolResult("ask_user_question requires an 'options' array.", IsError: true);
        }

        var options = new List<string>();
        foreach (var item in optionsEl.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                options.Add(text);
            }
        }

        if (options.Count == 0)
        {
            return new ToolResult("ask_user_question requires at least one non-empty option.", IsError: true);
        }

        var multiSelect = input.TryGetProperty("multiSelect", out var multiEl) && multiEl.ValueKind == JsonValueKind.True;

        if (context.UserQuestion is null)
        {
            return new ToolResult("No interactive user is available; proceed using your best judgment.");
        }

        var answer = await context.UserQuestion.AskAsync(question, options, multiSelect, cancellationToken).ConfigureAwait(false);
        return new ToolResult($"User selected: {answer}");
    }
}
