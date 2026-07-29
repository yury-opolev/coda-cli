namespace Coda.Tui.Plugins;

/// <summary>
/// The type of a user-configuration field declared in a plugin manifest's
/// <c>userConfig</c> array.
/// </summary>
public enum UserConfigFieldType
{
    /// <summary>A free-text string value.</summary>
    String,

    /// <summary>A boolean (<c>true</c> / <c>false</c>) value.</summary>
    Boolean,

    /// <summary>A numeric value.</summary>
    Number,

    /// <summary>A selection from a fixed set of declared options.</summary>
    Choice,

    /// <summary>
    /// A sensitive secret value. Stored encrypted in the OS credential store
    /// and never written to <c>settings.json</c> or any other plaintext file.
    /// </summary>
    Secret,
}

/// <summary>A single user-configuration field declared by a plugin.</summary>
public sealed record UserConfigField(
    string Key,
    UserConfigFieldType Type,
    string Label,
    bool Required,
    string? Default,
    IReadOnlyList<string> Options);

/// <summary>A plugin dependency with an optional semver constraint.</summary>
public sealed record PluginDependency(
    string PluginName,
    string? SemVerRange);

/// <summary>
/// The fully-parsed component-map manifest for a <c>plugin.json</c> file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unrecognised top-level fields are silently ignored by design.</b>
/// This mirrors Claude Code's convention and lets one <c>plugin.json</c> also serve as a
/// VS Code extension manifest, an npm <c>package.json</c>, or another ecosystem's descriptor
/// without causing a parse error in Coda.
/// </para>
/// <para>
/// <b>Asymmetry between <c>skills</c> and the other component directories:</b>
/// <c>skills</c> is additive — its entries are appended to the default <c>skills/</c>
/// subdirectory scan — whereas <c>commands</c>, <c>agents</c>, <c>outputStyles</c>, and
/// <c>themes</c> are exclusive: a declared path replaces the default directory for that
/// component. This follows Claude Code's rule, which lets a plugin add skills from
/// non-conventional locations (e.g. next to their documentation) without overriding
/// the whole skill tree.
/// </para>
/// </remarks>
public sealed record PluginManifest
{
    // -------------------------------------------------------------------------
    // Identity and metadata
    // -------------------------------------------------------------------------

    /// <summary>Unique kebab-case plugin identifier. Required.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Semver string, e.g. <c>1.2.3</c>. Defaults to <c>0.0.0</c>.</summary>
    public string Version { get; init; } = "0.0.0";

    /// <summary>Short human-readable description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Human-readable display label; shown instead of <see cref="Name"/> where space permits.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Plugin author name or contact.</summary>
    public string? Author { get; init; }

    /// <summary>URL of the plugin's homepage or documentation.</summary>
    public string? Homepage { get; init; }

    /// <summary>URL or shorthand of the plugin's source repository.</summary>
    public string? Repository { get; init; }

    /// <summary>SPDX license identifier (e.g. <c>MIT</c>, <c>Apache-2.0</c>).</summary>
    public string? License { get; init; }

    /// <summary>Discovery keywords for marketplace search.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// When <see langword="false"/> the plugin is installed in a disabled state until the user
    /// explicitly enables it. Defaults to <see langword="true"/>.
    /// </summary>
    public bool DefaultEnabled { get; init; } = true;

    // -------------------------------------------------------------------------
    // Component directories
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extra skill directories, in addition to the default <c>skills/</c> subdirectory.
    /// Each entry is a relative path within the plugin directory.
    /// Additive — see the remarks on <see cref="PluginManifest"/> for the rationale.
    /// </summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>
    /// Directory containing custom commands.
    /// Replaces the default <c>commands/</c> subdirectory when set.
    /// </summary>
    public string? Commands { get; init; }

    /// <summary>
    /// Directory containing sub-agent definitions.
    /// Replaces the default <c>agents/</c> subdirectory when set.
    /// </summary>
    public string? Agents { get; init; }

    /// <summary>
    /// Directory containing output-style definitions.
    /// Replaces the default <c>outputStyles/</c> subdirectory when set.
    /// </summary>
    public string? OutputStyles { get; init; }

    /// <summary>
    /// Directory containing theme definitions.
    /// Replaces the default <c>themes/</c> subdirectory when set.
    /// </summary>
    public string? Themes { get; init; }

    // -------------------------------------------------------------------------
    // Runtime configuration (Phase 4 — parsed but not yet wired)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Inline or path-based hook configurations. Parsed and exposed; consumed in Phase 4.
    /// </summary>
    public IReadOnlyList<string> Hooks { get; init; } = [];

    /// <summary>
    /// MCP server configurations. Parsed and exposed; consumed in Phase 4.
    /// </summary>
    public IReadOnlyList<string> McpServers { get; init; } = [];

    /// <summary>
    /// LSP server configurations. Parsed and exposed; wiring via the existing LSP loader.
    /// </summary>
    public IReadOnlyList<string> LspServers { get; init; } = [];

    // -------------------------------------------------------------------------
    // User configuration and dependencies
    // -------------------------------------------------------------------------

    /// <summary>Install-time user-configuration prompts.</summary>
    public IReadOnlyList<UserConfigField> UserConfig { get; init; } = [];

    /// <summary>Required plugin dependencies with optional semver ranges.</summary>
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];
}
