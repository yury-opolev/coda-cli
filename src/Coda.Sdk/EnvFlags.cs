namespace Coda.Sdk;

/// <summary>
/// Shared parsing of boolean-style environment variables. A variable is "on" when its value is
/// <c>"1"</c> or <c>"true"</c> (case-insensitive); null/empty/anything else is "off". Single
/// source of truth so every <c>CODA_*</c> toggle (e.g. <c>CODA_DISABLE_MODELS_FETCH</c>,
/// <c>CODA_SERVE_DISABLE_MCP</c>) honors the same convention.
/// </summary>
public static class EnvFlags
{
    /// <summary>True when <paramref name="value"/> is <c>"1"</c> or <c>"true"</c> (case-insensitive).</summary>
    public static bool IsTruthy(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
