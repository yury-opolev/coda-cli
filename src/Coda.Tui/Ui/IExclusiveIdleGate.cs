namespace Coda.Tui.Ui;

/// <summary>Coordinates mutations that require an atomically idle interactive session.</summary>
internal interface IExclusiveIdleGate
{
    bool IsBusy { get; }

    event Action? Changed;

    IDisposable? TryAcquire();
}
