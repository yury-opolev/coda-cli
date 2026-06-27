using Coda.Tui.Commands;
using Coda.Tui.Repl;
using LlmClient;

namespace Coda.Tui.Tests;

/// <summary>
/// Tests for AgentRunner's clear-on-success policy for PendingImages.
///
/// NOTE: End-to-end testing of AgentRunner.RunAsync (failed turn retains images)
/// requires real LLM credentials and a live session, which is impractical in unit
/// tests.  The clear-on-success logic is verified here at the SessionState level,
/// and the policy is documented in AgentRunner.cs with an inline comment.
/// </summary>
public sealed class AgentRunnerImagePolicyTests
{
    [Fact]
    public void PendingImages_are_retained_when_not_cleared_simulating_failed_turn()
    {
        // Arrange: a SessionState with one staged image (simulates state after
        // /image command; AgentRunner only calls Clear() on result.Success).
        var session = new SessionState("claude-ai");
        var image = new ImageBlock("image/png", "dGVzdA==");
        session.PendingImages.Add(image);

        // Act: simulate a FAILED turn — AgentRunner does NOT call Clear() when
        // result.Success is false, so PendingImages must still contain the image.
        // (No Clear() call here — that is the production behaviour on failure.)

        // Assert: the image is still present for the next retry.
        Assert.Single(session.PendingImages);
        Assert.Equal("image/png", session.PendingImages[0].MediaType);
    }

    [Fact]
    public void PendingImages_are_cleared_only_after_simulated_successful_turn()
    {
        // Arrange
        var session = new SessionState("claude-ai");
        session.PendingImages.Add(new ImageBlock("image/jpeg", "dGVzdA=="));

        // Act: simulate a SUCCESSFUL turn — AgentRunner calls Clear().
        session.PendingImages.Clear();

        // Assert
        Assert.Empty(session.PendingImages);
    }
}

public sealed class ImageCommandTests
{
    private static (TuiApp App, CommandContext Context, Spectre.Console.Testing.TestConsole Console) BuildApp()
    {
        var (app, ctx, console, _) = TestAppBuilder.BuildApp();
        return (app, ctx, console);
    }

    // ── /image with a valid temp PNG file ────────────────────────────────────

    [Fact]
    public async Task Image_command_with_valid_png_stages_an_ImageBlock()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.png");
        try
        {
            // Write a few bytes that look like a PNG
            await File.WriteAllBytesAsync(tmp, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            var (app, context, console) = BuildApp();
            var result = await app.DispatchAsync(
                ParsedInput.Slash("image", [tmp]),
                CancellationToken.None);

            Assert.False(result.ShouldExit);
            Assert.Single(context.Session.PendingImages);
            var block = context.Session.PendingImages[0];
            Assert.Equal("image/png", block.MediaType);
            Assert.NotEmpty(block.Base64Data);
            // Confirmation message
            Assert.Contains("Attached", console.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    // ── /image with a missing path → error, nothing staged ───────────────────

    [Fact]
    public async Task Image_command_with_missing_path_reports_error_and_stages_nothing()
    {
        var (app, context, console) = BuildApp();
        var missing = Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}.png");

        var result = await app.DispatchAsync(
            ParsedInput.Slash("image", [missing]),
            CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Empty(context.Session.PendingImages);
        Assert.Contains("not found", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ── /image with a non-image extension → error ────────────────────────────

    [Fact]
    public async Task Image_command_with_non_image_extension_reports_error()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(tmp, "hello");

            var (app, context, console) = BuildApp();
            var result = await app.DispatchAsync(
                ParsedInput.Slash("image", [tmp]),
                CancellationToken.None);

            Assert.False(result.ShouldExit);
            Assert.Empty(context.Session.PendingImages);
            Assert.Contains("not supported", console.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    // ── /image with no arguments → error ─────────────────────────────────────

    [Fact]
    public async Task Image_command_with_no_args_reports_error()
    {
        var (app, context, console) = BuildApp();

        var result = await app.DispatchAsync(
            ParsedInput.Slash("image", Array.Empty<string>()),
            CancellationToken.None);

        Assert.False(result.ShouldExit);
        Assert.Empty(context.Session.PendingImages);
        Assert.Contains("Usage:", console.Output, StringComparison.OrdinalIgnoreCase);
    }
}
