using System.Text.Json;
using Coda.Agent.Scheduling;
using Engine.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Engine.Tests.Scheduling;

/// <summary>
/// Task 2 store behaviour: schema-v2 persistence, independent per-record recovery, legacy
/// migration, invariant validation, buffered load diagnostics, and atomic best-effort writes.
/// </summary>
public sealed class ScheduledTaskStoreTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-01-01T00:00:00Z");

    // ────────────────────────────────────────────────────────────────
    // v2 round trip — every field, string enums, camelCase
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void V2_round_trip_persists_every_field_with_camelCase_and_string_enums()
    {
        var path = NewTempPath();
        try
        {
            var store = new ScheduledTaskStore(path);
            var added = store.Add(IntervalDraft(), Now);

            // Inject a fully-populated record (name + terminal outcome) so persistence exercises
            // every field, including the enum-typed ones.
            var full = added with
            {
                Name = "nightly report",
                LastTerminalOutcome = new ScheduleTerminalMetadata(
                    ScheduleTerminalOutcome.Succeeded,
                    DateTimeOffset.Parse("2025-01-01T00:05:00Z"),
                    "all good"),
            };
            Assert.True(store.Replace(full));

            var raw = File.ReadAllText(path);

            // camelCase property names.
            Assert.Contains("\"schemaVersion\"", raw);
            Assert.Contains("\"timeZoneId\"", raw);
            Assert.Contains("\"lastTerminalOutcome\"", raw);
            Assert.DoesNotContain("\"SchemaVersion\"", raw);
            Assert.DoesNotContain("\"TimeZoneId\"", raw);

            // string enums (member names, not integers).
            Assert.Contains("\"kind\": \"Interval\"", raw);
            Assert.Contains("\"outcome\": \"Succeeded\"", raw);

            // Reload independently and confirm a byte-for-byte field round trip.
            var reloaded = new ScheduledTaskStore(path).GetSnapshot();
            var task = Assert.Single(reloaded.Items);
            Assert.Equal(ScheduledTask.CurrentSchemaVersion, task.SchemaVersion);
            Assert.Equal(added.Id, task.Id);
            Assert.Equal("nightly report", task.Name);
            Assert.Equal(ScheduleKind.Interval, task.Kind);
            Assert.Equal("interval prompt", task.Prompt);
            Assert.Equal(TimeSpan.FromMinutes(5), task.Interval);
            Assert.Null(task.AtUtc);
            Assert.Null(task.Cron);
            Assert.Equal("UTC", task.TimeZoneId);
            Assert.Equal(added.NextRunUtc, task.NextRunUtc);
            Assert.Equal(added.CreatedAtUtc, task.CreatedAtUtc);
            Assert.NotNull(task.LastTerminalOutcome);
            Assert.Equal(ScheduleTerminalOutcome.Succeeded, task.LastTerminalOutcome!.Outcome);
            Assert.Equal("all good", task.LastTerminalOutcome.Summary);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Legacy migration + no rewrite until mutation
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_migrates_legacy_recurring_and_one_shot_records()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, """
            [
              { "Id": "legacyrec001", "Cron": "0 9 * * *", "Prompt": "daily", "Recurring": true,  "NextRunUtc": "2025-06-01T09:00:00Z" },
              { "Id": "legacyone002", "Cron": "0 9 * * *", "Prompt": "once",  "Recurring": false, "NextRunUtc": "2025-06-02T09:00:00Z" }
            ]
            """);

            var items = new ScheduledTaskStore(path).GetSnapshot().Items;
            Assert.Equal(2, items.Count);

            var recurring = items.Single(t => t.Id == "legacyrec001");
            Assert.Equal(ScheduleKind.Cron, recurring.Kind);
            Assert.Equal("UTC", recurring.TimeZoneId);
            Assert.Equal(ScheduledTask.CurrentSchemaVersion, recurring.SchemaVersion);
            Assert.Equal("0 9 * * *", recurring.Cron);
            Assert.Equal(DateTimeOffset.Parse("2025-06-01T09:00:00Z"), recurring.NextRunUtc);

            var oneShot = items.Single(t => t.Id == "legacyone002");
            Assert.Equal(ScheduleKind.At, oneShot.Kind);
            Assert.Equal(DateTimeOffset.Parse("2025-06-02T09:00:00Z"), oneShot.AtUtc);
            Assert.Equal(DateTimeOffset.Parse("2025-06-02T09:00:00Z"), oneShot.NextRunUtc);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_does_not_rewrite_file_until_first_mutation()
    {
        var path = NewTempPath();
        try
        {
            const string legacy = """
            [
              { "Id": "legacyrec001", "Cron": "0 9 * * *", "Prompt": "daily", "Recurring": true, "NextRunUtc": "2025-06-01T09:00:00Z" }
            ]
            """;
            File.WriteAllText(path, legacy);
            var before = File.ReadAllText(path);

            var store = new ScheduledTaskStore(path);
            Assert.Single(store.GetSnapshot().Items);

            // Pure load must NOT rewrite the on-disk document (legacy shape preserved verbatim).
            Assert.Equal(before, File.ReadAllText(path));

            // The next successful mutation upgrades the file to schema 2.
            store.Add(CronDraft(), Now);
            var after = File.ReadAllText(path);
            Assert.NotEqual(before, after);
            Assert.Contains("\"schemaVersion\"", after);
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Independent per-record recovery
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Load_recovers_valid_neighbours_around_a_malformed_element()
    {
        var path = NewTempPath();
        try
        {
            // Middle element is a v2-looking object with an unparseable date → JsonException.
            File.WriteAllText(path, """
            [
              { "schemaVersion": 2, "id": "good0000001", "kind": "Cron", "prompt": "a", "cron": "* * * * *", "timeZoneId": "UTC", "nextRunUtc": "2025-01-01T00:00:00Z", "createdAtUtc": "2025-01-01T00:00:00Z", "updatedAtUtc": "2025-01-01T00:00:00Z" },
              { "schemaVersion": 2, "id": "bad00000002", "kind": "Cron", "prompt": "b", "cron": "* * * * *", "timeZoneId": "UTC", "nextRunUtc": "not-a-real-date", "createdAtUtc": "2025-01-01T00:00:00Z", "updatedAtUtc": "2025-01-01T00:00:00Z" },
              { "schemaVersion": 2, "id": "good0000003", "kind": "Cron", "prompt": "c", "cron": "* * * * *", "timeZoneId": "UTC", "nextRunUtc": "2025-01-01T00:00:00Z", "createdAtUtc": "2025-01-01T00:00:00Z", "updatedAtUtc": "2025-01-01T00:00:00Z" }
            ]
            """);

            var logger = new CapturingLogger();
            var store = new ScheduledTaskStore(path, logger);

            var items = store.GetSnapshot().Items;
            Assert.Equal(2, items.Count);
            Assert.Contains(items, t => t.Id == "good0000001");
            Assert.Contains(items, t => t.Id == "good0000003");

            var skips = logger.Entries.Where(e => e.Message.Contains("record skipped")).ToList();
            Assert.Single(skips);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_skips_invalid_v2_record_but_keeps_valid_neighbour()
    {
        var path = NewTempPath();
        try
        {
            // Second element deserializes fine but violates an invariant (blank prompt).
            File.WriteAllText(path, """
            [
              { "schemaVersion": 2, "id": "valid000001", "kind": "Cron", "prompt": "keep me", "cron": "* * * * *", "timeZoneId": "UTC", "nextRunUtc": "2025-01-01T00:00:00Z", "createdAtUtc": "2025-01-01T00:00:00Z", "updatedAtUtc": "2025-01-01T00:00:00Z" },
              { "schemaVersion": 2, "id": "invalid0002", "kind": "Cron", "prompt": "   ", "cron": "* * * * *", "timeZoneId": "UTC", "nextRunUtc": "2025-01-01T00:00:00Z", "createdAtUtc": "2025-01-01T00:00:00Z", "updatedAtUtc": "2025-01-01T00:00:00Z" }
            ]
            """);

            var logger = new CapturingLogger();
            var store = new ScheduledTaskStore(path, logger);

            var task = Assert.Single(store.GetSnapshot().Items);
            Assert.Equal("valid000001", task.Id);
            Assert.Contains(logger.Entries, e => e.Message.Contains("record skipped"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_treats_non_array_document_as_empty_and_logs()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, """{ "not": "an array" }""");

            var logger = new CapturingLogger();
            var store = new ScheduledTaskStore(path, logger);

            Assert.Empty(store.GetSnapshot().Items);
            Assert.Contains(logger.Entries, e => e.Message.Contains("not a loadable array"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_treats_wholly_invalid_json_as_empty_and_logs()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, "this is not valid json {{{{");

            var logger = new CapturingLogger();
            var store = new ScheduledTaskStore(path, logger);

            Assert.Empty(store.GetSnapshot().Items);
            Assert.Contains(logger.Entries, e => e.Message.Contains("not a loadable array"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Load_diagnostics_are_buffered_until_logger_is_assigned()
    {
        var path = NewTempPath();
        try
        {
            File.WriteAllText(path, "this is not valid json {{{{");

            // Mirrors CodaSession: construct without a logger, then wire one later.
            var store = new ScheduledTaskStore(path);
            Assert.Empty(store.GetSnapshot().Items);

            var logger = new CapturingLogger();
            store.Logger = logger;

            Assert.Contains(logger.Entries, e => e.Message.Contains("not a loadable array"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Atomic persistence
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_writes_atomically_and_leaves_no_temp_files()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "scheduled_tasks.json");
            var store = new ScheduledTaskStore(path);

            store.Add(CronDraft(), Now);
            store.Add(IntervalDraft(), Now);
            Assert.True(store.Remove(store.GetSnapshot().Items[0].Id));

            // Target is valid JSON.
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
                Assert.Single(doc.RootElement.EnumerateArray());
            }

            // No leftover sibling temp files.
            var leftovers = Directory.GetFiles(dir).Where(f => f.EndsWith(".tmp", StringComparison.Ordinal)).ToList();
            Assert.Empty(leftovers);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public void Persist_failure_keeps_old_disk_json_but_updates_memory_version_and_logs()
    {
        var path = NewTempPath();
        try
        {
            var logger = new CapturingLogger();
            var store = new ScheduledTaskStore(path, logger);
            store.Add(CronDraft(), Now);

            var diskBefore = File.ReadAllText(path);
            var versionBefore = store.GetSnapshot().Version;

            // Hold the target open with no sharing so the atomic File.Move overwrite fails
            // deterministically while the on-disk bytes remain the previous valid document.
            using (var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                store.Add(IntervalDraft(), Now);

                var snap = store.GetSnapshot();
                Assert.Equal(2, snap.Items.Count);              // in-memory mutation succeeded
                Assert.Equal(versionBefore + 1, snap.Version);  // version advanced
                Assert.Contains(logger.Entries, e => e.Message.Contains("persistence failed"));
            }

            // Old on-disk document is intact (the failed write did not truncate it).
            Assert.Equal(diskBefore, File.ReadAllText(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Snapshot isolation
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_returns_copied_list_that_cannot_mutate_the_store()
    {
        var store = new ScheduledTaskStore();
        store.Add(CronDraft(), Now);

        var snapshot = store.GetSnapshot();
        Assert.Single(snapshot.Items);

        // The returned collection is a copy: mutating it (if it is a mutable type) must not
        // affect the store, and further store mutations must not change the captured snapshot.
        store.Add(IntervalDraft(), Now);

        Assert.Single(snapshot.Items);                 // captured snapshot is frozen
        Assert.Equal(2, store.GetSnapshot().Items.Count);
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static ScheduleDefinitionDraft IntervalDraft() =>
        new(null, ScheduleKind.Interval, "interval prompt", TimeSpan.FromMinutes(5), null, null, "UTC", Now + TimeSpan.FromMinutes(5));

    private static ScheduleDefinitionDraft CronDraft() =>
        new(null, ScheduleKind.Cron, "cron prompt", null, null, "*/5 * * * *", "UTC", Now + TimeSpan.FromMinutes(5));

    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), $"sched_store_{Guid.NewGuid():N}.json");

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sched_store_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void CleanupDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
