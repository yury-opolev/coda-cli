namespace Coda.Tui.Clipboard;

/// <summary>A PNG image read from the OS clipboard, encoded as base64.</summary>
public sealed record ClipboardImage(string MediaType, string Base64Data, int ByteLength);
