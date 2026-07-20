namespace Coda.Agent.Tasks;

/// <summary>
/// Logical output channel for a task's streamed text. The persistent
/// <see cref="TaskLogWriter"/> keeps an <em>independent</em> streaming secret-redactor state per
/// channel, so a secret split across chunks on one channel is never corrupted — and therefore
/// never leaked — by interleaved chunks on another channel. The in-memory output ring remains a
/// single raw combined stream regardless of channel.
/// </summary>
public enum TaskOutputChannel
{
    /// <summary>Default channel for subagents and any non-shell output.</summary>
    General = 0,

    /// <summary>A managed shell's standard output.</summary>
    Stdout = 1,

    /// <summary>A managed shell's standard error.</summary>
    Stderr = 2,
}
