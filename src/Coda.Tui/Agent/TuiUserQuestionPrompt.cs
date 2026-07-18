using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Coda.Agent;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.Prompts;

namespace Coda.Tui.Agent;

/// <summary>
/// Presents an agent question through the host-neutral prompt surface (single- or multi-select) and
/// returns the chosen text. Publishes <see cref="UserQuestionRequestedEvent"/>/
/// <see cref="UserQuestionResolvedEvent"/>. Selected options are mapped back to labels in their
/// original order and joined with ", "; cancellation yields an empty answer.
/// </summary>
public sealed class TuiUserQuestionPrompt(IUiPromptService prompts, IUiEventPublisher events) : IUserQuestionPrompt
{
    public async Task<string> AskAsync(
        string question,
        IReadOnlyList<string> options,
        bool multiSelect,
        CancellationToken cancellationToken = default)
    {
        events.Publish(new UserQuestionRequestedEvent(question, options, multiSelect));

        var promptOptions = options
            .Select((label, index) => new UiPromptOption(index.ToString(CultureInfo.InvariantCulture), label))
            .ToImmutableArray();

        var request = multiSelect
            ? UiPromptRequest.SelectMany(question, promptOptions)
            : UiPromptRequest.Select(question, promptOptions);

        var response = await prompts.RequestAsync(request, cancellationToken).ConfigureAwait(false);

        string answer;
        if (response.Cancelled)
        {
            answer = string.Empty;
        }
        else
        {
            var selected = new HashSet<string>(response.SelectedIds);
            var labels = new List<string>();
            for (var index = 0; index < options.Count; index++)
            {
                if (selected.Contains(index.ToString(CultureInfo.InvariantCulture)))
                {
                    labels.Add(options[index]);
                }
            }

            answer = string.Join(", ", labels);
        }

        events.Publish(new UserQuestionResolvedEvent(question, answer));
        return answer;
    }
}