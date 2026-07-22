using Coda.Agent;

namespace Engine.Tests;

public sealed class SteeringInboxTests
{
    [Fact]
    public void Enqueue_returns_id_timestamp_and_fifo_entries()
    {
        var inbox = new SteeringInbox();

        var first = inbox.Enqueue("first");
        var second = inbox.Enqueue("second");
        var delivered = inbox.TakeAllForDelivery();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.Id, second!.Id);
        Assert.True(first.EnqueuedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(["first", "second"], delivered.Select(entry => entry.Text));
    }

    [Fact]
    public void Recall_and_delivery_cannot_take_the_same_entry()
    {
        var inbox = new SteeringInbox();
        inbox.Enqueue("only");

        var recalled = inbox.RecallAll();
        var delivered = inbox.TakeAllForDelivery();

        Assert.Single(recalled);
        Assert.Empty(delivered);
    }

    [Fact]
    public async Task Seal_race_preserves_or_rejects_the_entry_without_loss()
    {
        var inbox = new SteeringInbox();
        using var start = new ManualResetEventSlim();
        SteeringEntry? accepted = null;
        bool sealedEmpty = false;

        var enqueue = Task.Run(() =>
        {
            start.Wait();
            accepted = inbox.Enqueue("racing");
        });
        var seal = Task.Run(() =>
        {
            start.Wait();
            sealedEmpty = inbox.TrySealEmpty();
        });

        start.Set();
        await Task.WhenAll(enqueue, seal);

        var entries = inbox.TakeAllForDelivery();
        Assert.True(
            (accepted is null && sealedEmpty && entries.Count == 0)
            || (accepted is not null && !sealedEmpty && Assert.Single(entries).Id == accepted.Id));
    }

    [Fact]
    public void Clear_reopens_a_sealed_queue()
    {
        var inbox = new SteeringInbox();
        Assert.True(inbox.TrySealEmpty());
        Assert.Null(inbox.Enqueue("late"));

        inbox.Clear();

        Assert.NotNull(inbox.Enqueue("next"));
    }
}
