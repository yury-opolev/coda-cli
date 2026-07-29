using Microsoft.Extensions.Logging;

namespace Coda.Agent.OutputStyles;

/// <summary>The built-in named output styles (personas) available to the user.</summary>
public static class BuiltInOutputStyles
{
    private static readonly OutputStyle Default = new(
        Name: "default",
        Description: "Standard balanced responses with no additional style guidance.",
        SystemPromptSuffix: string.Empty);

    private static readonly OutputStyle Concise = new(
        Name: "concise",
        Description: "Terse, minimal prose — give the answer with as few words as possible.",
        SystemPromptSuffix:
            "Respond as tersely as possible. Use bullet points and short sentences. Omit all preamble, filler, and unnecessary explanation. Prefer code over prose. If a single word or line suffices, use it. Never restate what the user said. Every word must earn its place.");

    private static readonly OutputStyle Explanatory = new(
        Name: "explanatory",
        Description: "Teach as you go — explain reasoning, concepts, and trade-offs.",
        SystemPromptSuffix:
            "Adopt a teaching tone. As you work through tasks, explain your reasoning, highlight relevant concepts, and surface important trade-offs or alternatives the user should understand. Define technical terms on first use. When you make a decision, say why. Help the user build mental models, not just get answers.");

    private static readonly OutputStyle CodeReviewer = new(
        Name: "code-reviewer",
        Description: "Focus on reviewing and critiquing code — spot issues, suggest improvements.",
        SystemPromptSuffix:
            "Act as a thorough code reviewer. Prioritize correctness, clarity, performance, security, and maintainability. When examining code, call out bugs, anti-patterns, missed edge cases, and unclear naming. Suggest concrete improvements with brief rationale. Be direct and specific — generic praise without substance is unhelpful. If the code is good, say so briefly and explain why.");

    /// <summary>All available built-in styles in display order. Never includes plugin styles.</summary>
    public static IReadOnlyList<OutputStyle> All { get; } = [Default, Concise, Explanatory, CodeReviewer];

    // Plugin styles — registered at startup, never overlap with built-ins.
    private static readonly object PluginLock = new();
    private static readonly List<OutputStyle> pluginStylesList = [];
    private static readonly Dictionary<string, OutputStyle> pluginStylesByName =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a plugin-contributed output style. If the name collides with a built-in,
    /// the registration is silently dropped and the supplied logger receives a warning.
    /// </summary>
    public static bool RegisterPlugin(OutputStyle style, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(style);

        // Built-in names are protected.
        foreach (var builtin in All)
        {
            if (string.Equals(builtin.Name, style.Name, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning(
                    "Plugin output style '{Name}' collides with a built-in style and will be ignored.",
                    style.Name);
                return false;
            }
        }

        lock (PluginLock)
        {
            if (pluginStylesByName.ContainsKey(style.Name))
            {
                // Duplicate plugin style — last registration wins for the same plugin being reloaded.
                pluginStylesList.RemoveAll(s => string.Equals(s.Name, style.Name, StringComparison.OrdinalIgnoreCase));
            }

            pluginStylesList.Add(style);
            pluginStylesByName[style.Name] = style;
        }

        return true;
    }

    /// <summary>Removes all plugin-registered styles. Called at test teardown or on plugin reload.</summary>
    public static void ClearPluginStyles()
    {
        lock (PluginLock)
        {
            pluginStylesList.Clear();
            pluginStylesByName.Clear();
        }
    }

    /// <summary>Returns plugin-registered styles (not including built-ins).</summary>
    public static IReadOnlyList<OutputStyle> GetPluginStyles()
    {
        lock (PluginLock)
        {
            return [.. pluginStylesList];
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if the name matches a built-in or a registered plugin style.
    /// </summary>
    public static bool IsKnown(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        foreach (var s in All)
        {
            if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }

        lock (PluginLock)
        {
            return pluginStylesByName.ContainsKey(name);
        }
    }

    /// <summary>
    /// Resolves a style name case-insensitively, checking plugin styles after built-ins.
    /// Null, "default", or any unknown name returns the <c>default</c> style so callers always
    /// receive a value.
    /// </summary>
    public static OutputStyle Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
        {
            return Default;
        }

        foreach (var style in All)
        {
            if (string.Equals(style.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return style;
            }
        }

        lock (PluginLock)
        {
            if (pluginStylesByName.TryGetValue(name, out var plugin))
            {
                return plugin;
            }
        }

        // Unknown name → fall back to default so callers always get a value.
        return Default;
    }
}
