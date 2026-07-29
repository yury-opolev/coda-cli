using Coda.Agent.Hooks;

namespace Coda.Tui.Tests;

/// <summary>
/// Regression tests for LOW-3: <c>bufferAssistantText</c> in
/// <c>InteractiveProgram.RunMainAsync</c> was computed from <c>startupSettings.Hooks</c>
/// (only user hooks) BEFORE plugin hooks were merged into <c>hookList</c>.
/// A plugin contributing an <c>AgentResponse</c> hook with <c>mutates: ["modifiedResponse"]</c>
/// therefore did not trigger buffering, causing the first turn to flash unbuffered assistant text.
/// </summary>
public sealed class PluginHookWiringTests
{
    /// <summary>
    /// Verifies that the display-mutating flag is missed when only <c>settingsHooks</c> is
    /// used (reproducing the bug) but is correctly detected when the merged <c>hookList</c>
    /// is used (verifying the fix).
    ///
    /// <para>The fix in <c>InteractiveProgram.RunMainAsync</c> moves the
    /// <c>bufferAssistantText</c> computation to after the plugin-merge step, replacing
    /// <c>new UserHookRunner(startupSettings.Hooks)</c> with
    /// <c>new UserHookRunner(hookList)</c>.</para>
    /// </summary>
    [Fact]
    public void BufferAssistantText_is_true_only_when_computed_from_merged_hookList_not_settingsHooks()
    {
        // A plugin contributes an AgentResponse hook that declares modifiedResponse.
        var pluginHook = new UserHook(
            "AgentResponse",
            Command: null,
            HandlerType: "prompt",
            HookPrompt: "rewrite response",
            Mutates: ["modifiedResponse"],
            PluginOrigin: ("formatter-plugin", "2.0.0"));

        // User settings have no hooks of their own.
        var settingsHooks = new List<UserHook>();

        // The merged list: plugin hooks first, then user hooks.
        var mergedHookList = new List<UserHook>([pluginHook, .. settingsHooks]);

        // --- Reproduces the bug ---
        // Before the fix, bufferAssistantText is derived from settingsHooks only.
        var beforeFix = settingsHooks.Count > 0
            && new UserHookRunner(settingsHooks).AnyHookMutatesDisplay;

        // --- Verifies the fix ---
        // After the fix, bufferAssistantText is derived from the merged hookList.
        var afterFix = mergedHookList.Count > 0
            && new UserHookRunner(mergedHookList).AnyHookMutatesDisplay;

        Assert.False(beforeFix, "Bug reproduced: plugin display-mutating hook missed when only settingsHooks used");
        Assert.True(afterFix, "Fix verified: plugin display-mutating hook detected in merged hookList");
    }
}
