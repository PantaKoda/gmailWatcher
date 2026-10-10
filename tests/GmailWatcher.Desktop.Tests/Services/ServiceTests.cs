using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Settings;
using GmailWatcher.Desktop.Platform;
using GmailWatcher.Desktop.Services;
using GmailWatcher.Desktop.Tests.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GmailWatcher.Desktop.Tests.Services;

/// <summary>A sign-in that returns whichever address it is told to, whatever was asked for: a person choosing the wrong Google account.</summary>
internal sealed class FixedConnector(string email, ICredentialStore credentials) : IAccountConnector
{
    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public async Task<string> SignInAsync(string? loginHint, IProgress<Uri>? authorizationUrl, CancellationToken cancellationToken)
    {
        await credentials.WriteAsync(email, new StoredCredential("token-for-" + email), cancellationToken);
        return email;
    }

    public Task SignOutAsync(string email, CancellationToken cancellationToken) => credentials.DeleteAsync(email, cancellationToken);
}

public sealed class AccountServiceTests
{
    [Fact]
    public async Task Connecting_adds_the_account_and_stores_its_token()
    {
        using var kit = new TestKit();

        var result = await kit.Accounts.ConnectAsync(null, null, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(result.WasExisting);
        Assert.Equal(AccountConnection.Connected, result.Account!.Connection);
        Assert.NotNull(await kit.Credentials.ReadAsync(result.Account.Email, TestContext.Current.CancellationToken));
        Assert.Single(kit.Accounts.List());
    }

    [Fact]
    public async Task Connecting_an_address_that_is_already_archived_reconnects_it_instead_of_duplicating()
    {
        using var kit = new TestKit();
        var first = await kit.Accounts.ConnectAsync("same@example.com", null, TestContext.Current.CancellationToken);
        kit.Archive.Runtime.Accounts.SetConnection(first.Account!.Id, AccountConnection.ReconnectRequired, "expired");

        var again = await kit.Accounts.ConnectAsync("same@example.com", null, TestContext.Current.CancellationToken);

        Assert.True(again.WasExisting);
        Assert.Equal(first.Account.Id, again.Account!.Id);
        Assert.Equal(AccountConnection.Connected, again.Account.Connection);
        Assert.Single(kit.Accounts.List());
    }

    [Fact]
    public async Task Signing_in_as_the_wrong_google_account_is_refused_and_leaves_no_token_behind()
    {
        using var kit = new TestKit();
        var accounts = new AccountService(kit.Archive, new FixedConnector("wrong@example.com", kit.Credentials), NullLogger<AccountService>.Instance);

        var result = await accounts.ConnectAsync("right@example.com", null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("wrong@example.com", result.Error, StringComparison.Ordinal);
        Assert.Contains("right@example.com", result.Error, StringComparison.Ordinal);
        Assert.Empty(kit.Accounts.List());
        Assert.Null(await kit.Credentials.ReadAsync("wrong@example.com", TestContext.Current.CancellationToken)); // no stray token
    }

    [Fact]
    public async Task Signing_in_as_another_archived_account_keeps_its_token()
    {
        using var kit = new TestKit();
        kit.Archive.Runtime.Accounts.Add("other@example.com");
        var accounts = new AccountService(kit.Archive, new FixedConnector("other@example.com", kit.Credentials), NullLogger<AccountService>.Instance);

        var result = await accounts.ConnectAsync("right@example.com", null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.NotNull(await kit.Credentials.ReadAsync("other@example.com", TestContext.Current.CancellationToken)); // it is a valid sign-in for another real account
    }

    [Fact]
    public async Task Connecting_needs_an_open_archive()
    {
        using var kit = new TestKit(openArchive: false);

        var result = await kit.Accounts.ConnectAsync(null, null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("archive folder", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sign_in_that_fails_reports_its_message_and_adds_nothing()
    {
        using var kit = new TestKit(googleConfigured: false);

        var result = await kit.Accounts.ConnectAsync(null, null, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("isn't set up", result.Error, StringComparison.Ordinal);
        Assert.Empty(kit.Accounts.List());
    }

    [Fact]
    public async Task Changes_are_announced()
    {
        using var kit = new TestKit();
        var changes = 0;
        kit.Accounts.AccountsChanged += (_, _) => changes++;

        await kit.Accounts.ConnectAsync(null, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, changes);
    }
}

public sealed class SyncServiceTests
{
    [Fact]
    public async Task One_job_per_account_a_second_start_is_refused_while_it_runs()
    {
        using var kit = new TestKit(accounts: 1, messages: 100, latency: TimeSpan.FromMilliseconds(15));
        var account = kit.Accounts.List()[0];

        Assert.True(kit.Sync.StartSync(account));
        Assert.False(kit.Sync.StartSync(account));
        Assert.False(kit.Sync.StartVerify(account));
        Assert.True(kit.Sync.IsBusy(account.Id));

        kit.Sync.Cancel(account.Id);
        await kit.WaitIdleAsync(account.Id);
        Assert.False(kit.Sync.IsBusy(account.Id));
        Assert.True(kit.Sync.StartSync(kit.Accounts.Get(account.Id)!)); // free again
        kit.Sync.Cancel(account.Id);
        await kit.WaitIdleAsync(account.Id);
    }

    [Fact]
    public void An_account_that_is_not_connected_cannot_sync()
    {
        using var kit = new TestKit(accounts: 1);
        var account = kit.Accounts.List()[0];
        kit.Archive.Runtime.Accounts.SetConnection(account.Id, AccountConnection.ArchiveOnly);

        Assert.False(kit.Sync.StartSync(kit.Accounts.Get(account.Id)!));
    }

    [Fact]
    public async Task Only_two_accounts_download_at_the_same_time()
    {
        using var kit = new TestKit(accounts: 4, messages: 80, latency: TimeSpan.FromMilliseconds(15));
        Assert.Equal(4, kit.Sync.StartSyncAll());

        await MainViewModelTests.WaitUntilAsync(
            () => kit.Accounts.List().Count(a => kit.Sync.GetActivity(a.Id).Kind == ActivityKind.Syncing) > 0, what: "downloads to begin");
        var syncing = kit.Accounts.List().Count(a => kit.Sync.GetActivity(a.Id).Kind == ActivityKind.Syncing);
        var queued = kit.Accounts.List().Count(a => kit.Sync.GetActivity(a.Id).Kind == ActivityKind.Queued);

        Assert.True(syncing <= 2, $"{syncing} accounts were downloading at once");
        Assert.True(queued >= 2 - (syncing - 1) || syncing == 2);

        foreach (var account in kit.Accounts.List())
        {
            await kit.WaitIdleAsync(account.Id, seconds: 90);
        }

        Assert.All(kit.Accounts.List(), a => Assert.Equal(80, kit.Archive.Runtime.Messages.Count(a.Id)));
    }

    [Fact]
    public async Task Quitting_stops_every_job_and_commits_what_was_downloaded()
    {
        using var kit = new TestKit(accounts: 2, messages: 400, latency: TimeSpan.FromMilliseconds(10));
        kit.Sync.StartSyncAll();
        var first = kit.Accounts.List()[0];
        await MainViewModelTests.WaitUntilAsync(() => kit.Archive.Runtime.Messages.Count(first.Id) > 15, what: "messages to arrive");

        await kit.Sync.StopAllAsync(TimeSpan.FromSeconds(10));

        Assert.False(kit.Sync.HasWork);
        Assert.InRange(kit.Archive.Runtime.Messages.Count(first.Id), 16, 399);
        // Nothing is half-written: every row has its file.
        var runtime = kit.Archive.Runtime;
        foreach (var page in runtime.Messages.EnumerateFiles(first.Id))
        {
            Assert.All(page, f => Assert.True(File.Exists(runtime.Layout.Resolve(first.Folder, f.EmlPath))));
        }
    }

    [Fact]
    public async Task A_verification_job_reports_through_the_activity()
    {
        using var kit = new TestKit(accounts: 1, messages: 40);
        var account = kit.Accounts.List()[0];
        kit.Sync.StartSync(account);
        await kit.WaitIdleAsync(account.Id);

        Assert.True(kit.Sync.StartVerify(kit.Accounts.Get(account.Id)!));
        await kit.WaitIdleAsync(account.Id);

        var report = kit.Sync.GetActivity(account.Id).LastReport;
        Assert.NotNull(report);
        Assert.True(report.IsClean);
        Assert.Equal(40, report.ArchivedMessages);
    }

    [Fact]
    public async Task Activity_changes_are_announced_for_the_account_concerned()
    {
        using var kit = new TestKit(accounts: 2, messages: 20);
        var ids = new HashSet<int>();
        kit.Sync.ActivityChanged += (_, id) =>
        {
            lock (ids)
            {
                ids.Add(id);
            }
        };
        var first = kit.Accounts.List()[0];

        kit.Sync.StartSync(first);
        await kit.WaitIdleAsync(first.Id);

        lock (ids)
        {
            Assert.Equal([first.Id], ids);
        }
    }
}

public sealed class AutoSyncSchedulerTests
{
    private static AutoSyncScheduler Scheduler(TestKit kit, FakeTimeProvider time, SettingsService settings) =>
        new(settings, kit.Sync, kit.Archive, time);

    [Fact]
    public async Task Off_by_default_nothing_is_ever_started()
    {
        using var kit = new TestKit(accounts: 1, messages: 10);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var settings = new SettingsService(kit.Paths, NullLogger<SettingsService>.Instance);
        settings.Load();
        using var scheduler = Scheduler(kit, time, settings);
        scheduler.Start();

        time.Advance(TimeSpan.FromDays(3));
        scheduler.Tick();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(kit.Sync.HasWork);
        Assert.Equal(0, kit.Archive.Runtime.Messages.Count(kit.Accounts.List()[0].Id));
    }

    [Fact]
    public async Task With_an_interval_it_syncs_once_each_time_the_interval_has_passed()
    {
        using var kit = new TestKit(accounts: 1, messages: 10);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var settings = new SettingsService(kit.Paths, NullLogger<SettingsService>.Instance);
        settings.Load();
        settings.Update(s => s with { Sync = s.Sync with { AutoSync = AutoSyncInterval.EveryHour } });
        using var scheduler = Scheduler(kit, time, settings);
        scheduler.Start();
        var account = kit.Accounts.List()[0];

        time.Advance(TimeSpan.FromMinutes(30));
        scheduler.Tick();
        Assert.False(kit.Sync.IsBusy(account.Id)); // not yet

        time.Advance(TimeSpan.FromMinutes(31));
        scheduler.Tick();
        await kit.WaitIdleAsync(account.Id);
        Assert.Equal(10, kit.Archive.Runtime.Messages.Count(account.Id));

        scheduler.Tick(); // the same hour again: nothing
        Assert.False(kit.Sync.IsBusy(account.Id));
    }
}

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmailwatcher-settings-" + Guid.NewGuid().ToString("N"));

    private GmailWatcher.Desktop.Infrastructure.AppPaths Paths => new(_root, "d", Path.Combine(_root, "c.json"), Path.Combine(_root, "logs"));

    public SettingsServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Changes_are_saved_and_come_back_after_a_restart()
    {
        using (var settings = new SettingsService(Paths, NullLogger<SettingsService>.Instance))
        {
            settings.Load();
            settings.Update(s => s with { Appearance = s.Appearance with { Theme = ThemePreference.Dark, AccentColor = "#9D7CFF", Density = Density.Compact } });
            settings.Update(s => s with { Sync = s.Sync with { AutoSync = AutoSyncInterval.Daily, SyncOnStartup = true } });
        } // dispose flushes

        using var again = new SettingsService(Paths, NullLogger<SettingsService>.Instance);
        again.Load();

        Assert.Equal(ThemePreference.Dark, again.App.Appearance.Theme);
        Assert.Equal("#9D7CFF", again.App.Appearance.AccentColor);
        Assert.Equal(Density.Compact, again.App.Appearance.Density);
        Assert.Equal(AutoSyncInterval.Daily, again.App.Sync.AutoSync);
        Assert.True(again.App.Sync.SyncOnStartup);
        Assert.Null(again.Problem);
    }

    [Fact]
    public void An_unreadable_file_is_set_aside_not_overwritten_and_defaults_are_used()
    {
        File.WriteAllText(Paths.SettingsFile, "{ this is not json");

        using var settings = new SettingsService(Paths, NullLogger<SettingsService>.Instance);
        settings.Load();

        Assert.Equal(ThemePreference.System, settings.App.Appearance.Theme);
        Assert.Contains("unreadable", settings.Problem, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(_root, "settings.json.unreadable-*"));
    }

    [Fact]
    public void Settings_from_a_newer_version_are_left_alone()
    {
        const string Newer = """{"schemaVersion":99,"appearance":{"theme":"Light"},"somethingNew":{"a":1}}""";
        File.WriteAllText(Paths.SettingsFile, Newer);

        using (var settings = new SettingsService(Paths, NullLogger<SettingsService>.Instance))
        {
            settings.Load();
            settings.Update(s => s with { Appearance = s.Appearance with { Theme = ThemePreference.Dark } });
            Assert.Contains("newer version", settings.Problem, StringComparison.Ordinal);
        }

        Assert.Equal(Newer, File.ReadAllText(Paths.SettingsFile));
    }

    [Fact]
    public void Invalid_values_are_repaired_and_the_rest_is_kept()
    {
        File.WriteAllText(Paths.SettingsFile, """{"schemaVersion":1,"appearance":{"theme":"Neon","accentColor":"not a color","density":"Compact"},"window":{"width":-5}}""");

        using var settings = new SettingsService(Paths, NullLogger<SettingsService>.Instance);
        settings.Load();

        Assert.Equal(ThemePreference.System, settings.App.Appearance.Theme);
        Assert.Equal(Density.Compact, settings.App.Appearance.Density);
        Assert.Null(settings.App.Appearance.AccentColor);
        Assert.Null(settings.App.Window.Width);
    }

    [Fact]
    public void Observers_hear_about_changes_with_the_old_and_new_values()
    {
        using var settings = new SettingsService(Paths, NullLogger<SettingsService>.Instance);
        settings.Load();
        AppSettingsChangedEventArgs? seen = null;
        settings.AppChanged += (_, e) => seen = e;

        settings.Update(s => s with { Appearance = s.Appearance with { Theme = ThemePreference.Light } });
        settings.Update(s => s with { Appearance = s.Appearance with { Theme = ThemePreference.Light } }); // no change: no event

        Assert.Equal(ThemePreference.System, seen!.Previous.Appearance.Theme);
        Assert.Equal(ThemePreference.Light, seen.Current.Appearance.Theme);
    }
}

public sealed class FileLauncherTests
{
    [Theory]
    [InlineData("report.pdf")]
    [InlineData("photo.JPG")]
    [InlineData("notes.txt")]
    [InlineData("budget.xlsx")]
    [InlineData("message.eml")]
    public void Plainly_inert_files_may_be_opened_with_one_click(string name) => Assert.True(FileLauncher.IsSafeToOpen(name));

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("run.bat")]
    [InlineData("script.ps1")]
    [InlineData("macro.docm")]
    [InlineData("link.lnk")]
    [InlineData("installer.msi")]
    [InlineData("page.html")]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    [InlineData("double.pdf.exe")]
    public void Anything_that_can_run_code_or_is_unknown_is_not_opened_directly(string name) => Assert.False(FileLauncher.IsSafeToOpen(name));
}
