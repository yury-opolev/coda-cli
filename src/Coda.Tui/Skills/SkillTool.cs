using System.Text;
using System.Text.Json;
using Coda.Agent;

namespace Coda.Tui.Skills;

/// <summary>
/// Model-invocable <c>skill</c> tool. Exposes the session's model-invocable skills so the agent
/// can choose and load skills by name. The <c>name</c> parameter is an enum built from the
/// discovered skill names — the model cannot hallucinate a skill that does not exist.
/// </summary>
/// <remarks>
/// <para>
/// Injected into <see cref="Coda.Sdk.SessionOptions.ExtraTools"/> at composition time. Created
/// with the already-loaded skill list; does not re-scan the filesystem. Use
/// <see cref="CreateOrNull"/> to skip registration when no model-invocable skills exist.
/// </para>
/// <para>
/// <strong>Trust gap (deferred):</strong> this tool auto-loads skill text from directories Coda
/// does not own (<c>~/.claude/skills</c>, plugin directories, etc.) directly into the model's
/// context without a permission prompt. This is intentional while <see cref="IsReadOnly"/> is
/// <see langword="true"/>, but it means third-party skill content reaches the model silently.
/// Origin-based trust gating (prompting the user before loading skills from external directories)
/// is deferred to the Trust phase of the skills roadmap. Until then, callers must consider that
/// model-invocable skills are an ambient injection vector for content Coda did not author.
/// </para>
/// </remarks>
public sealed class SkillTool : ITool
{
    /// <summary>Default combined character cap for the tool description catalogue.</summary>
    public const int DefaultDescriptionCap = 8_000;

    private readonly IReadOnlyList<SkillDefinition> _skills;
    private readonly SkillSessionState _state;
    private readonly string _inputSchemaJson;
    private readonly string _description;

    /// <summary>
    /// Initializes a new <see cref="SkillTool"/> with the provided <paramref name="modelInvocableSkills"/>
    /// (must be pre-filtered to exclude <c>disable-model-invocation: true</c> skills) and session state.
    /// </summary>
    /// <param name="modelInvocableSkills">Skills the model may invoke; must be non-empty.</param>
    /// <param name="state">Per-session state that tracks loaded bodies and reattach content.</param>
    /// <param name="descriptionCap">Combined character cap for the description catalogue; injectable for tests.</param>
    public SkillTool(
        IReadOnlyList<SkillDefinition> modelInvocableSkills,
        SkillSessionState state,
        int descriptionCap = DefaultDescriptionCap)
    {
        ArgumentNullException.ThrowIfNull(modelInvocableSkills);
        ArgumentNullException.ThrowIfNull(state);

        this._skills = modelInvocableSkills;
        this._state = state;
        this._inputSchemaJson = BuildSchema(modelInvocableSkills);
        this._description = BuildDescription(modelInvocableSkills, descriptionCap);
    }

    /// <inheritdoc/>
    public string Name => "skill";

    /// <inheritdoc/>
    public string Description => this._description;

    /// <inheritdoc/>
    public string InputSchemaJson => this._inputSchemaJson;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <summary>
    /// Creates a <see cref="SkillTool"/> from all skills in <paramref name="allSkills"/>, filtering
    /// out any with <c>disable-model-invocation: true</c>. Returns <see langword="null"/> when the
    /// filtered set is empty — in that case the tool must not be registered.
    /// </summary>
    public static SkillTool? CreateOrNull(
        IReadOnlyList<SkillDefinition> allSkills,
        SkillSessionState state,
        int descriptionCap = DefaultDescriptionCap)
    {
        ArgumentNullException.ThrowIfNull(allSkills);
        ArgumentNullException.ThrowIfNull(state);

        var modelInvocable = allSkills.Where(s => !s.DisableModelInvocation).ToList();
        return modelInvocable.Count > 0 ? new SkillTool(modelInvocable, state, descriptionCap) : null;
    }

    /// <inheritdoc/>
    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var name = input.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(new ToolResult("Missing required 'name' argument.", IsError: true));
        }

        var skill = this._skills.FirstOrDefault(
            s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
        {
            var valid = string.Join(", ", this._skills.Select(s => s.Name));
            return Task.FromResult(
                new ToolResult($"Unknown skill '{name}'. Valid options: {valid}", IsError: true));
        }

        // Argument binding — opt-in rule (same as /skill <name>): substitute only when the
        // caller supplied arguments OR the skill declares named arguments.
        var argumentsString = input.TryGetProperty("arguments", out var argsProp)
            ? argsProp.GetString() ?? string.Empty
            : string.Empty;

        IReadOnlyList<string> invokeArgs = !string.IsNullOrWhiteSpace(argumentsString)
            ? argumentsString.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];

        var body = (invokeArgs.Count > 0 || skill.Arguments.Count > 0)
            ? SkillArgumentBinder.Bind(skill.Body, skill.Arguments, invokeArgs)
            : skill.Body;

        var (_, content) = this._state.TryLoad(skill.Name, body);
        return Task.FromResult(new ToolResult(content));
    }

    // ── Schema construction ────────────────────────────────────────────────

    private static string BuildSchema(IReadOnlyList<SkillDefinition> skills)
    {
        var enumValues = string.Join(",", skills.Select(s => $"\"{EscapeJsonString(s.Name)}\""));
        return
            "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"Name of the skill to invoke.\",\"enum\":["
            + enumValues
            + "]},\"arguments\":{\"type\":\"string\",\"description\":\"Optional space-separated arguments substituted into the skill body ($1, $2, $name, $ARGUMENTS).\"}},\"required\":[\"name\"]}";
    }

    // ── Description construction ───────────────────────────────────────────

    private static string BuildDescription(IReadOnlyList<SkillDefinition> skills, int cap)
    {
        const string Preamble = "Invoke a model-invocable skill by name. Available skills:\n";
        var sb = new StringBuilder(Preamble);

        var listed = 0;
        foreach (var skill in skills)
        {
            var line = FormatCatalogueLine(skill);
            var addition = line.Length + 1; // +1 for the newline from AppendLine
            if (sb.Length + addition > cap)
            {
                break;
            }

            sb.AppendLine(line);
            listed++;
        }

        var unlisted = skills.Count - listed;
        if (unlisted > 0)
        {
            sb.AppendLine($"({unlisted} more skill{(unlisted == 1 ? string.Empty : "s")} not listed)");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatCatalogueLine(SkillDefinition skill)
    {
        var sb = new StringBuilder();
        sb.Append(skill.Name);
        sb.Append(" — ");
        sb.Append(skill.Description);
        if (!string.IsNullOrWhiteSpace(skill.WhenToUse))
        {
            sb.Append(" | ");
            sb.Append(skill.WhenToUse);
        }

        return sb.ToString();
    }

    private static string EscapeJsonString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
