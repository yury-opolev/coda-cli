namespace Coda.Tui.Ui.Events;

/// <summary>Synchronously publishes <see cref="UiEvent"/>s to the UI layer.</summary>
public interface IUiEventPublisher
{
    /// <summary>Publish a single event.</summary>
    void Publish(UiEvent uiEvent);
}

/// <summary>An <see cref="IUiEventPublisher"/> that drops every event; useful for headless/tests.</summary>
public sealed class NullUiEventPublisher : IUiEventPublisher
{
    /// <summary>The shared no-op instance.</summary>
    public static NullUiEventPublisher Instance { get; } = new();

    private NullUiEventPublisher()
    {
    }

    /// <inheritdoc />
    public void Publish(UiEvent uiEvent)
    {
    }
}
