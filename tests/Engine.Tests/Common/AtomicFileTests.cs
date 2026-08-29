using Coda.Common;

namespace Engine.Tests.Common;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"coda-atomicfile-{Guid.NewGuid():N}");

    public AtomicFileTests() => Directory.CreateDirectory(this.root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the test's own scratch directory.
        }
    }

    private string Path_(string name) => Path.Combine(this.root, name);

    private string[] TempFiles() =>
        Directory.GetFiles(this.root, "*.tmp");

    [Fact]
    public void Writes_the_contents_to_the_target()
    {
        var file = this.Path_("settings.json");

        AtomicFile.WriteAllText(file, "{\"a\":1}");

        Assert.Equal("{\"a\":1}", File.ReadAllText(file));
    }

    [Fact]
    public void Overwrites_an_existing_file()
    {
        var file = this.Path_("settings.json");
        File.WriteAllText(file, "old");

        AtomicFile.WriteAllText(file, "new");

        Assert.Equal("new", File.ReadAllText(file));
    }

    [Fact]
    public void Creates_the_containing_directory()
    {
        var file = Path.Combine(this.root, "nested", "deeper", "settings.json");

        AtomicFile.WriteAllText(file, "{}");

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Leaves_no_temporary_file_behind_on_success()
    {
        var file = this.Path_("settings.json");

        AtomicFile.WriteAllText(file, "{}");

        Assert.Empty(this.TempFiles());
    }

    [Fact]
    public void Leaves_no_temporary_file_behind_across_many_writes()
    {
        var file = this.Path_("settings.json");

        for (var i = 0; i < 50; i++)
        {
            AtomicFile.WriteAllText(file, $"{{\"i\":{i}}}");
        }

        Assert.Empty(this.TempFiles());
        Assert.Equal("{\"i\":49}", File.ReadAllText(file));
    }

    [Fact]
    public void Removes_the_temporary_file_when_the_write_fails()
    {
        // A directory where the target name already exists as a directory makes
        // the rename fail, which is the path that used to leak.
        var file = this.Path_("settings.json");
        Directory.CreateDirectory(file);

        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(file, "{}"));
        Assert.Empty(this.TempFiles());
    }

    [Fact]
    public void Sweeps_stale_temporary_files_left_by_earlier_versions()
    {
        // The historic writer used a truncated stem: settings.json produced
        // ".settings.<guid>.tmp". Those are the files found accumulating in
        // real ~/.coda directories.
        var stale = this.Path_($".settings.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(stale, "abandoned");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        AtomicFile.WriteAllText(this.Path_("settings.json"), "{}");

        Assert.False(File.Exists(stale), "an abandoned temp file should be swept");
    }

    [Fact]
    public void Sweeps_stale_temporary_files_matching_the_full_name()
    {
        var stale = this.Path_($".settings.json.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(stale, "abandoned");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        AtomicFile.WriteAllText(this.Path_("settings.json"), "{}");

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void Does_not_sweep_a_recent_temporary_file()
    {
        // A concurrent write in another process must never be disturbed.
        var recent = this.Path_($".settings.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(recent, "in flight");

        AtomicFile.WriteAllText(this.Path_("settings.json"), "{}");

        Assert.True(File.Exists(recent), "an in-flight write was deleted");
    }

    [Fact]
    public void Does_not_sweep_temporary_files_belonging_to_another_target()
    {
        var other = this.Path_($".plugin-state.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(other, "someone else's");
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-2));

        AtomicFile.WriteAllText(this.Path_("settings.json"), "{}");

        Assert.True(File.Exists(other), "swept a different file's temporary");
    }

    [Fact]
    public async Task Async_write_produces_the_contents_and_no_temporary_file()
    {
        var file = this.Path_("transcript.json");

        await AtomicFile.WriteAllTextAsync(file, "{\"ok\":true}");

        Assert.Equal("{\"ok\":true}", File.ReadAllText(file));
        Assert.Empty(this.TempFiles());
    }

    [Fact]
    public async Task Async_write_removes_the_temporary_file_when_it_fails()
    {
        var file = this.Path_("transcript.json");
        Directory.CreateDirectory(file);

        await Assert.ThrowsAnyAsync<Exception>(
            () => AtomicFile.WriteAllTextAsync(file, "{}"));

        Assert.Empty(this.TempFiles());
    }

    [Fact]
    public void Rejects_a_blank_path()
    {
        Assert.ThrowsAny<ArgumentException>(() => AtomicFile.WriteAllText(" ", "{}"));
    }
}
