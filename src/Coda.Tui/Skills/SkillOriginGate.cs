using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coda.Tui.Skills;

/// <summary>
/// Session-level gate that controls whether the model may load a skill body based on the
/// skill's <see cref="SkillDefinition.Origin"/>.
/// </summary>
/// <remarks>
/// <para>
/// Skills whose origin is <see cref="SkillOrigin.Project"/> or <see cref="SkillOrigin.User"/>
/// are trusted without a prompt — the user authored them.
/// </para>
/// <para>
/// Skills whose origin is <see cref="SkillOrigin.Claude"/> (<c>~/.claude/skills</c>) or
/// <see cref="SkillOrigin.Plugin"/> require explicit per-session approval before the model
/// may load their body. The gate prompts the user on first encounter and caches the decision
/// in the supplied <see cref="SkillSessionState"/> for the session's lifetime.
/// </para>
/// <para>
/// In unattended contexts (no prompt callback), the gate refuses and logs the refusal —
/// following the same unattended policy established for hooks in §8.2.
/// </para>
/// <para>
/// Explicit <c>/skill &lt;name&gt;</c> invocations by the user are never routed through this
/// gate: the user asking by name has already decided.
/// </para>
/// </remarks>
public sealed partial class SkillOriginGate
{
    private static readonly IReadOnlySet<SkillOrigin> TrustedOrigins =
        new HashSet<SkillOrigin> { SkillOrigin.Project, SkillOrigin.User };

    private readonly SkillSessionState _state;
    private readonly Func<SkillDefinition, CancellationToken, Task<bool>>? _promptCallback;
    private readonly ILogger _logger;

    /// <summary>
    /// Initialises the gate.
    /// </summary>
    /// <param name="state">Per-session state used to cache per-skill approvals.</param>
    /// <param name="promptCallback">
    /// Interactive callback that asks the user whether to allow the model to load a skill from
    /// an external origin. Returns <see langword="true"/> when the user grants approval.
    /// Pass <see langword="null"/> in headless / unattended contexts.
    /// </param>
    /// <param name="logger">Logger for refusal messages in unattended contexts.</param>
    public SkillOriginGate(
        SkillSessionState state,
        Func<SkillDefinition, CancellationToken, Task<bool>>? promptCallback = null,
        ILogger? logger = null)
    {
        this._state = state ?? throw new ArgumentNullException(nameof(state));
        this._promptCallback = promptCallback;
        this._logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the model is permitted to load the body of
    /// <paramref name="skill"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>Project / User origin: always permitted (trusted without a prompt).</item>
    ///   <item>Claude / Plugin origin with interactive prompt: prompt the user on first encounter;
    ///   cache the decision for the session.</item>
    ///   <item>Claude / Plugin origin without a prompt (unattended): refused; logged once.</item>
    /// </list>
    /// </remarks>
    public async Task<bool> MayLoadAsync(SkillDefinition skill, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skill);

        if (TrustedOrigins.Contains(skill.Origin))
        {
            return true;
        }

        if (this._state.HasOriginConsent(skill.Name))
        {
            return true;
        }

        if (this._promptCallback is not null)
        {
            var granted = await this._promptCallback(skill, ct).ConfigureAwait(false);
            if (granted)
            {
                this._state.GrantOriginConsent(skill.Name);
            }
            else
            {
                this.LogSkillDenied(skill.Name, skill.Origin.ToString());
            }

            return granted;
        }

        this.LogSkillUnattended(skill.Name, skill.Origin.ToString());
        return false;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skill '{skillName}' (origin: {origin}) was denied trust by the user — skipping")]
    private partial void LogSkillDenied(string skillName, string origin);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skill '{skillName}' (origin: {origin}) requires approval but no interactive user is available — skipping (unattended policy)")]
    private partial void LogSkillUnattended(string skillName, string origin);
}
