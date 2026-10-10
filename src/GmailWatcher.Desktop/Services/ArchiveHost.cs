using GmailWatcher.Archive;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Desktop.Infrastructure;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Desktop.Services;

/// <summary>
/// Owns the open archive. The folder is chosen on first run (and remembered in the per-user configuration), so the
/// archive may not exist yet when the app starts: everything that needs it asks <see cref="Runtime"/> only after
/// <see cref="IsOpen"/>.
/// </summary>
public sealed class ArchiveHost(GmailWatcherOptions options, UserConfigWriter config, ILoggerFactory loggers, TimeProvider time, string? fixedDirectory = null)
{
    private readonly ILogger _logger = loggers.CreateLogger<ArchiveHost>();
    private ArchiveRuntime? _runtime;

    /// <summary>The configured archive folder, or null until it has been chosen.</summary>
    public string? Directory { get; private set; } = fixedDirectory ?? (options.Archive.IsChosen ? options.Archive.Directory!.Trim() : null);

    public bool IsConfigured => Directory is not null;

    public bool IsOpen => _runtime is not null;

    /// <summary>Why the configured archive could not be opened (a newer version wrote it, the drive is gone...), in words for the user.</summary>
    public string? Problem { get; private set; }

    public ArchiveRuntime Runtime => _runtime ?? throw new InvalidOperationException("The archive is not open.");

    public event EventHandler? Opened;

    /// <summary>Opens the configured archive. Safe to call when nothing is configured (does nothing). Never throws; see <see cref="Problem"/>.</summary>
    public bool TryOpen()
    {
        if (_runtime is not null)
        {
            return true;
        }

        if (Directory is null)
        {
            return false;
        }

        try
        {
            _runtime = ArchiveRuntime.Open(Directory, options.Sync, loggers, time);
            _runtime.Mirror.EnsureCreated();
            Problem = null;
            _logger.LogInformation("Archive opened at {Directory}", Directory);
            Opened?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (ArchiveVersionException ex)
        {
            Problem = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogError(ex, "The archive at {Directory} could not be opened", Directory);
            Problem = $"The archive folder {Directory} could not be opened ({ex.Message}). Is the drive connected and the folder writable?";
        }

        return false;
    }

    /// <summary>Chooses the archive folder (first run), remembers it and opens it.</summary>
    /// <returns>Null on success, otherwise why the folder can't be used.</returns>
    public string? Choose(string directory)
    {
        directory = directory.Trim();
        if (!Path.IsPathFullyQualified(directory))
        {
            return "Choose a full folder path, for example D:\\Mail\\GmailWatcher.";
        }

        try
        {
            System.IO.Directory.CreateDirectory(directory);
            // A probe file proves the folder is writable now, instead of failing in the middle of the first sync.
            var probe = Path.Combine(directory, $".gmailwatcher-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Gmail Watcher can't write to that folder ({ex.Message}). Choose another one.";
        }

        Directory = Path.GetFullPath(directory);
        config.Set(("Archive:Directory", Directory));
        options.Archive.Directory = Directory;
        return TryOpen() ? null : Problem ?? "The archive could not be opened.";
    }
}
