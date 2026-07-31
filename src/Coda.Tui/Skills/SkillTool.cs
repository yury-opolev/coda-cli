using System.Text;
using System.Text.Json;
using Coda.Agent;
using Coda.Agent.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// Skills whose <see cref="SkillDefinition.Origin"/> is <see cref="SkillOrigin.Claude"/> or
/// <see cref="SkillOrigin.Plugin"/> require approval from the <see cref="SkillOriginGate"/>
/// before the model may load their body. Explicit <c>/skill &lt;name&gt;</c> invocations by the
/// user are not routed through this tool and are therefore never gated.
/// </para>
/// </remarks>
public sealed partial class SkillTool : ITool, ISkillShapeDeltaSource
{
    /// <summary>Default combined character cap for the tool description catalogue.</summary>
    public const int DefaultDescriptionCap = 8_000;

    private const string ConsentYes = "Yes";
    private const string ConsentNo = "No";

    private readonly IReadOnlyList<SkillDefinition> _skills;
    private readonly SkillSessionState _state;
    private readonly string _inputSchemaJson;
    private readonly string _description;
    private readonly ILogger _logger;
    private readonly SkillOriginGate? _originGate;

    /// <summary>
    /// Initializes a new <see cref="SkillTool"/> with the provided <paramref name="modelInvocableSkills"/>
    /// (must be pre-filtered to exclude <c>disable-model-invocation: true</c> skills) and session state.
    /// </summary>
    /// <param name="modelInvocableSkills">Skills the model may invoke; must be non-empty.</param>
    /// <param name="state">Per-session state that tracks loaded bodies and reattach content.</param>
    /// <param name="descriptionCap">Combined character cap for the description catalogue; injectable for tests.</param>
    /// <param name="logger">Logger for fork-degradation and consent-denial messages; null uses NullLogger.</param>
    /// <param name="originGate">
    /// Optional gate that controls whether the model may load skills from external origins
    /// (<see cref="SkillOrigin.Claude"/> and <see cref="SkillOrigin.Plugin"/>). When
    /// <see langword="null"/>, no origin gating is applied (backward-compatible default).
    /// </param>
    public SkillTool(
        IReadOnlyList<SkillDefinition> modelInvocableSkills,
        SkillSessionState state,
        int descriptionCap = DefaultDescriptionCap,
        ILogger? logger = null,
        SkillOriginGate? originGate = null)
    {
        ArgumentNullException.ThrowIfNull(modelInvocableSkills);
        ArgumentNullException.ThrowIfNull(state);

        this._skills = modelInvocableSkills;
        this._state = state;
        this._logger = logger ?? NullLogger.Instance;
        this._originGate = originGate;
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
    /// out any with <c>disable-model-invocation: true</c> and, when <paramref name="workingDirectory"/>
    /// is provided, any whose <c>paths</c> glob list does not match the current workspace.
    /// Returns <see langword="null"/> when the filtered set is empty.
    /// </summary>
    /// <param name="allSkills">All discovered skills.</param>
    /// <param name="state">Per-session state.</param>
    /// <param name="workingDirectory">
    /// Current workspace directory used for <c>paths</c> glob filtering. Null disables path
    /// filtering (all skills pass). User-invoked skills are never filtered by this caller.
    /// </param>
    /// <param name="descriptionCap">Combined character cap for the description catalogue.</param>
    /// <param name="logger">Optional logger; null uses NullLogger.</param>
    /// <param name="originGate">
    /// Optional gate controlling whether the model may load skills from external origins.
    /// See <see cref="SkillOriginGate"/>. When <see langword="null"/>, no origin gating is applied.
    /// </param>
    public static SkillTool? CreateOrNull(
        IReadOnlyList<SkillDefinition> allSkills,
        SkillSessionState state,
        string? workingDirectory = null,
        int descriptionCap = DefaultDescriptionCap,
        ILogger? logger = null,
        SkillOriginGate? originGate = null)
    {
        ArgumentNullException.ThrowIfNull(allSkills);
        ArgumentNullException.ThrowIfNull(state);

        var modelInvocable = allSkills
            .Where(s => !s.DisableModelInvocation)
            .Where(s => workingDirectory is null || SkillPathMatcher.IsMatch(s.Paths, workingDirectory))
            .ToList();

        return modelInvocable.Count > 0
            ? new SkillTool(modelInvocable, state, descriptionCap, logger, originGate)
            : null;
    }

    /// <inheritdoc/>
    public async Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        var name = input.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return new ToolResult("Missing required 'name' argument.", IsError: true);
        }

