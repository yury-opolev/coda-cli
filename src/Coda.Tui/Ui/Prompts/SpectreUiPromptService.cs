using System.Collections.Immutable;
using System.Linq;
using Spectre.Console;

namespace Coda.Tui.Ui.Prompts;

/// <summary>
/// Maps <see cref="UiPromptRequest"/>s onto Spectre.Console widgets. Used as the fallback prompt
/// surface when the actor-driven UI is not active (e.g. an offscreen Spectre console). Responses
/// always carry option ids, never labels.
/// </summary>
public sealed class SpectreUiPromptService : IUiPromptService
{
    private readonly IAnsiConsole _console;

    /// <summary>Create a service that prompts through <paramref name="console"/>.</summary>
    public SpectreUiPromptService(IAnsiConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    /// <inheritdoc />
    public bool IsInteractive => _console.Profile.Capabilities.Interactive;

    /// <inheritdoc />
    public async Task<UiPromptResponse> RequestAsync(UiPromptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return request.Kind switch
            {
                UiPromptKind.Confirm => await ConfirmAsync(request, cancellationToken).ConfigureAwait(false),
                UiPromptKind.SelectOne => await SelectOneAsync(request, cancellationToken).ConfigureAwait(false),
                UiPromptKind.SelectMany => await SelectManyAsync(request, cancellationToken).ConfigureAwait(false),
                _ => await TextAsync(request, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException)
        {
            return new UiPromptResponse(true, [], null);
        }
    }

    private async Task<UiPromptResponse> ConfirmAsync(UiPromptRequest request, CancellationToken cancellationToken)
    {
        var defaultYes = string.Equals(request.DefaultValue, "yes", StringComparison.OrdinalIgnoreCase);
        var confirmed = await _console.ConfirmAsync(request.Title, defaultYes, cancellationToken).ConfigureAwait(false);
        return new UiPromptResponse(false, [confirmed ? "yes" : "no"], null);
    }

    private async Task<UiPromptResponse> SelectOneAsync(UiPromptRequest request, CancellationToken cancellationToken)
    {
        var prompt = new SelectionPrompt<UiPromptOption>()
            .Title(request.Title)
            .UseConverter(o => o.Label)
            .AddChoices(request.Options);

        var choice = await _console.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        return new UiPromptResponse(false, [choice.Id], choice.Label);
    }

    private async Task<UiPromptResponse> SelectManyAsync(UiPromptRequest request, CancellationToken cancellationToken)
    {
        var prompt = new MultiSelectionPrompt<UiPromptOption>()
            .Title(request.Title)
            .UseConverter(o => o.Label)
            .NotRequired()
            .AddChoices(request.Options);

        var selected = await _console.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        return new UiPromptResponse(
            false,
            [.. selected.Select(o => o.Id)],
            string.Join(", ", selected.Select(o => o.Label)));
    }

    private async Task<UiPromptResponse> TextAsync(UiPromptRequest request, CancellationToken cancellationToken)
    {
        var prompt = new TextPrompt<string>(request.Title);
        if (request.DefaultValue is not null)
        {
            prompt.DefaultValue(request.DefaultValue);
        }

        if (!request.Required)
        {
            prompt.AllowEmpty();
        }

        if (request.Kind == UiPromptKind.Secret)
        {
            prompt.Secret();
        }

        var text = await _console.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        return new UiPromptResponse(false, [], text);
    }
}
