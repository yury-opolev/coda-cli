using Coda.Agent.Lsp;

namespace Engine.Tests.Lsp;

public sealed class LspDiagnosticRegistryTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static LspDiagnostic MakeDiagnostic(
        string message,
        LspDiagnosticSeverity severity = LspDiagnosticSeverity.Error,
        int startLine = 0,
        int startChar = 0,
        int endLine = 0,
        int endChar = 5)
    {
        var range = new LspRange(
            new LspPosition(startLine, startChar),
            new LspPosition(endLine, endChar));
        return new LspDiagnostic(message, severity, range, null, null);
    }

    private static DiagnosticFile MakeFile(string uri, params LspDiagnostic[] diagnostics)
    {
        return new DiagnosticFile(uri, diagnostics.ToList());
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ClearDeliveredForFile_matches_file_uri_against_local_path_key()
    {
        // The server publishes diagnostics keyed by a file:// URI; the agent loop clears
        // them using the local filesystem path. The registry must treat both as the same file.
        var registry = new LspDiagnosticRegistry();
        var path = Path.Combine(Path.GetTempPath(), "lsp-canon", "foo.ts");
        var uri = new Uri(path).AbsoluteUri;
        var diag = MakeDiagnostic("error: undefined symbol");

        registry.RegisterPending("ts", [MakeFile(uri, diag)]);
        Assert.Single(registry.CheckForDiagnostics());

        // Same diagnostic again → suppressed as already delivered.
        registry.RegisterPending("ts", [MakeFile(uri, diag)]);
        Assert.Empty(registry.CheckForDiagnostics());

        // Clearing by the LOCAL PATH (not the URI) must re-surface it.
        registry.ClearDeliveredForFile(path);
        registry.RegisterPending("ts", [MakeFile(uri, diag)]);
        Assert.Single(registry.CheckForDiagnostics());
    }

    [Fact]
    public void Dedupes_identical_diagnostics_within_a_batch()
    {
        var registry = new LspDiagnosticRegistry();
        var diag = MakeDiagnostic("error: undefined symbol");
        var file = new DiagnosticFile("file:///foo.ts", [diag, diag]);

        registry.RegisterPending("tsserver", [file]);
        var results = registry.CheckForDiagnostics();

        var resultDiags = Assert.Single(results).Diagnostics;
        Assert.Single(resultDiags);
    }

    [Fact]
    public void Does_not_redeliver_same_diagnostic_across_turns()
    {
        var registry = new LspDiagnosticRegistry();
        var diag = MakeDiagnostic("error: undefined symbol");
        var file = MakeFile("file:///foo.ts", diag);

        registry.RegisterPending("tsserver", [file]);
        registry.CheckForDiagnostics();

        // Second turn — same diagnostic
        registry.RegisterPending("tsserver", [file]);
        var second = registry.CheckForDiagnostics();

        Assert.Empty(second);
    }

    [Fact]
    public void Redelivers_after_ClearDeliveredForFile()
    {
        var registry = new LspDiagnosticRegistry();
        var diag = MakeDiagnostic("error: undefined symbol");
        var file = MakeFile("file:///foo.ts", diag);

        registry.RegisterPending("tsserver", [file]);
        registry.CheckForDiagnostics();

        registry.ClearDeliveredForFile("file:///foo.ts");

        registry.RegisterPending("tsserver", [file]);
        var second = registry.CheckForDiagnostics();

        var resultFile = Assert.Single(second);
        Assert.Single(resultFile.Diagnostics);
    }

    [Fact]
    public void Caps_at_10_per_file()
    {
        var registry = new LspDiagnosticRegistry();
        var diagnostics = Enumerable.Range(0, 15)
            .Select(i => MakeDiagnostic($"error {i}", startLine: i))
            .ToList();
        var file = new DiagnosticFile("file:///foo.ts", diagnostics);

        registry.RegisterPending("tsserver", [file]);
        var results = registry.CheckForDiagnostics();

        var resultFile = Assert.Single(results);
        Assert.True(resultFile.Diagnostics.Count <= 10);
    }

    [Fact]
    public void Caps_at_30_total()
    {
        var registry = new LspDiagnosticRegistry();
        // 3 files × 20 distinct diagnostics = 60 total
        var files = Enumerable.Range(0, 3)
            .Select(fileIdx =>
            {
                var diagnostics = Enumerable.Range(0, 20)
                    .Select(i => MakeDiagnostic($"error {fileIdx}-{i}", startLine: i))
                    .ToList();
                return new DiagnosticFile($"file:///file{fileIdx}.ts", diagnostics);
            })
            .ToList();

        registry.RegisterPending("tsserver", files);
        var results = registry.CheckForDiagnostics();

        var total = results.Sum(f => f.Diagnostics.Count);
        Assert.True(total <= 30, $"Expected <= 30 but got {total}");
    }

    [Fact]
    public void Sorts_errors_before_warnings()
    {
        var registry = new LspDiagnosticRegistry();
        var warning = MakeDiagnostic("warning msg", LspDiagnosticSeverity.Warning, startLine: 0);
        var error = MakeDiagnostic("error msg", LspDiagnosticSeverity.Error, startLine: 1);
        // Register with Warning first, then Error
        var file = new DiagnosticFile("file:///foo.ts", [warning, error]);

        registry.RegisterPending("tsserver", [file]);
        var results = registry.CheckForDiagnostics();

        var resultFile = Assert.Single(results);
        Assert.Equal(LspDiagnosticSeverity.Error, resultFile.Diagnostics[0].Severity);
        Assert.Equal(LspDiagnosticSeverity.Warning, resultFile.Diagnostics[1].Severity);
    }

    [Fact]
    public void Lru_evicts_after_500_files()
    {
        var registry = new LspDiagnosticRegistry();
        var diag = MakeDiagnostic("some error");

        // Deliver diagnostics for 500 distinct URIs (uri0 through uri499)
        for (var i = 0; i < 500; i++)
        {
            var f = MakeFile($"file:///uri{i}.ts", diag);
            registry.RegisterPending("tsserver", [f]);
            registry.CheckForDiagnostics();
        }

        // Deliver a 501st URI — this should evict uri0
        var file501 = MakeFile("file:///uri500.ts", diag);
        registry.RegisterPending("tsserver", [file501]);
        registry.CheckForDiagnostics();

        // Re-register uri0's identical diagnostic — LRU eviction means it can re-deliver
        var fileUri0Again = MakeFile("file:///uri0.ts", diag);
        registry.RegisterPending("tsserver", [fileUri0Again]);
        var redelivered = registry.CheckForDiagnostics();

        Assert.NotEmpty(redelivered);
    }

    [Fact]
    public void PendingCount_reflects_registrations()
    {
        var registry = new LspDiagnosticRegistry();
        Assert.Equal(0, registry.PendingCount);

        var file1 = MakeFile("file:///a.ts", MakeDiagnostic("a"));
        registry.RegisterPending("tsserver", [file1]);
        Assert.Equal(1, registry.PendingCount);

        var file2 = MakeFile("file:///b.ts", MakeDiagnostic("b"));
        registry.RegisterPending("tsserver", [file2]);
        Assert.Equal(2, registry.PendingCount);

        registry.CheckForDiagnostics();
        Assert.Equal(0, registry.PendingCount);
    }

    [Fact]
    public void ResetAll_clears_pending_and_delivered()
    {
        var registry = new LspDiagnosticRegistry();
        var diag = MakeDiagnostic("error: undefined symbol");
        var file = MakeFile("file:///foo.ts", diag);

        registry.RegisterPending("tsserver", [file]);
        registry.CheckForDiagnostics();

        // ResetAll should clear delivered tracking too
        registry.ResetAll();

        Assert.Equal(0, registry.PendingCount);

        // Re-register the same diagnostic — should re-deliver since delivered tracking cleared
        registry.RegisterPending("tsserver", [file]);
        var results = registry.CheckForDiagnostics();

        Assert.NotEmpty(results);
    }
}
