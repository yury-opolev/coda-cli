using Coda.Sdk;
using Engine.Tests.TestSupport;

namespace Engine.Tests;

public sealed class CodaSessionAdoptIdTests : IDisposable
{
    private readonly string tempDir = Directory.CreateTempSubdirectory("coda_adoptid_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void AdoptSessionId_rejects_empty()
    {
        using var session = FakeSession.New(this.tempDir);
        Assert.Throws<ArgumentException>(() => session.AdoptSessionId(""));
    }

    [Fact]
    public void AdoptSessionId_sets_the_session_id()
    {
        using var session = FakeSession.New(this.tempDir);
        session.AdoptSessionId("adopted99");
        Assert.Equal("adopted99", session.SessionId);
    }

    [Fact]
    public async Task AdoptSessionId_redirects_persistence_to_the_new_id_file()
    {
        using var session = FakeSession.New(this.tempDir);
        await session.RunAsync("first");            // persists under the original generated id

        session.AdoptSessionId("adopted99");
        await session.RunAsync("second");           // must now persist under the adopted id

        Assert.True(File.Exists(Path.Combine(this.tempDir, ".coda", "sessions", "adopted99.json")));
    }
}
