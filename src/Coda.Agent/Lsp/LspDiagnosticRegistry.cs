using System.Text.Json;

namespace Coda.Agent.Lsp;

/// <summary>
/// Stores LSP diagnostics received asynchronously from LSP servers via
/// textDocument/publishDiagnostics notifications.  One instance per Coda session.
///
/// Thread-safe: <see cref="RegisterPending"/> runs on the LSP connection read-loop
/// thread while the agent loop calls <see cref="CheckForDiagnostics"/> from its own
/// context, so all public methods serialise on an internal lock.
/// </summary>
public sealed class LspDiagnosticRegistry
{
    private const int MaxDiagnosticsPerFile = 10;
    private const int MaxTotalDiagnostics = 30;
    private const int MaxDeliveredFiles = 500;

    private readonly object gate = new();

    private sealed class PendingEntry
    {
        public required string ServerName { get; init; }
        public required IReadOnlyList<DiagnosticFile> Files { get; init; }
    }

    private readonly List<PendingEntry> pending = [];

    // LRU-bounded cross-turn delivered-key tracking.
    // Key: file URI.  Value: set of diagnostic keys already delivered for that file.
    private readonly LruDictionary<string, HashSet<string>> delivered =
        new(MaxDeliveredFiles);

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Number of undelivered pending entries.</summary>
    public int PendingCount
    {
        get
        {
            lock (this.gate)
            {
                return this.pending.Count;
            }
        }
    }

    /// <summary>
    /// Appends a new pending entry from <paramref name="serverName"/> with the
    /// given <paramref name="files"/>.
    /// </summary>
    public void RegisterPending(string serverName, IReadOnlyList<DiagnosticFile> files)
    {
        lock (this.gate)
        {
            this.pending.Add(new PendingEntry { ServerName = serverName, Files = files });
        }
    }

    /// <summary>
    /// Returns all new (undelivered) diagnostics after deduplication and volume
    /// limiting, then marks them delivered and removes the pending entries.
    /// </summary>
    public IReadOnlyList<DiagnosticFile> CheckForDiagnostics()
    {
        lock (this.gate)
        {
            return this.CheckForDiagnosticsLocked();
        }
    }

    private IReadOnlyList<DiagnosticFile> CheckForDiagnosticsLocked()
    {
        if (this.pending.Count == 0)
        {
            return [];
        }

        // --- Collect all files from all pending entries ---
        var allFiles = new List<DiagnosticFile>();
        foreach (var entry in this.pending)
        {
            allFiles.AddRange(entry.Files);
        }

        this.pending.Clear();

        // --- Deduplicate (within-batch + cross-turn) ---
        var batchSeen = new Dictionary<string, HashSet<string>>();
        var dedupedFiles = new List<DiagnosticFile>();

        foreach (var file in allFiles)
        {
            if (!batchSeen.TryGetValue(file.Uri, out var batchKeys))
            {
                batchKeys = [];
                batchSeen[file.Uri] = batchKeys;
            }

            this.delivered.TryGetValue(CanonicalKey(file.Uri), out var previouslyDelivered);

            var newDiagnostics = new List<LspDiagnostic>();
            foreach (var diag in file.Diagnostics)
            {
                var key = CreateDiagnosticKey(diag);
                if (batchKeys.Contains(key))
                {
                    continue;
                }

                if (previouslyDelivered is not null && previouslyDelivered.Contains(key))
                {
                    continue;
                }

                batchKeys.Add(key);
                newDiagnostics.Add(diag);
            }

            if (newDiagnostics.Count > 0)
            {
                dedupedFiles.Add(new DiagnosticFile(file.Uri, newDiagnostics));
            }
        }

        if (dedupedFiles.Count == 0)
        {
            return [];
        }

        // --- Volume limit: sort by severity, cap per file + cap total ---
        var result = new List<DiagnosticFile>();
        var totalCount = 0;

        foreach (var file in dedupedFiles)
        {
            if (totalCount >= MaxTotalDiagnostics)
            {
                break;
            }

            var sorted = file.Diagnostics
                .OrderBy(d => (int)d.Severity)
                .ToList();

            // Cap per file
            if (sorted.Count > MaxDiagnosticsPerFile)
            {
                sorted = sorted.Take(MaxDiagnosticsPerFile).ToList();
            }

            // Cap total
            var remaining = MaxTotalDiagnostics - totalCount;
            if (sorted.Count > remaining)
            {
                sorted = sorted.Take(remaining).ToList();
            }

            if (sorted.Count == 0)
            {
                break;
            }

            result.Add(new DiagnosticFile(file.Uri, sorted));
            totalCount += sorted.Count;
        }

        // --- Record delivered keys (cross-turn dedup) ---
        foreach (var file in result)
        {
            var fileKey = CanonicalKey(file.Uri);
            HashSet<string> deliveredKeys;
            if (!this.delivered.TryGetValue(fileKey, out var existing))
            {
                deliveredKeys = [];
                this.delivered.Set(fileKey, deliveredKeys);
            }
            else
            {
                deliveredKeys = existing!;
            }

            foreach (var diag in file.Diagnostics)
            {
                deliveredKeys.Add(CreateDiagnosticKey(diag));
            }
        }

        return result;
    }

    /// <summary>
    /// Removes the delivered-key set for <paramref name="uri"/> so that
    /// previously delivered diagnostics for that file will re-surface.
    /// </summary>
    public void ClearDeliveredForFile(string uri)
    {
        lock (this.gate)
        {
            this.delivered.Remove(CanonicalKey(uri));
        }
    }

    /// <summary>Clears all pending entries and delivered-key tracking.</summary>
    public void ResetAll()
    {
        lock (this.gate)
        {
            this.pending.Clear();
            this.delivered.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reduces a <c>file://</c> URI or a filesystem path to one stable key so that the
    /// delivered-tracking set matches regardless of whether the producer (the LSP server's
    /// publishDiagnostics URI) or the consumer (the agent loop's edited-file path) supplied it.
    /// Case is folded because Coda targets Windows, whose filesystem is case-insensitive.
    /// </summary>
    private static string CanonicalKey(string uriOrPath)
    {
        var path = uriOrPath;
        if (Uri.TryCreate(uriOrPath, UriKind.Absolute, out var parsed) && parsed.IsFile)
        {
            path = parsed.LocalPath;
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            // Keep the best-effort value when the path can't be resolved.
        }

        return OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;
    }

    private static string CreateDiagnosticKey(LspDiagnostic diag)
    {
        return JsonSerializer.Serialize(new
        {
            message = diag.Message,
            severity = diag.Severity,
            range = new
            {
                start = new { line = diag.Range.Start.Line, character = diag.Range.Start.Character },
                end = new { line = diag.Range.End.Line, character = diag.Range.End.Character },
            },
            source = diag.Source,
            code = diag.Code,
        });
    }
}
