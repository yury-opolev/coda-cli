namespace Coda.Tui.Plugins;

/// <summary>A class of components that a plugin may provide.</summary>
public enum PluginComponentClass
{
    /// <summary>Prompt-based skills invocable by the model or user.</summary>
    Skill,

    /// <summary>Shell subprocess or HTTP hooks that run on relevant agent lifecycle events.</summary>
    Hook,

    /// <summary>Model Context Protocol server processes.</summary>
    McpServer,

    /// <summary>Custom subagent type definitions.</summary>
    Subagent,
}
