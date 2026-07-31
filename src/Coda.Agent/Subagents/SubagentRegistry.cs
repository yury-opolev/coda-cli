namespace Coda.Agent.Subagents;

/// <summary>
/// Resolves subagent type names against a list of plugin-contributed definitions,
/// falling back to <see cref="BuiltInAgents"/> for any unknown or null type.
/// </summary>
public sealed class SubagentRegistry
{
    private readonly IReadOnlyList<SubagentDefinition> pluginAgents;

    /// <summary>
    /// Initialises the registry with an optional list of plugin-contributed agent definitions.
    /// </summary>
    public SubagentRegistry(IReadOnlyList<SubagentDefinition>? pluginAgents = null)
    {
        this.pluginAgents = pluginAgents ?? [];
    }

    /// <summary>
    /// Resolves a subagent type name. Built-in types are checked first (case-insensitive)
    /// so a plugin agent with the same type as a built-in cannot shadow it — plugin
    /// definitions whose type collides with a built-in are rejected at compose time by
    /// <c>PluginComponentComposer</c>, but this order provides defence-in-depth.
    /// Unknown types fall back to <see cref="BuiltInAgents.Resolve"/>.
    /// </summary>
    public SubagentDefinition Resolve(string? type)
    {
        if (!string.IsNullOrWhiteSpace(type))
        {
            // Built-in check: if the type matches a built-in, return it directly without
            // checking plugin agents.
            if (BuiltInAgents.IsBuiltInType(type))
            {
                return BuiltInAgents.Resolve(type);
            }

            foreach (var definition in this.pluginAgents)
            {
                if (string.Equals(definition.Type, type, StringComparison.OrdinalIgnoreCase))
                {
                    return definition;
                }
            }
        }

        return BuiltInAgents.Resolve(type);
    }
}
