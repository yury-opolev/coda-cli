using System.Collections.Immutable;
using System.Linq;
using Spectre.Console;
using Coda.Tui.Ui.Rendering;

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
        var confirmed = await _console.ConfirmAsync(Render(request.Title), defaultYes, cancellationToken).ConfigureAwait(false);
        return new UiPromptResponse(false, [confirmed ? "yes" : "no"], null);
    }

    private async Task<UiPromptResponse> SelectOneAsync(UiPromptRequest request, CancellationToken cancellationToken)
    {
        var prompt = new SelectionPrompt<UiPromptOption>()
            .Title(Render(request.Title))
            .UseConverter(o => Render(UiPromptOptionFormatter.Format(o)))
            .AddChoices(request.Options);

        if (request.DefaultValue is { Length: > 0 } defaultId)
        {
            var defaultOption = request.Options.FirstOrDefault(o => string.Equals(o.Id, defaultId, StringComparison.Ordinal));
            if (defaultOption is not null)
            {
                prompt.DefaultValue(defaultOption);
            }
        }

        var choice = await _console.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
        return new UiPromptResponse(false, [choice.Id], choice.Label);
    }

    private async Task<UiPromptResponse> SelectManyAsync(UiPromptRequest request, CancellationToken cancellationToken)
    {
        var prompt = new MultiSelectionPrompt<UiPromptOption>()
            .Title(Render(request.Title))
            .UseConverter(o => Render(UiPromptOptionFormatter.Format(o)))
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
        var prompt = new TextPrompt<string>(Render(request.Title));
        if (request.DefaultValue is not null)
        {
            prompt.DefaultValue(Render(request.DefaultValue));
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

    private static string Render(string? value) =>
        Markup.Escape(TerminalTextSanitizer.SanitizeSingleLine(value));
}
