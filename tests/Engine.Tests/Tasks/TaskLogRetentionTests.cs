using Coda.Agent.Tasks;
using Xunit;

namespace Engine.Tests.Tasks;

public class TaskLogRetentionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "coda-retention-" + Guid.NewGuid().ToString("N"));

    public TaskLogRetentionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteLog(string name, int bytes, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_root, name);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    /// <summary>Deterministic fixed-clock provider for age-based retention tests.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public void TaskLogRetention_IsNotPublic()
    {
        var type = typeof(TaskLogRetention);
        Assert.False(type.IsPublic, "TaskLogRetention must not be public; it is an internal housekeeping type.");
        Assert.True(type.IsNotPublic);
    }

    [Fact]
    public void Cleanup_DeletesLogsOlderThanMaxAge()
    {
        var old = WriteLog("old.log", 10, DateTime.UtcNow.AddDays(-8));
        var fresh = WriteLog("fresh.log", 10, DateTime.UtcNow.AddDays(-1));

        TaskLogRetention.Cleanup(_root, TimeSpan.FromDays(7), globalCapBytes: 1_000_000);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Cleanup_WithFixedClock_DeletesLogsOlderThanMaxAge()
    {
        var now = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var old = WriteLog("old.log", 10, now.UtcDateTime.AddDays(-8));
        var fresh = WriteLog("fresh.log", 10, now.UtcDateTime.AddDays(-6));

        TaskLogRetention.Cleanup(
            _root, TimeSpan.FromDays(7), globalCapBytes: 1_000_000, timeProvider: new FixedTimeProvider(now));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Cleanup_RecursesIntoSessionSubdirectories()
    {
        var old = WriteLog(Path.Combine("sess-a", "old.log"), 10, DateTime.UtcNow.AddDays(-9));
        var fresh = WriteLog(Path.Combine("sess-b", "fresh.log"), 10, DateTime.UtcNow.AddDays(-1));

        TaskLogRetention.Cleanup(_root, TimeSpan.FromDays(7), globalCapBytes: 1_000_000);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Cleanup_EnforcesGlobalCap_DeletingOldestFirst()
    {
        var oldest = WriteLog("a.log", 100, DateTime.UtcNow.AddHours(-3));
        var middle = WriteLog("b.log", 100, DateTime.UtcNow.AddHours(-2));
        var newest = WriteLog("c.log", 100, DateTime.UtcNow.AddHours(-1));

        // Cap of 150 bytes keeps only the newest (100); the next would push total to 200.
        TaskLogRetention.Cleanup(_root, TimeSpan.FromDays(7), globalCapBytes: 150);

        Assert.True(File.Exists(newest));
        Assert.False(File.Exists(middle));
        Assert.False(File.Exists(oldest));
    }

    [Fact]
    public void Cleanup_MissingRoot_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            TaskLogRetention.Cleanup(
                Path.Combine(_root, "does-not-exist"),
                TimeSpan.FromDays(7),
                globalCapBytes: 1000));
        Assert.Null(ex);
    }

    [Fact]
    public void Cleanup_UndeletableFile_IsBestEffortAndDoesNotThrow()
    {
        var locked = WriteLog("locked.log", 10, DateTime.UtcNow.AddDays(-9));
        var alsoOld = WriteLog("also-old.log", 10, DateTime.UtcNow.AddDays(-9));

        // Hold the file open with no sharing so deletion fails on Windows; cleanup must
        // swallow the failure and still process the other file.
        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Record.Exception(() =>
            TaskLogRetention.Cleanup(_root, TimeSpan.FromDays(7), globalCapBytes: 1_000_000));

        Assert.Null(ex);
        Assert.False(File.Exists(alsoOld));
    }

    [Fact]
    public void Defaults_AreSevenDaysAnd512Mebibytes()
    {
        Assert.Equal(TimeSpan.FromDays(7), TaskLogRetention.MaxAge);
        Assert.Equal(512L * 1024 * 1024, TaskLogRetention.GlobalCapBytes);
    }
}
