using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Platform;
using GmailWatcher.Desktop.Infrastructure;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;
using GmailWatcher.Desktop.Updates;

namespace GmailWatcher.Desktop.ViewModels;

/// <summary>How this copy of the app was started. Demo mode uses made-up mail and its own folders; it never touches real data.</summary>
public sealed record AppMode(bool IsDemo);

/// <summary>
/// The main window: which of its screens to show (choose a folder, set up Google, add the first account, the accounts), the
/// account cards, the header summary, and the actions behind every button. Services push changes from background threads;
/// they are coalesced and applied on the UI thread, in place, so the list never flickers.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAccountCommands, IDisposable
{
    private readonly IShell _shell;
    private readonly ArchiveHost _archive;
    private readonly AccountService _accounts;
    private readonly SyncService _sync;
    private readonly GoogleClientSetup _google;
    private readonly IFileLauncher _files;
    private readonly IExternalBrowser _browser;
    private readonly UpdateService _updates;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly Dictionary<int, AccountRowViewModel> _rows = [];
    private readonly ConcurrentDictionary<int, byte> _dirty = new();
    private readonly HashSet<int> _loadingStats = [];
    private int _drainScheduled;
    private int _reloadScheduled;

    public MainViewModel(
        IShell shell,
        ArchiveHost archive,
        AccountService accounts,
        SyncService sync,
        GoogleClientSetup google,
        IFileLauncher files,
        IExternalBrowser browser,
        UpdateService updates,
        IUiDispatcher dispatcher,
        TimeProvider time,
        AppMode mode)
    {
        _shell = shell;
        _archive = archive;
        _accounts = accounts;
        _sync = sync;
        _google = google;
        _files = files;
        _browser = browser;
        _updates = updates;
        _dispatcher = dispatcher;
        _time = time;
        IsDemo = mode.IsDemo;
        FolderText = archive.Directory ?? AppPaths.SuggestedArchiveDirectory;

        _sync.ActivityChanged += OnActivityChanged;
        _accounts.AccountsChanged += OnAccountsChanged;
        _updates.Changed += OnUpdatesChanged;
        _archive.Opened += OnArchiveOpened;
        Reload();
    }

    public ObservableCollection<AccountRowViewModel> Accounts { get; } = [];

    public bool IsDemo { get; }

    // --- Which screen -------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    [NotifyPropertyChangedFor(nameof(ShowArchiveProblem))]
    [NotifyPropertyChangedFor(nameof(ShowNoAccounts))]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(ShowGoogleBanner))]
    [NotifyPropertyChangedFor(nameof(HasAccounts))]
    [NotifyPropertyChangedFor(nameof(ShowToolbar))]
    public partial bool ArchiveOpen { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    [NotifyPropertyChangedFor(nameof(ShowArchiveProblem))]
    public partial bool ArchiveConfigured { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoAccounts))]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(HasAccounts))]
    [NotifyPropertyChangedFor(nameof(ShowToolbar))]
    public partial int AccountCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(ShowToolbar))]
    public partial AccountDetailsViewModel? Details { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGoogleBanner))]
    public partial bool GoogleReady { get; private set; }

    public bool ShowWelcome => !ArchiveConfigured;

    public bool ShowArchiveProblem => ArchiveConfigured && !ArchiveOpen;

    public bool ShowNoAccounts => ArchiveOpen && AccountCount == 0;

    public bool HasAccounts => ArchiveOpen && AccountCount > 0;

    public bool ShowList => HasAccounts && Details is null;

    public bool ShowToolbar => HasAccounts && Details is null;

    public bool ShowGoogleBanner => ArchiveOpen && !GoogleReady;

    public bool ShowDetails => Details is not null;

    // --- Welcome: choosing the archive folder -------------------------------------------------------------------------

    [ObservableProperty]
    public partial string FolderText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolderProblem))]
    public partial string? FolderProblem { get; private set; }

    public bool HasFolderProblem => FolderProblem is not null;

    public string ArchiveProblemText => _archive.Problem ?? "The archive could not be opened.";

    // --- Header and footer ------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial StatusTone StatusTone { get; private set; } = StatusTone.Unknown;

    [ObservableProperty]
    public partial bool AnyBusy { get; private set; }

    [ObservableProperty]
    public partial string FooterText { get; private set; } = "";

    [ObservableProperty]
    public partial bool UpdateAvailable { get; private set; }

    [ObservableProperty]
    public partial string UpdateTooltip { get; private set; } = "";

    [ObservableProperty]
    public partial string? Notice { get; private set; }

    public bool HasNotice => Notice is not null;

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    public void Dispose()
    {
        _sync.ActivityChanged -= OnActivityChanged;
        _accounts.AccountsChanged -= OnAccountsChanged;
        _updates.Changed -= OnUpdatesChanged;
        _archive.Opened -= OnArchiveOpened;
        Details?.Dispose();
    }

    /// <summary>Re-renders relative times ("2 minutes ago"). Called by a slow timer.</summary>
    public void Tick()
    {
        foreach (var row in _rows.Values)
        {
            row.Refresh();
        }

        UpdateHeader();
    }

    // --- Commands -----------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        if (await _shell.PickFolderAsync("Choose where Gmail Watcher keeps your mail archive") is { } folder)
        {
            FolderText = folder;
            FolderProblem = null;
        }
    }

    [RelayCommand]
    private void UseFolder()
    {
        FolderProblem = _archive.Choose(FolderText);
        if (FolderProblem is null)
        {
            Reload();
        }
        else
        {
            OnPropertyChanged(nameof(ShowArchiveProblem));
        }
    }

    [RelayCommand]
    private async Task ChooseOtherFolderAsync()
    {
        if (await _shell.PickFolderAsync("Choose where Gmail Watcher keeps your mail archive") is { } folder)
        {
            FolderText = folder;
            UseFolder();
        }
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        if (!_accounts.Connector.IsAvailable && !await _shell.OpenGoogleSetupAsync())
        {
            Reload();
            return;
        }

        Reload();
        var result = await _shell.AddAccountAsync(null);
        Reload();
        if (result is { Account: { } account, WasExisting: false })
        {
            Notice = $"{account.Email} was added. Press Sync to download its mail.";
        }
        else if (result is { Account: { } existing })
        {
            Notice = $"{existing.Email} is connected again.";
        }
    }

    [RelayCommand]
    private async Task SetUpGoogleAsync()
    {
        await _shell.OpenGoogleSetupAsync();
        Reload();
    }

    [RelayCommand]
    private void SyncAll()
    {
        var started = _sync.StartSyncAll();
        Notice = started == 0
            ? (_rows.Values.Any(r => r.Display.NeedsReconnect) ? "Nothing to sync: reconnect the account that needs it first." : "Nothing to sync right now.")
            : null;
    }

    [RelayCommand]
    private void StopAll()
    {
        foreach (var row in _rows.Values.Where(r => r.Display.IsBusy))
        {
            _sync.Cancel(row.Id);
        }
    }

    [RelayCommand]
    private void OpenSettings() => _shell.OpenSettings();

    [RelayCommand]
    private void OpenUpdate() => _shell.OpenUpdate();

    [RelayCommand]
    private void LaunchDemo() => _shell.LaunchDemo();

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    [RelayCommand]
    private void CloseDetails()
    {
        var details = Details;
        Details = null;
        OnPropertyChanged(nameof(ShowDetails));
        details?.Dispose();
    }

    [RelayCommand]
    private void OpenArchiveFolder()
    {
        if (_archive.Directory is { } directory)
        {
            _files.OpenFolder(directory);
        }
    }

    // --- IAccountCommands ---------------------------------------------------------------------------------------------

    void IAccountCommands.Sync(AccountRecord account)
    {
        Notice = null;
        _sync.StartSync(account);
    }

    void IAccountCommands.Stop(int accountId) => _sync.Cancel(accountId);

    void IAccountCommands.Verify(AccountRecord account) => _sync.StartVerify(account);

    async Task IAccountCommands.ReconnectAsync(AccountRecord account)
    {
        if (!_accounts.Connector.IsAvailable && !await _shell.OpenGoogleSetupAsync())
        {
            return;
        }

        var result = await _shell.AddAccountAsync(account.Email);
        Reload();
        if (result is { Account: not null })
        {
            Notice = $"{account.Email} is connected again. Press Sync to catch up.";
        }
    }

    void IAccountCommands.OpenFolder(AccountRecord account)
    {
        if (_archive.IsOpen)
        {
            _files.OpenFolder(_archive.Runtime.Layout.AccountDirectory(account.Folder));
        }
    }

    void IAccountCommands.RecheckEverything(AccountRecord account)
    {
        _archive.Runtime.Accounts.ClearHistory(account.Id);
        _sync.StartSync(account);
        Notice = "Checking the whole mailbox against the archive (this lists every message in Gmail, so it takes longer than a normal sync).";
    }

    async Task IAccountCommands.DisconnectAsync(AccountRecord account)
    {
        var confirmed = await _shell.ConfirmAsync(new ConfirmRequest(
            $"Disconnect {account.Email}?",
            "Gmail Watcher will stop using Google for this account and tell Google to forget its access. " +
            "Your archive stays on this PC and you can still search, browse and verify it. You can connect the account again later.",
            "Disconnect"));
        if (confirmed)
        {
            _sync.Cancel(account.Id);
            await _accounts.DisconnectAsync(account, CancellationToken.None);
            Reload();
        }
    }

    async Task IAccountCommands.DeleteArchiveAsync(AccountRecord account)
    {
        var confirmed = await _shell.ConfirmAsync(new ConfirmRequest(
            $"Delete the archive of {account.Email} from this PC?",
            "This permanently deletes every downloaded message and attachment of this account from this PC. " +
            "If Gmail no longer has the mail (because you emptied it), these files are the only copy and they will be gone for good. " +
            "Mail still in Gmail is not touched.",
            "Delete from this PC",
            IsDangerous: true,
            RequiredPhrase: "DELETE"));
        if (confirmed)
        {
            _sync.Cancel(account.Id);
            if (Details?.Row.Id == account.Id)
            {
                CloseDetails();
            }

            await _accounts.DeleteArchiveAsync(account, CancellationToken.None);
            Reload();
        }
    }

    void IAccountCommands.CloseDetails() => CloseDetails();

    void IAccountCommands.ShowDetails(AccountRowViewModel row)
    {
        Details?.Dispose();
        Details = new AccountDetailsViewModel(row, _archive, _sync, _shell, _files, this, _dispatcher, _time);
        OnPropertyChanged(nameof(ShowDetails));
    }

    // --- Keeping the cards current ------------------------------------------------------------------------------------

    private void OnActivityChanged(object? sender, int accountId)
    {
        _dirty[accountId] = 0;
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
        {
            _dispatcher.Post(Drain);
        }
    }

    private void OnAccountsChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _reloadScheduled, 1) == 0)
        {
            _dispatcher.Post(() =>
            {
                Interlocked.Exchange(ref _reloadScheduled, 0);
                Reload();
            });
        }
    }

    private void OnArchiveOpened(object? sender, EventArgs e) => _dispatcher.Post(Reload);

    private void OnUpdatesChanged(object? sender, EventArgs e) => _dispatcher.Post(UpdateUpdateState);

    private void Drain()
    {
        Interlocked.Exchange(ref _drainScheduled, 0);
        foreach (var id in _dirty.Keys.ToList())
        {
            _dirty.TryRemove(id, out _);
            if (!_rows.TryGetValue(id, out var row))
            {
                continue;
            }

            var activity = _sync.GetActivity(id);
            var wasBusy = row.Activity.IsBusy;
            row.Refresh(activity: activity);
            if (wasBusy && !activity.IsBusy)
            {
                ReloadAccount(id); // a job finished: re-read the account (verification, sync state) and its stats
            }
        }

        UpdateHeader();
    }

    /// <summary>Rebuilds the screen state and the list of cards from the archive.</summary>
    public void Reload()
    {
        ArchiveConfigured = _archive.IsConfigured;
        ArchiveOpen = _archive.IsOpen;
        GoogleReady = _accounts.Connector.IsAvailable;
        OnPropertyChanged(nameof(ArchiveProblemText));
        UpdateUpdateState();

        var records = _accounts.List();
        var known = records.Select(r => r.Id).ToHashSet();
        foreach (var gone in _rows.Keys.Where(id => !known.Contains(id)).ToList())
        {
            Accounts.Remove(_rows[gone]);
            _rows.Remove(gone);
        }

        foreach (var record in records)
        {
            if (_rows.TryGetValue(record.Id, out var row))
            {
                row.Refresh(record, activity: _sync.GetActivity(record.Id));
            }
            else
            {
                row = new AccountRowViewModel(record, this, _time);
                row.Refresh(activity: _sync.GetActivity(record.Id));
                _rows[record.Id] = row;
                Accounts.Add(row);
            }

            LoadStats(record.Id);
        }

        AccountCount = records.Count;
        UpdateHeader();
    }

    private void ReloadAccount(int id)
    {
        if (_accounts.Get(id) is { } record && _rows.TryGetValue(id, out var row))
        {
            row.Refresh(record);
            LoadStats(id);
            Details?.Refresh();
        }
    }

    private void LoadStats(int id)
    {
        if (!_archive.IsOpen || !_loadingStats.Add(id))
        {
            return;
        }

        var runtime = _archive.Runtime;
        _ = Task.Run(() =>
        {
            try
            {
                return (AccountStats?)runtime.Messages.GetStats(id);
            }
            catch (Exception)
            {
                return null; // the card keeps its last numbers; the next refresh tries again
            }
        }).ContinueWith(task => _dispatcher.Post(() =>
        {
            _loadingStats.Remove(id);
            if (task.Result is { } stats && _rows.TryGetValue(id, out var row))
            {
                row.Refresh(stats: stats);
                UpdateHeader();
            }
        }), TaskScheduler.Default);
    }

    private void UpdateHeader()
    {
        var rows = _rows.Values.ToList();
        var busy = rows.Count(r => r.Display.IsBusy);
        AnyBusy = busy > 0;

        if (busy > 0)
        {
            StatusText = rows.Any(r => r.Activity.Kind == ActivityKind.Verifying) && busy == 1 ? "CHECKING" : busy == 1 ? "SYNCING" : $"{busy} WORKING";
            StatusTone = StatusTone.Running;
        }
        else if (rows.Count == 0)
        {
            StatusText = _archive.IsOpen ? "READY" : "SET UP";
            StatusTone = StatusTone.Unknown;
        }
        else if (rows.Any(r => r.Display.NeedsReconnect && !r.Display.IsArchiveOnly))
        {
            StatusText = "RECONNECT";
            StatusTone = StatusTone.Warning;
        }
        else if (rows.All(r => r.Display.Verify == VerifyState.Verified))
        {
            StatusText = "ALL VERIFIED";
            StatusTone = StatusTone.Success;
        }
        else if (rows.Any(r => r.Display.Verify == VerifyState.Problems))
        {
            StatusText = "PROBLEMS";
            StatusTone = StatusTone.Failure;
        }
        else
        {
            StatusText = rows.Count == 1 ? "1 ACCOUNT" : $"{rows.Count} ACCOUNTS";
            StatusTone = StatusTone.Neutral;
        }

        var bytes = rows.Sum(r => r.Stats?.TotalBytes ?? 0);
        FooterText = _archive.Directory is { } directory
            ? rows.Count == 0 ? directory : $"{directory} · {Formatting.Bytes(bytes)} archived"
            : "";
    }

    private void UpdateUpdateState()
    {
        UpdateAvailable = _updates.IsUpdateAvailable;
        UpdateTooltip = _updates.Latest is { } latest ? $"Version {latest.Version} is available. Click to see what changed." : "";
    }
}
