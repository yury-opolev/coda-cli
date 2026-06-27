using LlmAuth;
using LlmAuth.Providers.ClaudeAi;
using LlmAuth.Providers.GitHubCopilot;
using LlmAuth.Storage.Windows;

// Sample CLI exercising the LlmAuth library.
//   Claude.ai:  login | headers | authurl | logout
//   Copilot:    copilot-login | copilot-headers | copilot-logout

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

var identity = new AnthropicClientIdentity();
using var claude = new ClaudeAiProvider();
using var copilot = new GitHubCopilotProvider();
var store = new DpapiTokenStore();
var manager = new CredentialManager(store, [claude, copilot, new ApiKeyProvider()]);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    switch (command)
    {
        case "login":
            Console.WriteLine("Opening your browser to sign in to Claude.ai…");
            var credential = await manager.LoginAsync(ClaudeAiProvider.Id, cancellationToken: cts.Token);
            Console.WriteLine("Signed in.");
            Console.WriteLine($"  account : {credential.Account?.EmailAddress ?? "(unknown)"}");
            Console.WriteLine($"  scopes  : {string.Join(" ", credential.Scopes)}");
            Console.WriteLine($"  expires : {credential.ExpiresAt:u}");
            break;

        case "authurl":
            // Low-layer: print the exact authorize URL without opening a browser.
            var flow = claude.BeginLogin(new LoginOptions { RedirectMode = RedirectMode.Manual });
            Console.WriteLine(flow.AuthorizeUrl);
            break;

        case "headers":
            PrintHeaders("Claude.ai", identity.GetDefaultHeaders(),
                (await manager.GetAuthHeadersAsync(ClaudeAiProvider.Id, cts.Token)).Headers);
            break;

        case "logout":
            await manager.LogoutAsync(ClaudeAiProvider.Id, cts.Token);
            Console.WriteLine("Logged out (stored Claude.ai credential removed).");
            break;

        case "copilot-login":
            // Device flow: the library calls back with a code to enter at GitHub.
            var copilotCred = await manager.LoginWithDeviceCodeAsync(
                GitHubCopilotProvider.Id,
                (prompt, _) =>
                {
                    Console.WriteLine();
                    Console.WriteLine($"  1. Open: {prompt.VerificationUri}");
                    Console.WriteLine($"  2. Enter code: {prompt.UserCode}");
                    Console.WriteLine("  (waiting for you to authorize…)");
                    return Task.CompletedTask;
                },
                cancellationToken: cts.Token);
            Console.WriteLine("Signed in to GitHub Copilot.");
            Console.WriteLine($"  copilot token expires: {copilotCred.ExpiresAt:u}");
            break;

        case "copilot-headers":
            PrintHeaders("GitHub Copilot", new Dictionary<string, string>(),
                (await manager.GetAuthHeadersAsync(GitHubCopilotProvider.Id, cts.Token)).Headers);
            break;

        case "copilot-logout":
            await manager.LogoutAsync(GitHubCopilotProvider.Id, cts.Token);
            Console.WriteLine("Logged out (stored Copilot credential removed).");
            break;

        default:
            Console.WriteLine("Usage:");
            Console.WriteLine("  Claude.ai : login | headers | authurl | logout");
            Console.WriteLine("  Copilot   : copilot-login | copilot-headers | copilot-logout");
            Console.WriteLine($"User-Agent: {identity.GetUserAgent()}");
            break;
    }

    return 0;
}
catch (LlmAuthException ex)
{
    Console.Error.WriteLine($"Auth error: {ex.Message}");
    return 1;
}

static void PrintHeaders(string label, IReadOnlyDictionary<string, string> identityHeaders, IReadOnlyDictionary<string, string> credentialHeaders)
{
    if (identityHeaders.Count > 0)
    {
        Console.WriteLine($"# {label} identity headers:");
        foreach (var (k, v) in identityHeaders)
        {
            Console.WriteLine($"{k}: {v}");
        }
    }

    Console.WriteLine($"# {label} credential headers:");
    foreach (var (k, v) in credentialHeaders)
    {
        var shown = k.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ? "Bearer <redacted>" : v;
        Console.WriteLine($"{k}: {shown}");
    }
}
