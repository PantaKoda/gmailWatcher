using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Desktop.Platform;

/// <summary>Opens folders and files from the archive in Explorer. Never throws; reports whether it worked.</summary>
public interface IFileLauncher
{
    bool OpenFolder(string path);

    /// <summary>Opens Explorer with the file selected.</summary>
    bool Reveal(string path);

    /// <summary>
    /// Opens a file with its default program, but only for types that are safe to open from a mailbox. Anything else
    /// (programs, scripts, shortcuts, macro-enabled documents) is refused: use <see cref="Reveal"/> and decide in Explorer.
    /// </summary>
    bool OpenFile(string path);
}

public sealed class FileLauncher(ILogger<FileLauncher> logger) : IFileLauncher
{
    // An allow-list, not a block-list: a mailbox is the most common way for malware to arrive, so only plainly inert
    // document, picture and text types may be opened with one click.
    private static readonly HashSet<string> SafeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".md", ".csv", ".log", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".svg",
        ".eml", ".ics", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".rtf",
    };

    public static bool IsSafeToOpen(string fileName) => SafeExtensions.Contains(Path.GetExtension(fileName));

    public bool OpenFolder(string path) =>
        Directory.Exists(path) && Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public bool Reveal(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return OpenFolder(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
        }

        return Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"" });
    }

    public bool OpenFile(string path) =>
        File.Exists(path) && IsSafeToOpen(path) && Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private bool Start(ProcessStartInfo info)
    {
        try
        {
            using var process = Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open {Target}", info.FileName);
            return false;
        }
    }
}
