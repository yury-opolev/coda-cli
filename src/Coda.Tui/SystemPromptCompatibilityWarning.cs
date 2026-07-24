using LlmAuth.Providers.ClaudeAi;
using Coda.Tui.Repl;
using Coda.Tui.Ui.Events;
using Coda.Tui.Ui.State;

namespace Coda.Tui;

internal static class SystemPromptCompatibilityWarning
{
    public static string? For(string providerId, string? systemPromptOverride) =>
        systemPromptOverride is not null &&
        string.Equals(providerId, ClaudeAiProvider.Id, StringComparison.Ordinal)
            ? "Claude.ai OAuth may require its compatibility system prefix; the exact supplied prompt will be sent unchanged."
            : null;

    public static void Publish(CommandContext context)
    {
        var warning = For(context.Session.ActiveProviderId, context.Session.SystemPromptOverride);
        if (warning is not null)
        {
            context.Events.Publish(new DiagnosticEvent("system prompt", warning, UiNotificationLevel.Warning));
        }
    }
}
