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
        long outputRingBytes)
    {
        Id = id;
        ParentId = parentId;
        Depth = depth;
        Kind = kind;
        Description = description;
        LogPath = logPath;
        StartedAt = DateTimeOffset.UtcNow;
        _output = new OutputRing(outputRingBytes);
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

    internal bool TryComplete(string? result) => Transition(TaskRunStatus.Completed, result, error: null);
    internal bool TryFail(string? error) => Transition(TaskRunStatus.Failed, result: null, error);
    internal bool TryStop() => Transition(TaskRunStatus.Stopped, result: null, error: null);

    private bool Transition(TaskRunStatus next, string? result, string? error)
    {
        lock (_gate)
        {
            if (_status != TaskRunStatus.Running)
            {
                return false;
            }

            _status = next;
            _result = result;
            _error = error;
            _endedAt = DateTimeOffset.UtcNow;
            _version++;
            return true;
        }
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

    /// <summary>Appends output and bumps the version so subscribers observe progress.</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _output.Append(text);
        lock (_gate)
        {
            _version++;
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
