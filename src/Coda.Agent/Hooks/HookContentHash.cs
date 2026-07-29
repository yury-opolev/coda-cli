using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Coda.Agent.Hooks;

/// <summary>
/// Computes a stable SHA-256 content hash of a hook's behaviorally-significant fields.
/// The hash is used as the trust key: editing a trusted hook's command or URL changes the
/// hash and causes a re-prompt rather than inheriting the previous trust decision.
/// </summary>
public static class HookContentHash
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Returns a short human-readable identifier for a hook (command, url, or handler type).</summary>
    internal static string HookId(UserHook hook) =>
        hook.Command is { } cmd ? cmd
        : hook.Url is { } url ? url
        : $"[{hook.HandlerType ?? "command"}]";

    /// <summary>
    /// Returns the hex-encoded SHA-256 of the hook's behavioral fields. Fields that only
    /// affect display (e.g. source path) are excluded so cosmetic changes do not revoke trust.
    /// </summary>
    public static string Compute(UserHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        // Canonical representation of the fields that determine what the hook does.
        // Order is fixed; null fields are serialised as JSON null so the hash is stable.
        var canonical = new
        {
            @event = hook.Event?.ToLowerInvariant(),
            handlerType = hook.HandlerType?.ToLowerInvariant() ?? "command",
            command = hook.Command,
            url = hook.Url,
            hookPrompt = hook.HookPrompt,
            agentType = hook.AgentType,
            matcher = hook.Matcher,
            timeoutSeconds = hook.TimeoutSeconds,
            failOpen = hook.FailOpen,
            unattendedDecision = hook.UnattendedDecision?.ToLowerInvariant(),
            allowSystemPromptReplace = hook.AllowSystemPromptReplace,
            mutates = hook.Mutates is null ? null : hook.Mutates.OrderBy(m => m, StringComparer.Ordinal).ToArray(),
        };

        var json = JsonSerializer.Serialize(canonical, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
