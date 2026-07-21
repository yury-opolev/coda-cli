using Coda.Agent.Scheduling;

namespace Engine.Tests.Scheduling;

/// <summary>
/// Verifies that legacy 5-field persisted schedule records
/// (<c>Id,Cron,Prompt,Recurring,NextRunUtc</c>) are migrated into the versioned v2
/// <see cref="ScheduledTask"/> shape on load instead of being silently corrupted.
/// </summary>
public sealed class ScheduledTaskStoreLegacyTests
{
    private const string LegacyRecurringJson = """
    [
      {
        "Id": "legacy001abcd",
        "Cron": "0 9 * * *",
        "Prompt": "daily legacy task",
        "Recurring": true,
        "NextRunUtc": "2025-06-01T09:00:00Z"
      }
    ]
    """;

    private const string LegacyOneShotJson = """
    [
      {
        "Id": "legacy002abcd",
        "Cron": "0 9 * * *",
        "Prompt": "one-shot legacy task",
        "Recurring": false,
        "NextRunUtc": "2025-06-01T09:00:00Z"
      }
    ]
    """;

    [Fact]
    public void Load_migrates_legacy_recurring_record_to_cron()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, LegacyRecurringJson);

            var store = new ScheduledTaskStore(path);

            var task = Assert.Single(store.Items);
            Assert.Equal("legacy001abcd", task.Id);
            Assert.Equal(ScheduleKind.Cron, task.Kind);
            Assert.Equal("UTC", task.TimeZoneId);
            Assert.Equal(ScheduledTask.CurrentSchemaVersion, task.SchemaVersion);
            Assert.Equal("0 9 * * *", task.Cron);
            Assert.Equal("daily legacy task", task.Prompt);
            Assert.Equal(DateTimeOffset.Parse("2025-06-01T09:00:00Z"), task.NextRunUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_migrates_legacy_one_shot_record_to_at()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, LegacyOneShotJson);

            var store = new ScheduledTaskStore(path);

            var task = Assert.Single(store.Items);
            Assert.Equal(ScheduleKind.At, task.Kind);
            Assert.Equal(DateTimeOffset.Parse("2025-06-01T09:00:00Z"), task.AtUtc);
            Assert.Equal(DateTimeOffset.Parse("2025-06-01T09:00:00Z"), task.NextRunUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Save_after_loading_legacy_preserves_migrated_cron_record()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, LegacyRecurringJson);

            var store = new ScheduledTaskStore(path);

            // Adding a new schedule triggers Save(), which rewrites the whole file. The legacy
            // record must survive as a Cron definition rather than being erased or downgraded.
            store.Add(
                "*/5 * * * *",
                "new task",
                recurring: true,
                nowUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var reloaded = new ScheduledTaskStore(path);
            Assert.Equal(2, reloaded.Items.Count);

            var legacy = reloaded.Items.Single(t => t.Id == "legacy001abcd");
            Assert.Equal(ScheduleKind.Cron, legacy.Kind);
            Assert.Equal("0 9 * * *", legacy.Cron);
            Assert.Equal("daily legacy task", legacy.Prompt);
            Assert.Equal("UTC", legacy.TimeZoneId);
            Assert.Equal(ScheduledTask.CurrentSchemaVersion, legacy.SchemaVersion);
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), $"sched_legacy_{Guid.NewGuid():N}.json");

    private static void Cleanup(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
