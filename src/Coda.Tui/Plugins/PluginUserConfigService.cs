using Coda.Tui.Ui.Prompts;
using LlmAuth;

namespace Coda.Tui.Plugins;

/// <summary>The outcome of a <see cref="PluginUserConfigService.ConfigureAsync"/> call.</summary>
public sealed record PluginConfigResult(
    bool Ok,
    string? DisabledReason,
    IReadOnlyDictionary<string, string> CollectedValues);

/// <summary>
/// Collects <c>userConfig</c> values declared in a plugin manifest, persists them to the
/// appropriate store, and returns the result.
/// </summary>
/// <remarks>
/// <para>
/// <b>Secret fields</b> (<see cref="UserConfigFieldType.Secret"/>) are stored in the OS
/// credential store (<see cref="ITokenStore"/>) under the key
/// <c>plugin:&lt;pluginName&gt;:&lt;fieldKey&gt;</c> and are <em>never</em> written to
/// <c>plugin-state.json</c> or any other plaintext file.
/// </para>
/// <para>
/// <b>Unattended mode</b> (when <paramref name="prompts"/> is <see langword="null"/> or
/// non-interactive): each field's declared <c>default</c> is used. If a required field has no
/// default the plugin is left disabled with a logged reason rather than half-configured.
/// </para>
/// </remarks>
public sealed class PluginUserConfigService
{
    /// <summary>
    /// Collects and persists <c>userConfig</c> values for a plugin.
    /// </summary>
    /// <param name="pluginName">The plugin's canonical name.</param>
    /// <param name="fields">The <c>userConfig</c> field declarations from the manifest.</param>
    /// <param name="prompts">
    /// Prompt surface used to ask the user for values. When <see langword="null"/> or
    /// <c>!IsInteractive</c>, defaults are used and missing required values disable the plugin.
    /// </param>
    /// <param name="credentialStore">
    /// Encrypted credential store; receives secret field values.
    /// </param>
    /// <param name="stateStore">
    /// Plugin state store; receives non-secret field values.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<PluginConfigResult> ConfigureAsync(
        string pluginName,
        IReadOnlyList<UserConfigField> fields,
        IUiPromptService? prompts,
        ITokenStore credentialStore,
        PluginStateStore stateStore,
        CancellationToken ct = default)
    {
        if (fields.Count == 0)
        {
            return new PluginConfigResult(true, null, new Dictionary<string, string>());
        }

        var isInteractive = prompts?.IsInteractive ?? false;
        var collectedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var secretValues = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            string? value;

            if (isInteractive && prompts is not null)
            {
                value = await PromptForFieldAsync(field, prompts, ct).ConfigureAwait(false);
            }
            else
            {
                value = field.Default;
            }

            if (value is null && field.Required)
            {
                return new PluginConfigResult(
                    false,
                    $"Plugin '{pluginName}' requires configuration field '{field.Key}' " +
                    $"(type: {field.Type}) but no value was provided and no default is declared. " +
                    "Run in interactive mode or set a default to configure this plugin.",
                    collectedValues);
            }

            if (value is null)
            {
                // Optional field with no value — skip
                continue;
            }

            if (field.Type == UserConfigFieldType.Secret)
            {
                secretValues[field.Key] = value;
            }
            else
            {
                collectedValues[field.Key] = value;
            }
        }

        // Persist non-secret values to state store
        if (collectedValues.Count > 0)
        {
            stateStore.SetPluginConfig(pluginName, collectedValues);
        }

        // Persist secret values to credential store
        foreach (var (key, secretValue) in secretValues)
        {
            var credKey = CredentialKey(pluginName, key);
            await credentialStore.SetAsync(credKey, secretValue, ct).ConfigureAwait(false);
        }

        return new PluginConfigResult(true, null, collectedValues);
    }

    /// <summary>
    /// Credential store key for a plugin secret, following the pattern
    /// <c>plugin|&lt;pluginName&gt;|&lt;fieldKey&gt;</c>.
    /// Using <c>|</c> as the separator (instead of <c>:</c>) ensures the key is unambiguous even
    /// if a plugin name or field key happens to contain a colon, and is compatible with the
    /// kebab-case validation enforced by <see cref="PluginManifestParser"/>.
    /// </summary>
    public static string CredentialKey(string pluginName, string fieldKey) =>
        $"plugin|{pluginName}|{fieldKey}";

    private static async Task<string?> PromptForFieldAsync(
        UserConfigField field,
        IUiPromptService prompts,
        CancellationToken ct)
    {
        UiPromptRequest request = field.Type switch
        {
            UserConfigFieldType.Secret =>
                UiPromptRequest.Text(field.Label, field.Default, required: field.Required, secret: true),

            UserConfigFieldType.Boolean =>
                UiPromptRequest.Confirm(field.Label, bool.TryParse(field.Default, out var b) && b),

            UserConfigFieldType.Choice when field.Options.Count > 0 =>
                UiPromptRequest.Select(
                    field.Label,
                    field.Options.Select(o => new UiPromptOption(o, o)),
                    field.Default),

            _ =>
                UiPromptRequest.Text(field.Label, field.Default, required: field.Required),
        };

        var response = await prompts.RequestAsync(request, ct).ConfigureAwait(false);
        if (response.Cancelled)
        {
            return field.Default;
        }

        if (response.Text is not null)
        {
            return response.Text;
        }

        if (response.SelectedIds.Length > 0)
        {
            // For Confirm, map "yes"/"no" back to "true"/"false"
            if (field.Type == UserConfigFieldType.Boolean)
            {
                return response.SelectedIds[0] == "yes" ? "true" : "false";
            }

            return response.SelectedIds[0];
        }

        return field.Default;
    }
}
