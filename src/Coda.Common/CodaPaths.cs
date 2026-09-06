using System;
using System.IO;

namespace Coda.Common;

/// <summary>
/// Resolves the Coda profile root — the directory that contains <c>.coda</c> —
/// through a single, overridable seam so every serve-reachable reader of user
/// state (settings, the model-catalog cache, skills, plugins, credentials) can
/// be redirected together to an isolated profile.
/// </summary>
/// <remarks>
/// <para>
/// Setting <c>USERPROFILE</c>/<c>HOME</c> for a child process does NOT redirect
/// these reads on Windows: <see cref="Environment.SpecialFolder.UserProfile"/>
/// resolves via <c>SHGetKnownFolderPath</c>, which reads the user token, not the
/// environment. Verified empirically — it returned the real profile even with
/// <c>USERPROFILE</c> pointed elsewhere. The Rust engine's <c>directories</c>
/// crate has the same property. So the only reliable redirect is an explicit
/// application-level override, which this type provides.
/// </para>
/// <para>
/// Precedence: <c>CODA_HOME</c> (the profile root, matching the Rust engine and
/// the TUI), then the existing <c>CODA_SETTINGS_DIR</c> settings seam, then the
/// real user profile. A single <c>CODA_HOME</c> therefore isolates every reader
/// routed through here.
/// </para>
/// </remarks>
public static class CodaPaths
{
    /// <summary>The environment variable that overrides the profile root.</summary>
    public const string HomeEnv = "CODA_HOME";

    /// <summary>The legacy settings-root override, honored for backwards compatibility.</summary>
    public const string SettingsDirEnv = "CODA_SETTINGS_DIR";

    /// <summary>
    /// The profile root (the parent of <c>.coda</c>): <c>CODA_HOME</c>, then
    /// <c>CODA_SETTINGS_DIR</c>, then the OS user-profile directory.
    /// </summary>
    public static string HomeDirectory
    {
        get
        {
            var codaHome = Environment.GetEnvironmentVariable(HomeEnv);
            if (!string.IsNullOrEmpty(codaHome))
            {
                return codaHome;
            }

            var settingsDir = Environment.GetEnvironmentVariable(SettingsDirEnv);
            if (!string.IsNullOrEmpty(settingsDir))
            {
                return settingsDir;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    /// <summary>The <c>.coda</c> state directory under <see cref="HomeDirectory"/>.</summary>
    public static string CodaDirectory => Path.Combine(HomeDirectory, ".coda");
}
