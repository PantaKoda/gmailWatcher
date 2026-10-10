using GmailWatcher.Core.Settings;
using GmailWatcher.Desktop.Infrastructure;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Desktop.Services;

public sealed class AppSettingsChangedEventArgs(AppSettings previous, AppSettings current) : EventArgs
{
    public AppSettings Previous { get; } = previous;

    public AppSettings Current { get; } = current;
}

/// <summary>
/// Holds the current settings in memory and saves changes shortly after they happen, to a JSON file replaced atomically.
/// An unreadable file is set aside (never silently overwritten) and the defaults are used; a file written by a newer
/// version is left alone and changes are not saved. If the file can't be written the app keeps working in memory.
/// </summary>
public sealed class SettingsService(AppPaths paths, ILogger<SettingsService> logger) : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private Timer? _saveTimer;
    private bool _dirty;
    private bool _readOnly;

    public AppSettings App { get; private set; } = SettingsCodecs.App.CreateDefault();

    /// <summary>A user-facing explanation when settings could not be loaded or will not be saved.</summary>
    public string? Problem { get; private set; }

    public event EventHandler<AppSettingsChangedEventArgs>? AppChanged;

    public event EventHandler? ProblemChanged;

    public void Load()
    {
        _saveTimer = new Timer(_ => Flush());
        try
        {
            if (!File.Exists(paths.SettingsFile))
            {
                return;
            }

            var result = SettingsCodecs.App.Deserialize(File.ReadAllText(paths.SettingsFile));
            App = result.Value;
            switch (result.Status)
            {
                case Core.Settings.SettingsLoadStatus.Corrupt:
                    var kept = $"{paths.SettingsFile}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
                    File.Copy(paths.SettingsFile, kept, overwrite: true);
                    SetProblem("Saved settings were unreadable and have been reset. The old file was kept next to it.");
                    break;
                case Core.Settings.SettingsLoadStatus.Repaired:
                    SetProblem("Some saved settings were invalid and have been reset to defaults.");
                    break;
                case Core.Settings.SettingsLoadStatus.NewerVersion:
                    _readOnly = true;
                    SetProblem("Settings were saved by a newer version of Gmail Watcher. Changes made now will not be saved.");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Settings could not be read; continuing with defaults");
            _readOnly = true;
            SetProblem($"Settings could not be read, and changes will not be saved. {ex.Message}");
        }
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings previous;
        AppSettings updated;
        lock (_gate)
        {
            previous = App;
            updated = SettingsCodecs.Normalize(change(App));
            if (updated == previous)
            {
                return;
            }

            App = updated;
            _dirty = true;
            _saveTimer?.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }

        AppChanged?.Invoke(this, new AppSettingsChangedEventArgs(previous, updated));
    }

    /// <summary>Writes pending changes now. Called on a timer and at shutdown; safe to call concurrently.</summary>
    public void Flush()
    {
        lock (_writeGate)
        {
            AppSettings snapshot;
            lock (_gate)
            {
                if (!_dirty || _readOnly)
                {
                    return;
                }

                _dirty = false;
                snapshot = App;
            }

            try
            {
                Directory.CreateDirectory(paths.DataDirectory);
                var temp = paths.SettingsFile + ".tmp";
                File.WriteAllText(temp, SettingsCodecs.App.Serialize(snapshot));
                File.Move(temp, paths.SettingsFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Saving settings failed");
                lock (_gate)
                {
                    _dirty = true; // retried on the next change or at shutdown
                }

                SetProblem($"Saving settings failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _saveTimer?.Dispose();
        Flush();
    }

    private void SetProblem(string? problem)
    {
        if (problem == Problem)
        {
            return;
        }

        Problem = problem;
        ProblemChanged?.Invoke(this, EventArgs.Empty);
    }
}
