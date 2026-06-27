using System.Text.Json;
using Coda.Sdk.Telemetry;
using Microsoft.Extensions.Logging;

namespace Engine.Tests;

public sealed class JsonLinesFileLoggerTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "coda-logger-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(this.dir, recursive: true); } catch { }
    }

    [Fact]
    public void Logs_at_or_above_min_level_are_written_as_json()
    {
        string file;
        using (var provider = new JsonLinesFileLoggerProvider(this.dir, LogLevel.Information, maxFileSizeBytes: 0, maxRunParts: 0, sessionStem: "coda-t"))
        {
            var logger = provider.CreateLogger("Cat.A");
            logger.LogInformation("hello {Name}", "world");
            logger.LogDebug("should be filtered");
            file = provider.CurrentFilePath;
        }

        var lines = File.ReadAllLines(file);
        Assert.Single(lines);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("Information", doc.RootElement.GetProperty("level").GetString());
        Assert.Equal("Cat.A", doc.RootElement.GetProperty("category").GetString());
        Assert.Equal("hello world", doc.RootElement.GetProperty("message").GetString());
        Assert.True(doc.RootElement.TryGetProperty("ts", out _));
    }

    [Fact]
    public void Secrets_in_messages_are_redacted()
    {
        string file;
        using (var provider = new JsonLinesFileLoggerProvider(this.dir, LogLevel.Trace, maxFileSizeBytes: 0, maxRunParts: 0, sessionStem: "coda-t"))
        {
            provider.CreateLogger("X").LogError("token sk-ant-api03-SECRET12345");
            file = provider.CurrentFilePath;
        }

        var text = File.ReadAllText(file);
        Assert.DoesNotContain("sk-ant-api03-SECRET12345", text);
    }
}
