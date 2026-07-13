using Coda.Sdk;

namespace Engine.Tests;

public sealed class SessionIdsTests
{
    [Fact]
    public void NewId_is_12_char_lowercase_hex()
    {
        var id = SessionIds.NewId();
        Assert.Equal(12, id.Length);
        Assert.Matches("^[0-9a-f]{12}$", id);
    }

    [Fact]
    public void NewId_is_distinct_across_calls()
    {
        Assert.NotEqual(SessionIds.NewId(), SessionIds.NewId());
    }
}
