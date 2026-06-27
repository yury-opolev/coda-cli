using Coda.Agent;
using Spectre.Console;

namespace Coda.Tui.Agent;

/// <summary>
/// Renders an agent question as a Spectre.Console selection prompt so the user can
/// pick one (or more) options interactively in the TUI.
/// </summary>
public sealed class TuiUserQuestionPrompt : IUserQuestionPrompt
{
    private readonly IAnsiConsole console;

    public TuiUserQuestionPrompt(IAnsiConsole console)
    {
        this.console = console;
    }

    public Task<string> AskAsync(
        string question,
        IReadOnlyList<string> options,
        bool multiSelect,
        CancellationToken cancellationToken = default)
    {
        if (!this.console.Profile.Capabilities.Interactive)
        {
            // Non-interactive console: fall back gracefully without prompting.
            // In practice this branch is unreachable because AgentRunner only wires this
            // prompt when the console is interactive; kept as defence in depth.
            return Task.FromResult(string.Empty);
        }

        if (multiSelect)
        {
            var multiPrompt = new MultiSelectionPrompt<string>()
                .Title(question)
                .NotRequired();

            foreach (var option in options)
            {
                multiPrompt.AddChoice(option);
            }

            var selected = this.console.Prompt(multiPrompt);
            return Task.FromResult(string.Join(", ", selected));
        }
        else
        {
            var singlePrompt = new SelectionPrompt<string>()
                .Title(question)
                .AddChoices(options);

            var choice = this.console.Prompt(singlePrompt);
            return Task.FromResult(choice);
        }
    }
}
