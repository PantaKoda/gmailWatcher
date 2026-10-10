using GmailWatcher.Archive.Demo;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Platform;
using GmailWatcher.Core.Updates;
using GmailWatcher.Desktop.Infrastructure;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;
using GmailWatcher.Desktop.Updates;
using GmailWatcher.Desktop.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Desktop.Tests;

internal sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}

/// <summary>
/// Queues the work view models post to the UI thread and runs it only when the test pumps. View models are then touched
/// from the test thread alone, as they are from the UI thread in the app, so tests never race the background jobs.
/// </summary>
internal sealed class PumpDispatcher : IUiDispatcher
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();

    public void Post(Action action) => _queue.Enqueue(action);

    /// <summary>Runs everything queued so far (and whatever that queues in turn).</summary>
    public void Run()
    {
        while (_queue.TryDequeue(out var action))
        {
            action();
        }
    }
}

internal sealed class RecordingBrowser : IExternalBrowser
{
    public List<Uri> Opened { get; } = [];

    public Task<LinkOpenResult> OpenAsync(Uri url)
    {
        Opened.Add(url);
        return Task.FromResult(LinkOpenResult.Opened);
    }
}

internal sealed class RecordingFiles : IFileLauncher
{
    public List<string> Folders { get; } = [];

    public List<string> Revealed { get; } = [];

    public List<string> Opened { get; } = [];

    public bool OpenFolder(string path)
    {
        Folders.Add(path);
        return true;
    }

    public bool Reveal(string path)
    {
        Revealed.Add(path);
        return true;
    }

    public bool OpenFile(string path)
    {
        Opened.Add(path);
        return true;
    }
}

/// <summary>A shell that answers dialogs from a script and remembers what it was asked.</summary>
internal sealed class FakeShell : IShell
{
    public bool ConfirmAnswer { get; set; } = true;

    public string? FolderAnswer { get; set; }

    public string? ClientFileAnswer { get; set; }

    public bool GoogleSetupResult { get; set; } = true;

    public Func<string?, Task<ConnectResult?>>? OnAddAccount { get; set; }

    public List<ConfirmRequest> Confirms { get; } = [];

    public List<string?> AddAccountCalls { get; } = [];

    public List<string> Copied { get; } = [];

    public int SettingsOpened { get; private set; }

    public int UpdateOpened { get; private set; }

    public int DemoLaunched { get; private set; }

    public bool Quit_ { get; private set; }

    public void OpenSettings() => SettingsOpened++;

    public void OpenUpdate() => UpdateOpened++;

    public Task<bool> OpenGoogleSetupAsync() => Task.FromResult(GoogleSetupResult);

    public Task<ConnectResult?> AddAccountAsync(string? expectedEmail)
    {
        AddAccountCalls.Add(expectedEmail);
        return OnAddAccount?.Invoke(expectedEmail) ?? Task.FromResult<ConnectResult?>(null);
    }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult(FolderAnswer);

    public Task<string?> PickClientFileAsync() => Task.FromResult(ClientFileAnswer);

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        Confirms.Add(request);
        return Task.FromResult(ConfirmAnswer);
    }

    public Task CopyTextAsync(string text)
    {
        Copied.Add(text);
        return Task.CompletedTask;
    }

    public void LaunchDemo() => DemoLaunched++;

    public void Quit() => Quit_ = true;
}

internal sealed class NoReleases : IReleaseSource
{
    public Task<ReleaseListResult> GetReleasesAsync(CancellationToken cancellationToken) => Task.FromResult(new ReleaseListResult([], null));

