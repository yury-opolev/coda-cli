using System.Text.Json.Nodes;
using Coda.Sdk;
using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmAuth.Storage.Windows;
using LlmClient;

namespace Coda.Tui;

/// <summary>
/// Non-interactive <c>coda models</c>: prints the active provider's model list
/// (live → models.dev catalog → built-in) as text or, with <c>--json</c>, JSON.
/// The headless counterpart of the TUI <c>/model</c> listing.
/// </summary>
public static class ModelsRunner
{
    /// <summary>
    /// Resolve the configured (provider, model) from the precedence chain
    /// explicit flag → persisted settings default — the SAME resolution
    /// <c>coda serve</c> and <c>coda run</c> apply. Returns <see langword="null"/>
    /// for either when neither the flag nor settings supplies it (no built-in
    /// fallback); <see cref="RunAsync"/> then fails fast. Exposed for parity testing.
    /// </summary>
    public static (string? ProviderId, string? Model) ResolveDefaults(
        string? providerFlag,
        string workingDirectory,
        string? userSettingsDir = null)
    {
        var settings = Coda.Agent.Settings.SettingsLoader.Load(workingDirectory, userSettingsDir);
        return Coda.Sdk.Providers.ProviderModelResolver.Resolve(providerFlag, modelFlag: null, settings);
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (!TryParse(args, out var providerToken, out var cwd, out var json, out var refresh, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Usage: coda models [--provider <id>] [--cwd <path>] [--json] [--refresh]");
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Coda requires Windows (it uses DPAPI for secure credential storage).");
            return 1;
        }

        var workingDirectory = cwd ?? Directory.GetCurrentDirectory();
        string providerId;
        string model;
        try
        {
            var (resolvedProvider, resolvedModel) = ResolveDefaults(providerToken, workingDirectory);
            (providerId, model) = Coda.Sdk.Providers.ProviderModelResolver.Require(resolvedProvider, resolvedModel);
        }
        catch (Coda.Sdk.Providers.ProviderModelNotConfiguredException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        using var claude = new ClaudeAiProvider();
        using var copilot = new GitHubCopilotProvider();
        var apiKey = new ApiKeyProvider();
        var credentials = new CredentialManager(new DpapiTokenStore(), [claude, copilot, apiKey]);

        var options = new SessionOptions
        {
            ProviderId = providerId,
            Model = model,
            WorkingDirectory = workingDirectory,
        };

        using var session = new CodaSession(credentials, options);
        var result = await session.ListModelsAsync(refresh, cancellationToken).ConfigureAwait(false);

        if (json)
        {
            WriteJson(result);
        }
        else
        {
            WriteText(result, providerId);
        }

        return 0;
    }

    private static void WriteJson(ModelListResult result)
    {
        var models = new JsonArray();
        foreach (var m in result.Models)
        {
            models.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["displayName"] = m.DisplayName,
                ["contextLimit"] = m.ContextLimit,
            });
        }

        var root = new JsonObject
        {
            ["source"] = result.Source.ToString().ToLowerInvariant(),
            ["provider"] = result.ProviderId,
            ["models"] = models,
        };
        Console.Out.WriteLine(root.ToJsonString());
    }

    private static void WriteText(ModelListResult result, string providerId)
    {
        Console.Error.WriteLine($"{providerId} models ({result.Source.ToString().ToLowerInvariant()}):");
        foreach (var m in result.Models)
        {
            var detail = m.DisplayName;
            if (m.ContextLimit is int ctx)
            {
                detail = string.IsNullOrEmpty(detail) ? $"{ctx} ctx" : $"{detail} · {ctx} ctx";
            }

            Console.Out.WriteLine(string.IsNullOrEmpty(detail) ? m.Id : $"{m.Id}\t{detail}");
        }
    }

    public static bool TryParse(
        IReadOnlyList<string> args,
        out string? provider,
        out string? cwd,
        out bool json,
        out bool refresh,
        out string? error)
    {
        provider = null;
        cwd = null;
        json = false;
        refresh = false;
        error = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--provider":
                    if (++i >= args.Count) { error = "Missing value for --provider."; return false; }
                    provider = args[i];
                    break;
                case "--cwd":
                    if (++i >= args.Count) { error = "Missing value for --cwd."; return false; }
                    cwd = args[i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--refresh":
                    refresh = true;
                    break;
                default:
                    error = $"Unknown argument '{args[i]}'.";
                    return false;
            }
        }

        return true;
    }
}
