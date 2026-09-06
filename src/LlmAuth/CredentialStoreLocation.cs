namespace LlmAuth;

/// <summary>
/// Resolves the on-disk directory for credential stores and migrates credentials
/// from the legacy location on first use.
///
/// <para>Default (new): <c>~/.coda/credentials</c> — keeps all of Coda's own state
/// under <c>~/.coda</c>, separate from the Claude CLI's <c>~/.claude</c>.</para>
/// <para>Legacy: <c>%APPDATA%\LlmAuth</c> on Windows (and <c>~/.config/LlmAuth</c>
/// on other OSes), used before credentials were moved under <c>~/.coda</c>.
/// Existing credentials are migrated automatically and the legacy folder removed.</para>
/// </summary>
public static class CredentialStoreLocation
{
    /// <summary>The current default credential directory: <c>~/.coda/credentials</c>.</summary>
    /// <remarks>
    /// Honors the <c>CODA_HOME</c> profile-root override (then the legacy
    /// <c>CODA_SETTINGS_DIR</c>), so credentials are redirected together with the
    /// rest of the profile under an isolated home. Setting <c>USERPROFILE</c>
    /// does not work on Windows — <see cref="Environment.SpecialFolder.UserProfile"/>
    /// resolves via the user token, not the environment.
    /// </remarks>
    public static string Default => Path.Combine(
        HomeDirectory,
        ".coda",
        "credentials");

    /// <summary>
    /// The profile root: <c>CODA_HOME</c>, then <c>CODA_SETTINGS_DIR</c>, then the OS user profile.
    /// Mirrors <c>Coda.Common.CodaPaths.HomeDirectory</c> (duplicated here because
    /// <c>LlmAuth</c> is a leaf assembly with no reference to <c>Coda.Common</c>).
    /// </summary>
    private static string HomeDirectory
    {
        get
        {
            var codaHome = Environment.GetEnvironmentVariable("CODA_HOME");
            if (!string.IsNullOrEmpty(codaHome))
            {
                return codaHome;
            }

            var settingsDir = Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR");
            if (!string.IsNullOrEmpty(settingsDir))
            {
                return settingsDir;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

    /// <summary>The pre-<c>~/.coda</c> credential directory credentials are migrated from.</summary>
    public static string Legacy => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LlmAuth");

    /// <summary>
    /// Returns the resolved default credential directory, migrating credentials from
    /// the legacy location on first run. Best-effort: migration failures are swallowed
    /// so a fresh store is always usable.
    /// </summary>
    public static string ResolveDefault()
    {
        var explicitProfile =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODA_HOME")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODA_SETTINGS_DIR"));
        return ResolveDefault(Legacy, Default, explicitProfile);
    }

    internal static string ResolveDefault(string legacy, string target, bool explicitProfile)
    {
        // An explicitly isolated profile must never import or delete the user's
        // legacy credentials, even when its new credentials directory is empty.
        if (explicitProfile)
        {
            Directory.CreateDirectory(target);
        }
        else
        {
            Migrate(legacy, target);
        }
        return target;
    }

    /// <summary>
    /// Migrate credential files from <paramref name="legacy"/> to <paramref name="target"/>.
    /// No-op when the target already holds credentials, when the paths are the same, or
    /// when the legacy directory is missing/empty. After a successful copy the legacy
    /// directory is removed (so credentials live in one place). Idempotent.
    /// </summary>
    public static void Migrate(string legacy, string target)
    {
        // Already on the new layout — never touch existing credentials.
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            return;
        }

        Directory.CreateDirectory(target);

        if (string.Equals(legacy, target, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(legacy))
        {
            return;
        }

        var copiedAny = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(legacy))
            {
                var destination = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(destination))
                {
                    File.Copy(file, destination);
                    copiedAny = true;
                }
            }

            // Remove the legacy folder only after a clean copy, so credentials are never
            // left duplicated across two locations.
            if (copiedAny)
            {
                Directory.Delete(legacy, recursive: true);
            }
        }
        catch
        {
            // Best-effort: a fresh (possibly empty) target directory still works.
        }
    }
}
