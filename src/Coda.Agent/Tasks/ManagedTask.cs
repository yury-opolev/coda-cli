namespace Coda.Agent.Tasks;

/// <summary>
/// One live unit of work (subagent or shell). Owns a cancellation source,
/// a monotonic version, and its lifecycle status. Extended in later tasks with
/// an output ring, steering inbox, and OS process.
/// </summary>
internal sealed class ManagedTask : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly OutputRing _output;
    private readonly Action<ManagedTask>? _onTerminal;
    private long _version;
    private TaskRunStatus _status = TaskRunStatus.Running;
    private DateTimeOffset? _endedAt;
    private string? _result;
    private string? _error;

    internal ManagedTask(
        string id,
        string? parentId,
        int depth,
        TaskKind kind,
        string description,
        string logPath,
        long outputRingBytes,
        Action<ManagedTask>? onTerminal = null)
    {
        Id = id;
        ParentId = parentId;
        Depth = depth;
        Kind = kind;
        Description = description;
        LogPath = logPath;
        StartedAt = DateTimeOffset.UtcNow;
        _output = new OutputRing(outputRingBytes);
        _onTerminal = onTerminal;
    }

    public string Id { get; }
    public string? ParentId { get; }
    public int Depth { get; }
    public TaskKind Kind { get; }
    public string Description { get; }
    public string LogPath { get; }
    public DateTimeOffset StartedAt { get; }

    /// <summary>Cancellation token for the underlying work. Signalled by Cancel().</summary>
    public CancellationToken Token => _cts.Token;

    public long Version { get { lock (_gate) { return _version; } } }
    public TaskRunStatus Status { get { lock (_gate) { return _status; } } }

    /// <summary>Requests cancellation of the underlying work without changing status.</summary>
    internal void Cancel()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed; ignore */ }
    }

    // Compatibility bool overloads. The out-version overloads are authoritative: they
    // report the EXACT version assigned by the transition so the manager can publish that
    // precise value rather than re-reading a later, possibly-advanced version.
    internal bool TryComplete(string? result) => TryComplete(result, out _);
    internal bool TryFail(string? error) => TryFail(error, out _);
    internal bool TryStop() => TryStop(out _);

    internal bool TryComplete(string? result, out long version) =>
        Transition(TaskRunStatus.Completed, result, error: null, out version);
    internal bool TryFail(string? error, out long version) =>
        Transition(TaskRunStatus.Failed, result: null, error, out version);
    internal bool TryStop(out long version) =>
        Transition(TaskRunStatus.Stopped, result: null, error: null, out version);

    private bool Transition(TaskRunStatus next, string? result, string? error, out long version)
    {
        lock (_gate)
        {
            if (_status != TaskRunStatus.Running)
            {
                // Already terminal: report the current (unchanged) version and refuse.
                version = _version;
                return false;
            }

            _status = next;
            _result = result;
            _error = error;
            _endedAt = DateTimeOffset.UtcNow;
            version = ++_version;
        }

        // Invoke the manager-supplied terminal hook OUTSIDE the task lock so that log
        // flushing (disk I/O) and registry mutation never run while this task's gate is
        // held, avoiding lock-ordering deadlocks with registry readers.
        try { _onTerminal?.Invoke(this); }
        catch { /* best-effort; a hook failure must never surface from a transition. */ }

        return true;
    }

    public TaskSnapshot ToSnapshot()
    {
        lock (_gate)
        {
            return new TaskSnapshot(
                Id, ParentId, Depth, Kind, Description,
                _status, _version, StartedAt, _endedAt, LogPath, _result, _error);
        }
    }

    /// <summary>
    /// Appends output and, if it happened, bumps the version and returns the EXACT version
    /// assigned to this append. Returns <c>null</c> — a complete no-op — when the text is
    /// empty/null or the task is already terminal: no version bump, no ring write. Output and
    /// version assignment happen under the same lock so the returned version is authoritative.
    /// </summary>
    public long? TryAppend(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        lock (_gate)
        {
            // Reject output once terminal so status ordering stays clean: no append can bump
            // the version after the final Completed/Failed/Stopped transition.
            if (_status != TaskRunStatus.Running) return null;
            _output.Append(text);
            return ++_version;
        }
    }

    /// <summary>Reads output at or after the absolute cursor. See OutputRing.ReadFrom.</summary>
    public (string Text, long NextCursor, bool Truncated) ReadIncremental(long cursor) =>
        _output.ReadFrom(cursor);

    /// <summary>Returns the last maxChars characters of buffered output.</summary>
    public string Peek(int maxChars) => _output.Peek(maxChars);

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
    }
}
