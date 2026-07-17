using Coda.Tui.Ui.Events;

namespace Coda.Tui.Tests;

/// <summary>Test double that records every published <see cref="UiEvent"/> in order.</summary>
internal sealed class RecordingUiEvents : IUiEventPublisher
{
    private readonly List<UiEvent> events = new();

    public IReadOnlyList<UiEvent> Events => this.events;

    public void Publish(UiEvent uiEvent) => this.events.Add(uiEvent);
}