        var skill = this._skills.FirstOrDefault(
            s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
        {
            var valid = string.Join(", ", this._skills.Select(s => s.Name));
            return new ToolResult($"Unknown skill '{name}'. Valid options: {valid}", IsError: true);
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

        // ── Origin trust gate ──────────────────────────────────────────────
        // Claude and Plugin origin skills require per-session approval before the model
        // may load them. Project and User skills are trusted without a prompt.
        if (this._originGate is not null)
        {
            var permitted = await this._originGate.MayLoadAsync(skill, cancellationToken)
                .ConfigureAwait(false);
            if (!permitted)
            {
                return new ToolResult(
                    $"Skill '{skill.Name}' requires approval before the model may invoke it. " +
                    $"Run /skill {skill.Name} to be prompted, or ask the user to approve it interactively.",
                    IsError: false);
            }
        }

        // ── Directory consent ──────────────────────────────────────────────
        var directoryConsented = await this.ResolveDirectoryConsentAsync(
            skill, context, cancellationToken).ConfigureAwait(false);

        var bodyWithNote = directoryConsented.Granted
            ? body
            : directoryConsented.DenialNote is { } note
                ? body + "\n\n" + note
                : body;

        // ── Fork / inline dispatch ─────────────────────────────────────────
        if (skill.ContextMode == SkillContextMode.Fork)
        {
            return await this.ExecuteForkAsync(skill, bodyWithNote, context, cancellationToken)
                .ConfigureAwait(false);
        }

        return this.ExecuteInline(skill, bodyWithNote);
    }

    // ── Inline execution ───────────────────────────────────────────────────

    private ToolResult ExecuteInline(SkillDefinition skill, string body)
    {
        var (_, content) = this._state.TryLoad(skill.Name, body);
        var shapeDelta = SkillTurnShapeComposer.BuildSkillDelta(skill);
        return new ToolResult(content) { ShapeDelta = shapeDelta };
    }

    // ── Fork execution ─────────────────────────────────────────────────────

    private async Task<ToolResult> ExecuteForkAsync(
        SkillDefinition skill,
        string body,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        // Degrade to inline when at max depth (spec: must degrade, not fail).
        if (context.CurrentDepth >= context.MaxSubagentDepth)
        {
            this.LogForkDegraded(skill.Name, context.MaxSubagentDepth);
            var inlineNote = $"[Note: Skill '{skill.Name}' could not fork (max subagent depth reached); running inline.]\n\n";
            var (_, content) = this._state.TryLoad(skill.Name, body);
            var inlineDelta = SkillTurnShapeComposer.BuildSkillDelta(skill);
            return new ToolResult(inlineNote + content) { ShapeDelta = inlineDelta };
        }

        // Degrade to inline when Tasks or Sink is unavailable (safety net for tests / limited contexts).
        if (context.Tasks is null || context.Sink is null)
        {
            var inlineNote = $"[Note: Skill '{skill.Name}' could not fork (Tasks or Sink unavailable); running inline.]\n\n";
            var (_, content) = this._state.TryLoad(skill.Name, body);
            var inlineDelta = SkillTurnShapeComposer.BuildSkillDelta(skill);
            return new ToolResult(inlineNote + content) { ShapeDelta = inlineDelta };
        }

        // Build the subagent's restriction: inherit parent restriction AND apply skill's own delta
        // so the subagent is monotonically at least as restricted as the parent turn.
        var skillDelta = SkillTurnShapeComposer.BuildSkillDelta(skill);
        var subagentRestriction = TurnShape.Layer(context.ParentToolRestriction, skillDelta);

        var subagentType = skill.AgentType ?? "general-purpose";
        var description = $"Skill: {skill.Name}";

        var report = await context.Tasks.RunSubagentForegroundAsync(
            context.Subagents!,
            subagentType,
            body,
            description,
            context.Sink,
            context.CurrentTaskId,
            parentActivity: context.ToolActivity,
            parentRestriction: subagentRestriction,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Forked skills do not affect the parent turn's shape — only the subagent is restricted.
        return new ToolResult(report);
    }

    // ── Directory consent ──────────────────────────────────────────────────

    private async Task<(bool Granted, string? DenialNote)> ResolveDirectoryConsentAsync(
        SkillDefinition skill,
        ToolContext context,
        CancellationToken cancellationToken)
    {
        if (skill.SourcePath is not { } sourcePath)
        {
            // No file-system origin — no directory to consent to.
            return (true, null);
        }

        var dirName = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(dirName))
        {
            return (true, null);
        }

        // Traversal check: a SourcePath containing ".." must never be used to widen access.
        if (!IsTraversalSafe(sourcePath))
        {
            return (false, "[Directory access denied: the skill's source path contains path traversal and cannot be used.]");
        }

        var canonicalDir = Path.GetFullPath(dirName);

        // Already consented this session — no re-prompt.
        if (this._state.HasDirectoryConsent(skill.Name))
        {
            return (true, null);
        }

        // Unattended context: §8.2 rule — never auto-grant in headless/unattended contexts.
        if (context.UserQuestion is null)
        {
            this.LogDirectoryConsentDeniedUnattended(skill.Name);
            return (false, $"[Directory access not granted: no interactive user available. Bundled resources at '{canonicalDir}' are not accessible this session.]");
        }

        // Prompt the user. The question names the directory so granting is an informed decision.
        var question = $"Skill '{skill.Name}' wants to make its directory available to the agent:\n  {canonicalDir}\n\nGrant access?";
        var answer = await context.UserQuestion
            .AskAsync(question, [ConsentYes, ConsentNo], multiSelect: false, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(answer, ConsentYes, StringComparison.OrdinalIgnoreCase))
        {
            this._state.GrantDirectoryConsent(skill.Name, canonicalDir);
            return (true, null);
        }

        return (false, $"[Directory access denied by user. Bundled resources at '{canonicalDir}' are not accessible this session.]");
    }

    /// <summary>
    /// Returns <see langword="false"/> when any path segment in <paramref name="path"/> is
    /// <c>..</c> — indicating a traversal attempt that must not be used to widen file access.
    /// </summary>
    private static bool IsTraversalSafe(string path)
    {
        var segments = path.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
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

    // ── Logger messages ────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skill '{SkillName}' fork degraded to inline: MaxSubagentDepth ({MaxDepth}) reached")]
    private partial void LogForkDegraded(string skillName, int maxDepth);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skill '{SkillName}' directory access not granted: unattended context (no interactive user available)")]
    private partial void LogDirectoryConsentDeniedUnattended(string skillName);
}
