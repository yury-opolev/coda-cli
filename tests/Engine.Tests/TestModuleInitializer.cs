using System.Runtime.CompilerServices;
using Coda.Sdk;

namespace Engine.Tests;

internal static class TestModuleInitializer
{
    /// <summary>
    /// <see cref="ModelCatalog.Default"/> normally prefers <c>~/.coda/cache/models.json</c> — a live
    /// catalog cached on the developer's machine — which makes catalog-derived assertions
    /// non-deterministic: when a model's real output limit drifts upstream (e.g. claude-sonnet-4-6
    /// 64000 → 128000) the cached value breaks "bundled value" tests LOCALLY while CI (which has no
    /// cache) stays green. Force the whole test run to skip the cache so <see cref="ModelCatalog.Default"/>
    /// resolves to the BUNDLED snapshot deterministically — via an env flag honored inside
    /// <see cref="ModelCatalog.Load"/>, so it survives a <see cref="ModelCatalog.ResetDefault"/> (which
    /// <see cref="ModelCatalog.RefreshAsync(System.Net.Http.HttpClient, System.Threading.CancellationToken)"/>
    /// triggers) that a one-shot pin would not.
    /// </summary>
    [ModuleInitializer]
    internal static void PinBundledModelCatalog()
    {
        System.Environment.SetEnvironmentVariable(ModelCatalog.SkipCacheEnv, "1");
        ModelCatalog.ResetDefault();
    }
}
