namespace Engine.Tests.TestSupport;

/// <summary>
/// Creates a temporary directory for a test and deletes it on dispose.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("coda_p4_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(this.Path, recursive: true); } catch { }
    }
}
