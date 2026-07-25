using Coda.Sdk;
using Coda.Tui.Rendering;
using Coda.Tui.Repl;
using LlmClient;
using Spectre.Console;

namespace Coda.Tui.Commands;

/// <summary>
/// Stages an image for the next user turn.
/// Usage: /image &lt;path&gt;
/// The image is base64-encoded and attached to the next prompt as an <see cref="ImageBlock"/>.
/// </summary>
public sealed class ImageCommand : ISlashCommand
{
    /// <summary>Maximum accepted file size (5 MiB).</summary>
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> SupportedExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
        };

    public string Name => "image";

    public IReadOnlyList<string> Aliases => [];

    public string Summary => "Attach an image to the next message";

    public CommandHelp Help => new(
        "/image <path>",
        Description: "Base64-encodes the specified image file and stages it as an image block to be sent with the next user message. Supported formats: .png, .jpg/.jpeg, .gif, .webp. Maximum file size is 5 MB.",
        Options:
        [
            ("<path>", "path to the image file to attach (absolute, or relative to the current directory)"),
        ],
        Examples: ["/image screenshot.png", "/image /tmp/diagram.webp"]);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (args.Count == 0)
        {
            context.Console.MarkupLine(Theme.WarnMarkup("Usage: /image <path>  — attaches an image to the next turn."));
            return CommandResult.Continue;
        }

        var path = args[0];

        if (!File.Exists(path))
        {
            context.Console.MarkupLine(Theme.ErrorMarkup($"File not found: {Markup.Escape(path)}"));
            return CommandResult.Continue;
        }

        var ext = Path.GetExtension(path);
        if (!SupportedExtensions.TryGetValue(ext, out var mediaType))
        {
            var supported = string.Join(", ", SupportedExtensions.Keys);
            context.Console.MarkupLine(Theme.ErrorMarkup(
                $"File type not supported: '{Markup.Escape(ext)}'. Supported types: {supported}"));
            return CommandResult.Continue;
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length > MaxFileSizeBytes)
        {
            var sizeMb = fileInfo.Length / (1024.0 * 1024.0);
            context.Console.MarkupLine(Theme.WarnMarkup(
                $"File too large ({sizeMb:F1} MB). Maximum size is 5 MB."));
            return CommandResult.Continue;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var base64 = Convert.ToBase64String(bytes);

        var validationError = ImageAttachmentValidation.Validate(mediaType, base64);
        if (validationError is not null)
        {
            context.Console.MarkupLine(Theme.WarnMarkup(Markup.Escape(validationError)));
            return CommandResult.Continue;
        }

        var block = new ImageBlock(mediaType, base64);

        // When a live composer is available, insert an [Image N] token so the attachment is positioned in
        // the message; the token-scan turn composer then attaches only referenced images. Without a composer
        // (plain/non-interactive), stage without a token so the image is auto-included on the next turn.
        var tokenInserted = context.DraftInsertCallback is not null;
        var label = context.Session.StageImage(block, tokenInserted);
        if (tokenInserted)
        {
            context.DraftInsertCallback!.Invoke($"[Image {label}]");
        }

        var fileName = Path.GetFileName(path);
        var sizeKb = bytes.Length / 1024.0;
        context.Console.MarkupLine(Theme.DimMarkup(
            $"Attached {Markup.Escape(fileName)} as [Image {label}] ({sizeKb:F1} KB). It will be sent with your next message."));

        return CommandResult.Continue;
    }
}
