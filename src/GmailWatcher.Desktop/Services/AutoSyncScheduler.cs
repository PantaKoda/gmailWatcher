using GmailWatcher.Core.Settings;

namespace GmailWatcher.Desktop.Services;

/// <summary>
/// Optional syncing while the app is open: on a schedule the user chose (off by default) and, if asked, shortly after
/// start-up. Accounts that need reconnecting are skipped. Nothing runs in the background after the app is closed.
/// </summary>
public sealed class AutoSyncScheduler(SettingsService settings, SyncService sync, ArchiveHost archive, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    private Timer? _timer;
    private DateTimeOffset _lastRun;

    public void Start()
    {
        _lastRun = time.GetUtcNow();
        if (settings.App.Sync.SyncOnStartup)
        {
            _ = Task.Delay(StartupDelay).ContinueWith(_ =>
            {
                if (archive.IsOpen)
                {
                    sync.StartSyncAll();
                    _lastRun = time.GetUtcNow();
                }
            }, TaskScheduler.Default);
        }

        _timer = new Timer(_ => Tick(), null, CheckEvery, CheckEvery);
    }

    /// <summary>Starts a sync of every account when the chosen interval has passed. Public so tests can drive it.</summary>
    public void Tick()
    {
        if (!archive.IsOpen || SyncSettings.IntervalOf(settings.App.Sync.AutoSync) is not { } interval)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (now - _lastRun >= interval)
        {
            _lastRun = now;
            sync.StartSyncAll();
        }
    }

    public void Dispose() => _timer?.Dispose();
}
