using System.Text;

namespace Coda.Agent.Tasks;

/// <summary>
/// Foreground-shell stdout/stderr capture with an atomic disable/snapshot seam. While a shell
/// runs in the foreground its per-stream output is accumulated here so the caller can return the
/// exact stdout/stderr. The moment the shell is promoted to the background (detach wins),
/// <see cref="DisableAndSnapshot"/> atomically snapshots the captured output for the returned
/// <c>ShellRunResult</c> and stops capturing, releasing the buffers — so a high-output detached
/// shell cannot grow capture memory in its background finalizer while its pumps keep streaming
/// into the ring/log.
/// </summary>
internal sealed class ShellOutputCapture
{
    private readonly object _gate = new();
    private StringBuilder _stdout = new();
    private StringBuilder _stderr = new();
    private bool _capturing = true;

    /// <summary>Appends a stdout chunk while capturing; a no-op once capture is disabled.</summary>
    public void AppendStdout(string chunk)
    {
        lock (_gate)
        {
            if (_capturing) _stdout.Append(chunk);
        }
    }

    /// <summary>Appends a stderr chunk while capturing; a no-op once capture is disabled.</summary>
    public void AppendStderr(string chunk)
    {
        lock (_gate)
        {
            if (_capturing) _stderr.Append(chunk);
        }
    }

    /// <summary>The stdout captured so far (empty once disabled).</summary>
    public string Stdout
    {
        get { lock (_gate) { return _stdout.ToString(); } }
    }

    /// <summary>The stderr captured so far (empty once disabled).</summary>
    public string Stderr
    {
        get { lock (_gate) { return _stderr.ToString(); } }
    }

    /// <summary>
    /// Atomically snapshots the current stdout/stderr, stops capturing, and releases the buffers.
    /// Idempotent: after the first call the capture is empty and further appends are dropped, so a
    /// second call returns empty strings.
    /// </summary>
    public (string Stdout, string Stderr) DisableAndSnapshot()
    {
        lock (_gate)
        {
            var stdout = _stdout.ToString();
            var stderr = _stderr.ToString();
            _capturing = false;
            // Replace (not just Clear) so the retained buffer capacity is released to the GC and a
            // detached, high-output shell cannot pin large capture memory.
            _stdout = new StringBuilder();
            _stderr = new StringBuilder();
            return (stdout, stderr);
        }
    }

    /// <summary>Test seam: total characters currently retained across both buffers.</summary>
    internal int RetainedCharCount
    {
        get { lock (_gate) { return _stdout.Length + _stderr.Length; } }
    }
}
