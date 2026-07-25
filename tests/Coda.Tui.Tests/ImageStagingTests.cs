using Coda.Tui.Agent;
using Coda.Tui.Repl;
using LlmClient;

namespace Coda.Tui.Tests;

/// <summary>
/// Coverage for labeled image staging on <see cref="SessionState"/> and the token-scan turn composer in
/// <see cref="AgentRunner.BuildImageTurnContent"/>: label assignment, reset, ordered token attachment,
/// auto-inclusion of non-token images, and dropping token-inserted images whose token was deleted.
/// </summary>
public sealed class ImageStagingTests
{
    private static ImageBlock Png() => new("image/png", "AA==");

    [Fact]
    public void StageImage_assigns_labels_starting_at_one()
    {
        var session = new SessionState("claude-ai");
        Assert.Equal(1, session.StageImage(Png()));
        Assert.Equal(2, session.StageImage(Png()));
    }

    [Fact]
    public void ClearStagedImages_resets_label_counter_and_list()
    {
        var session = new SessionState("claude-ai");
        session.StageImage(Png());
        session.StageImage(Png());

        session.ClearStagedImages();

        Assert.Empty(session.PendingImages);
        Assert.Equal(1, session.StageImage(Png()));
    }

    [Fact]
    public void PendingImages_adapter_preserves_backward_compat()
    {
        var session = new SessionState("claude-ai");
        session.PendingImages.Add(new ImageBlock("image/png", "dGVzdA=="));

        Assert.Single(session.PendingImages);
        Assert.Equal("image/png", session.PendingImages[0].MediaType);
        Assert.Single(new List<ContentBlock>(session.PendingImages));

        session.PendingImages.Clear();
        Assert.Empty(session.PendingImages);
    }

    [Fact]
    public void Token_in_text_attaches_referenced_image()
    {
        var session = new SessionState("claude-ai");
        var block = Png();
        session.StageImage(block, tokenInserted: true);

        var content = AgentRunner.BuildImageTurnContent(session.PendingLabeledImages, "look at [Image 1] please");

        Assert.NotNull(content);
        Assert.Equal(2, content!.Count);
        Assert.Same(block, content[0]);
        Assert.IsType<TextBlock>(content[1]);
    }

    [Fact]
    public void No_token_drops_token_inserted_images()
    {
        var session = new SessionState("claude-ai");
        session.StageImage(Png(), tokenInserted: true);

        var content = AgentRunner.BuildImageTurnContent(session.PendingLabeledImages, "no reference at all");

        Assert.Null(content);
    }

    [Fact]
    public void No_token_auto_includes_non_token_images()
    {
        var session = new SessionState("claude-ai");
        var block = Png();
        session.StageImage(block, tokenInserted: false);

        var content = AgentRunner.BuildImageTurnContent(session.PendingLabeledImages, "hello there");

        Assert.NotNull(content);
        Assert.Same(block, content![0]);
        Assert.IsType<TextBlock>(content[^1]);
    }

    [Fact]
    public void Tokens_out_of_order_attach_in_token_order()
    {
        var session = new SessionState("claude-ai");
        var first = new ImageBlock("image/png", "AA==");
        var second = new ImageBlock("image/jpeg", "BB==");
        session.StageImage(first, tokenInserted: true);   // label 1
        session.StageImage(second, tokenInserted: true);  // label 2

        var content = AgentRunner.BuildImageTurnContent(session.PendingLabeledImages, "[Image 2] before [Image 1]");

        Assert.NotNull(content);
        Assert.Same(second, content![0]);
        Assert.Same(first, content[1]);
        Assert.IsType<TextBlock>(content[2]);
    }

    [Fact]
    public void Literal_token_with_no_matching_image_attaches_nothing()
    {
        var session = new SessionState("claude-ai");
        session.StageImage(Png(), tokenInserted: true); // label 1 only

        var content = AgentRunner.BuildImageTurnContent(session.PendingLabeledImages, "see [Image 3] here");

        Assert.Null(content);
    }

    [Fact]
    public void No_staged_images_yields_text_only_turn()
    {
        var session = new SessionState("claude-ai");
        Assert.Null(AgentRunner.BuildImageTurnContent(session.PendingLabeledImages, "just text"));
    }
}
