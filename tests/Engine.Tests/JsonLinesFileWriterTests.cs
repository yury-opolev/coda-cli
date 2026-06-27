using Coda.Sdk.Telemetry;

namespace Engine.Tests;

public sealed class JsonLinesFileWriterTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "coda-logtest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch { }
    }

    [Fact]
    public void WriteLine_appends_one_line_per_event()
    {
        using (var writer = new JsonLinesFileWriter(this.dir, maxFileSizeBytes: 0, maxRunParts: 0, sessionStem: "coda-run"))
        {
            writer.WriteLine("""{"a":1}""");
            writer.WriteLine("""{"b":2}""");
        }

        var path = Path.Combine(this.dir, "coda-run.log");
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal("""{"a":1}""", lines[0]);
        Assert.Equal("""{"b":2}""", lines[1]);
    }

    [Fact]
    public void Exceeding_size_cap_rolls_to_next_numbered_part()
    {
        using (var writer = new JsonLinesFileWriter(this.dir, maxFileSizeBytes: 10, maxRunParts: 0, sessionStem: "coda-run"))
        {
            writer.WriteLine(new string('x', 20)); // first part exceeds 10 bytes
            writer.WriteLine(new string('y', 20)); // forces a roll before/at this write
        }

        Assert.True(File.Exists(Path.Combine(this.dir, "coda-run.log")));
        Assert.True(File.Exists(Path.Combine(this.dir, "coda-run.1.log")));
    }

    [Fact]
    public void Ring_buffer_deletes_oldest_part_beyond_MaxRunParts()
    {
        using (var writer = new JsonLinesFileWriter(this.dir, maxFileSizeBytes: 5, maxRunParts: 2, sessionStem: "coda-run"))
        {
            for (var i = 0; i < 6; i++)
            {
                writer.WriteLine(new string('z', 10));
            }
        }

        var parts = Directory.GetFiles(this.dir, "coda-run*.log");
        Assert.Equal(2, parts.Length); // only newest 2 kept
        Assert.False(File.Exists(Path.Combine(this.dir, "coda-run.log"))); // oldest pruned
    }

    [Fact]
    public void PruneRuns_keeps_newest_runs_grouped_by_stem()
    {
        Directory.CreateDirectory(this.dir);
        // Three runs, oldest first by timestamp in the stem.
        File.WriteAllText(Path.Combine(this.dir, "coda-20260101-000000-1.log"), "a");
        File.WriteAllText(Path.Combine(this.dir, "coda-20260102-000000-2.log"), "b");
        File.WriteAllText(Path.Combine(this.dir, "coda-20260102-000000-2.1.log"), "b2"); // same run, 2 parts
        File.WriteAllText(Path.Combine(this.dir, "coda-20260103-000000-3.log"), "c");

        JsonLinesFileWriter.PruneRuns(this.dir, retainedRuns: 2);

        Assert.False(File.Exists(Path.Combine(this.dir, "coda-20260101-000000-1.log"))); // oldest run gone
        Assert.True(File.Exists(Path.Combine(this.dir, "coda-20260102-000000-2.log")));
        Assert.True(File.Exists(Path.Combine(this.dir, "coda-20260102-000000-2.1.log")));
        Assert.True(File.Exists(Path.Combine(this.dir, "coda-20260103-000000-3.log")));
    }
}
