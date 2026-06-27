namespace Engine.Tests.Common;
using Coda.Common;
public class ContextWindowTests
{
    [Fact]
    public void DefaultTokens_is_200k()
    {
        Assert.Equal(200_000, ContextWindow.DefaultTokens);
    }
}