    public Task DownloadAsync(Uri url, Stream destination, long maxBytes, IProgress<double>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class NoLaunch : IProcessLauncher
{
    public bool Start(string executable, IReadOnlyList<string> arguments) => true;
}

/// <summary>
/// The app's whole service stack on a temporary folder, with demo mail instead of Google: real database, real files, real
/// sync engine, real view models. Disposing removes everything.
/// </summary>
internal sealed class TestKit : IDisposable
{
    public TestKit(bool openArchive = true, bool googleConfigured = true, int accounts = 0, TimeSpan? latency = null, int messages = 0)
    {
        Root = Path.Combine(Path.GetTempPath(), "gmailwatcher-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Root, Path.Combine(Root, "appsettings.json"), Path.Combine(Root, "gmailwatcher.config.json"), Path.Combine(Root, "logs"));
        Options = new GmailWatcherOptions();
        Options.Updates.Repository = null;
        if (googleConfigured)
        {
            Options.Google.ClientId = "123456789-abcdef.apps.googleusercontent.com";
        }

        Config = new UserConfigWriter(Paths);
        Archive = new ArchiveHost(Options, Config, NullLoggerFactory.Instance, Time, openArchive ? Path.Combine(Root, "archive") : null);
        if (openArchive)
        {
            Archive.TryOpen();
        }

        Mailboxes = new DemoMailboxFactory(latency ?? TimeSpan.Zero, messages == 0 ? 120 : messages);
        Credentials = new SessionCredentialStore();
        Connector = googleConfigured ? new DemoAccountConnector(Mailboxes, Credentials) : new UnavailableConnector();
        Accounts = new AccountService(Archive, Connector, NullLogger<AccountService>.Instance);
        Sync = new SyncService(Archive, Mailboxes, NullLogger<SyncService>.Instance);
        Google = new GoogleClientSetup(Options, Config);
        Updates = new UpdateService(Options, new NoReleases(), Paths, InstallInfo.Detect([]), new NoLaunch(), Time, NullLogger<UpdateService>.Instance);
        Shell = new FakeShell();
        Files = new RecordingFiles();
        Browser = new RecordingBrowser();
        for (var i = 0; i < accounts && openArchive; i++)
        {
            Archive.Runtime.Accounts.Add($"person{i + 1}@example.com", null);
        }
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public GmailWatcherOptions Options { get; }

    public UserConfigWriter Config { get; }

    public TimeProvider Time { get; } = TimeProvider.System;

    public ArchiveHost Archive { get; }

    public DemoMailboxFactory Mailboxes { get; }

    public SessionCredentialStore Credentials { get; }

    public IAccountConnector Connector { get; }

    public AccountService Accounts { get; }

    public SyncService Sync { get; }

    public GoogleClientSetup Google { get; }

    public UpdateService Updates { get; }

    public FakeShell Shell { get; }

    public RecordingFiles Files { get; }

    public RecordingBrowser Browser { get; }

    /// <summary>Queued UI work; run it with <see cref="UntilAsync"/> or <see cref="WaitIdleAsync"/>.</summary>
    public PumpDispatcher Pump { get; } = new();

    public MainViewModel CreateMain(bool demo = false, IUiDispatcher? dispatcher = null) =>
        new(Shell, Archive, Accounts, Sync, Google, Files, Browser, Updates, dispatcher ?? Pump, Time, new AppMode(demo));

    /// <summary>Waits for a condition on view-model state, running the queued UI work as it goes.</summary>
    public async Task UntilAsync(Func<bool> condition, int seconds = 120, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            Pump.Run();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {seconds} s waiting for {what ?? "a condition"}.");
            }

            await Task.Delay(15);
        }
    }

    /// <summary>Waits until the account has no job running (the jobs run on background threads).</summary>
    public async Task WaitIdleAsync(int accountId, int seconds = 240)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (Sync.IsBusy(accountId))
        {
            Pump.Run();
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Account {accountId} was still busy after {seconds} seconds.");
            }

            await Task.Delay(20);
        }

        Pump.Run(); // what the finished job announced
    }

    public void Dispose()
    {
        Sync.Dispose();
        if (Archive.IsOpen)
        {
            Archive.Runtime.Database.ReleaseConnections();
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class UnavailableConnector : IAccountConnector
    {
        public bool IsAvailable => false;

        public string? UnavailableReason => "Google sign-in isn't set up yet.";

        public Task<string> SignInAsync(string? loginHint, IProgress<Uri>? authorizationUrl, CancellationToken cancellationToken) =>
            throw new ConnectException(UnavailableReason!);

        public Task SignOutAsync(string email, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
