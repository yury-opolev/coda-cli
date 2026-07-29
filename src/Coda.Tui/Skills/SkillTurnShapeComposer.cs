using Coda.Agent;

namespace Coda.Tui.Skills;

/// <summary>
/// Builds the <see cref="TurnShape"/> delta that a skill contributes to the current turn.
/// Pure and separately testable — does not interact with any runtime state.
/// </summary>
public static class SkillTurnShapeComposer
{
    /// <summary>
    /// Maps the Phase 2 capability fields of <paramref name="skill"/> to a <see cref="TurnShape"/>
    /// delta. Returns <see langword="null"/> when the skill carries no shape-relevant capabilities
    /// (i.e. the empty delta is equivalent to no override).
    /// </summary>
    /// <remarks>
    /// The returned shape is a <em>delta</em>, not a final resolved shape. Callers must pass it
    /// through <see cref="TurnShape.Layer"/> to compose it with any existing
    /// hook-imposed shape. Composition rules:
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="TurnShape.PreApprovedTools"/> — skill's <c>allowed-tools</c> produces
    ///     pre-approval only (no restriction); composed by union so approval can only grow.
    ///   </item>
    ///   <item>
    ///     <see cref="TurnShape.DeniedTools"/> — unioned with any existing denial list so the
    ///     set of denied tools can only grow.
    ///   </item>
    ///   <item>
    ///     <see cref="TurnShape.Model"/>/<see cref="TurnShape.Effort"/> — skill wins (last-write)
    ///     when set; <c>"inherit"</c> in frontmatter is normalised to <see langword="null"/> by
    ///     the parser before reaching here, so null always means "no override".
    ///   </item>
    /// </list>
    /// </remarks>
    public static TurnShape? BuildSkillDelta(SkillDefinition skill)
    {
        var hasAllowed = skill.AllowedTools.Count > 0;
        var hasDenied = skill.DisallowedTools.Count > 0;
        var hasModel = skill.Model is not null;
        var hasEffort = skill.Effort is not null;

        if (!hasAllowed && !hasDenied && !hasModel && !hasEffort)
        {
            return null;
        }

        return new TurnShape
        {
            // Skill's allowed-tools pre-approves only; it does NOT restrict the tool set.
            // Restriction belongs to hook-imposed AllowedTools only.
            PreApprovedTools = hasAllowed ? skill.AllowedTools : null,
            DeniedTools = hasDenied ? skill.DisallowedTools : null,
            Model = skill.Model,
            Effort = skill.Effort,
        };
    }
}
