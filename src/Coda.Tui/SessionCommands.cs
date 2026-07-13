using Coda.Sdk;

namespace Coda.Tui;

/// <summary>Headless `coda export <id>` / `coda import <file>` — operate on files in the working dir only.</summary>
public static class SessionCommands
{
    public static async Task<int> RunExportAsync(string[] args, string workingDirectory)
    {
        string? id = null;
        string? outPath = null;
        var pretty = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    if (++i >= args.Length) { Console.Error.WriteLine("Missing value for --out."); return 1; }
                    outPath = args[i];
                    break;
                case "--pretty":
                    pretty = true;
                    break;
                default:
                    if (id is null && !args[i].StartsWith('-')) { id = args[i]; }
                    else { Console.Error.WriteLine($"Unexpected argument '{args[i]}'."); return 1; }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            Console.Error.WriteLine("Usage: coda export <id> [--out <path>] [--pretty]");
            return 1;
        }

        var service = new SessionBundleService(workingDirectory, Branding.Version);
        var bundle = await service.ExportAsync(id, DateTime.UtcNow).ConfigureAwait(false);
        if (bundle is null)
        {
            Console.Error.WriteLine($"Session '{id}' not found.");
            return 1;
        }

        var resolved = outPath is null
            ? Path.Combine(workingDirectory, $"{id}.coda-session.json")
            : Path.IsPathRooted(outPath) ? outPath : Path.Combine(workingDirectory, outPath);
        await service.WriteAsync(bundle, resolved, pretty).ConfigureAwait(false);
        Console.WriteLine(resolved);
        return 0;
    }

    public static async Task<int> RunImportAsync(string[] args, string workingDirectory)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("Usage: coda import <file>");
            return 1;
        }

        var path = Path.IsPathRooted(args[0]) ? args[0] : Path.Combine(workingDirectory, args[0]);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        try
        {
            var service = new SessionBundleService(workingDirectory, Branding.Version);
            var id = await service.ImportAsync(path).ConfigureAwait(false);
            Console.WriteLine(id);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or IOException)
        {
            Console.Error.WriteLine($"Import failed: {ex.Message}");
            return 1;
        }
    }
}
