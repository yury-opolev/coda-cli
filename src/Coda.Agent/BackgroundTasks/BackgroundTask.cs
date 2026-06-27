using System.Text;

namespace Coda.Agent.BackgroundTasks;

/// <summary>
/// Represents a running (or finished) background subagent task. Thread-safe.
/// </summary>
public sealed class BackgroundTask : IDisposable
{
    private readonly object gate = new();
    private readonly StringBuilder buffer = new();
    private readonly CancellationTokenSource cts;
    private BackgroundTaskStatus status = BackgroundTaskStatus.Running;
    private int readCursor;
    private string? finalResult;
    private string? errorMessage;

    public BackgroundTask(string id, CancellationTokenSource cts)
    {
        this.Id = id ?? throw new ArgumentNullException(nameof(id));
        this.cts = cts ?? throw new ArgumentNullException(nameof(cts));
    }

    public string Id { get; }

    public CancellationToken Token => this.cts.Token;

    public string? FinalResult
    {
        get
        {
            lock (this.gate)
            {
                return this.finalResult;
            }
        }
    }

    public string? ErrorMessage
    {
        get
        {
            lock (this.gate)
            {
                return this.errorMessage;
            }
        }
    }

    public BackgroundTaskStatus Status
    {
        get
        {
            lock (this.gate)
            {
                return this.status;
            }
        }
    }

    /// <summary>Append text to the output buffer.</summary>
    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (this.gate)
        {
            this.buffer.Append(text);
        }
    }

    /// <summary>
    /// Returns text appended since the last call and the current status.
    /// Advances the read cursor so the next call returns only new text.
    /// </summary>
    public (string NewText, BackgroundTaskStatus Status) ReadFromCursor()
    {
        lock (this.gate)
        {
            var full = this.buffer.ToString();
            var newText = full.Length > this.readCursor
                ? full[this.readCursor..]
                : string.Empty;
            this.readCursor = full.Length;
            return (newText, this.status);
        }
    }

    /// <summary>Mark the task as successfully completed.</summary>
    public void MarkCompleted(string result)
    {
        lock (this.gate)
        {
            this.finalResult = result;
            this.status = BackgroundTaskStatus.Completed;
        }
    }

    /// <summary>Mark the task as failed with an error message.</summary>
    public void MarkFailed(string error)
    {
        lock (this.gate)
        {
            this.errorMessage = error;
            this.status = BackgroundTaskStatus.Failed;
        }
    }

    /// <summary>Mark the task as stopped (cancelled).</summary>
    public void MarkStopped()
    {
        lock (this.gate)
        {
            this.status = BackgroundTaskStatus.Stopped;
        }
    }

    /// <summary>Cancel the task's cancellation token.</summary>
    public void Cancel()
    {
        this.cts.Cancel();
    }

    /// <inheritdoc/>
    public void Dispose() => this.cts.Dispose();
}
