using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Sync;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.ViewModels;

namespace GmailWatcher.Desktop.Tests.ViewModels;

public sealed class MainViewModelTests
{
    internal static async Task WaitUntilAsync(Func<bool> condition, int seconds = 120, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {seconds} s waiting for {what ?? "a condition"}.");
            }

            await Task.Delay(15);
        }
    }

    // --- First run -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Before_a_folder_is_chosen_only_the_welcome_screen_is_shown()
    {
        using var kit = new TestKit(openArchive: false);

        var viewModel = kit.CreateMain();

        Assert.True(viewModel.ShowWelcome);
        Assert.False(viewModel.ShowNoAccounts);
        Assert.False(viewModel.ShowList);
        Assert.False(viewModel.ArchiveOpen);
        Assert.Equal("SET UP", viewModel.StatusText);
        Assert.Equal(AppPaths_Suggestion(), viewModel.FolderText);
    }

    private static string AppPaths_Suggestion() => GmailWatcher.Desktop.Infrastructure.AppPaths.SuggestedArchiveDirectory;

    [Fact]
    public void Using_a_folder_opens_the_archive_remembers_the_choice_and_invites_the_first_account()
    {
        using var kit = new TestKit(openArchive: false);
        var viewModel = kit.CreateMain();
        var chosen = Path.Combine(kit.Root, "my-archive");
        viewModel.FolderText = chosen;

        viewModel.UseFolderCommand.Execute(null);

        Assert.Null(viewModel.FolderProblem);
        Assert.True(viewModel.ArchiveOpen);
        Assert.True(viewModel.ShowNoAccounts);
        Assert.False(viewModel.ShowWelcome);
        Assert.True(File.Exists(Path.Combine(chosen, "archive.db")));
        Assert.True(File.Exists(Path.Combine(chosen, "analytics.duckdb")));
        Assert.Contains(chosen.Replace("\\", "\\\\"), File.ReadAllText(kit.Paths.UserConfigFile)); // survives a restart
    }

    [Theory]
    [InlineData("relative\\path")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_folder_that_is_not_a_full_path_is_refused_with_a_reason(string text)
    {
        using var kit = new TestKit(openArchive: false);
        var viewModel = kit.CreateMain();
        viewModel.FolderText = text;

        viewModel.UseFolderCommand.Execute(null);

        Assert.True(viewModel.HasFolderProblem);
        Assert.True(viewModel.ShowWelcome);
        Assert.False(viewModel.ArchiveOpen);
    }

    [Fact]
    public void A_folder_that_cannot_be_written_is_refused()
    {
        using var kit = new TestKit(openArchive: false);
        var blocker = Path.Combine(kit.Root, "a-file");
        File.WriteAllText(blocker, "x");
        var viewModel = kit.CreateMain();
        viewModel.FolderText = Path.Combine(blocker, "inside"); // a folder "inside" a file

        viewModel.UseFolderCommand.Execute(null);

        Assert.True(viewModel.HasFolderProblem);
        Assert.False(viewModel.ArchiveOpen);
    }

    [Fact]
    public async Task Browsing_for_a_folder_fills_the_box()
    {
        using var kit = new TestKit(openArchive: false);
        kit.Shell.FolderAnswer = Path.Combine(kit.Root, "elsewhere");
        var viewModel = kit.CreateMain();

        await viewModel.BrowseFolderCommand.ExecuteAsync(null);

        Assert.Equal(kit.Shell.FolderAnswer, viewModel.FolderText);
    }

    // --- Adding accounts -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Adding_an_account_shows_a_card_and_says_to_press_sync()
    {
        using var kit = new TestKit();
        kit.Shell.OnAddAccount = async expected => await kit.Accounts.ConnectAsync(expected, null, CancellationToken.None);
        var viewModel = kit.CreateMain();
        Assert.True(viewModel.ShowNoAccounts);

        await viewModel.AddAccountCommand.ExecuteAsync(null);

        var row = Assert.Single(viewModel.Accounts);
        Assert.StartsWith("demo", row.Email, StringComparison.Ordinal);
        Assert.True(viewModel.ShowList);
        Assert.Contains("Press Sync", viewModel.Notice, StringComparison.Ordinal);
        Assert.Contains("Not downloaded yet", row.Display.SyncLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_google_setup_adding_an_account_first_offers_the_setup()
    {
        using var kit = new TestKit(googleConfigured: false);
        kit.Shell.GoogleSetupResult = false;
        var viewModel = kit.CreateMain();
        Assert.True(viewModel.ShowGoogleBanner);

        await viewModel.AddAccountCommand.ExecuteAsync(null);

        Assert.Empty(kit.Shell.AddAccountCalls); // the setup was dismissed: no sign-in was attempted
    }

    [Fact]
    public void The_banner_about_google_goes_away_once_it_is_set_up()
    {
        using var kit = new TestKit(googleConfigured: true);

        Assert.False(kit.CreateMain().ShowGoogleBanner);
    }

    [Fact]
    public async Task Reconnecting_asks_for_exactly_that_account()
    {
        using var kit = new TestKit(accounts: 1);
        var account = kit.Accounts.List()[0];
        kit.Archive.Runtime.Accounts.SetConnection(account.Id, AccountConnection.ReconnectRequired, "expired");
        var viewModel = kit.CreateMain();
        var row = viewModel.Accounts[0];
        Assert.True(row.Display.NeedsReconnect);

        await row.ReconnectCommand.ExecuteAsync(null);

        Assert.Equal([account.Email], kit.Shell.AddAccountCalls);
    }

    // --- Syncing -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pressing_sync_downloads_in_the_background_and_the_card_follows()
    {
        using var kit = new TestKit(accounts: 1, messages: 60);
        var viewModel = kit.CreateMain();
        var row = viewModel.Accounts[0];

        row.SyncCommand.Execute(null);
        await kit.WaitIdleAsync(row.Id);
        await kit.UntilAsync(() => row.Display.HasMail, what: "the card to show the archive");

        Assert.Contains("60 messages", row.Display.Summary, StringComparison.Ordinal);
        Assert.StartsWith("Last synced", row.Display.SyncLine, StringComparison.Ordinal);
        Assert.True(row.Display.CanVerify);
        Assert.Equal(60, kit.Archive.Runtime.Messages.Count(row.Id));
    }

    [Fact]
    public async Task Stop_keeps_what_was_downloaded_and_the_next_sync_finishes_the_job()
    {
        using var kit = new TestKit(accounts: 1, messages: 300, latency: TimeSpan.FromMilliseconds(10));
        var viewModel = kit.CreateMain();
        var row = viewModel.Accounts[0];
        row.SyncCommand.Execute(null);
        await kit.UntilAsync(() => kit.Archive.Runtime.Messages.Count(row.Id) > 20, what: "some messages to arrive");

        row.StopCommand.Execute(null);
        await kit.WaitIdleAsync(row.Id);

        var kept = kit.Archive.Runtime.Messages.Count(row.Id);
        Assert.InRange(kept, 21, 299);
        Assert.Equal(SyncOutcome.Cancelled, kit.Sync.GetActivity(row.Id).LastSync?.Outcome);

        row.SyncCommand.Execute(null);
        await kit.WaitIdleAsync(row.Id);
        Assert.Equal(300, kit.Archive.Runtime.Messages.Count(row.Id));
    }

    [Fact]
    public async Task Sync_all_starts_every_connected_account_and_skips_the_rest()
    {
        using var kit = new TestKit(accounts: 3, messages: 30);
        var accounts = kit.Accounts.List();
        kit.Archive.Runtime.Accounts.SetConnection(accounts[2].Id, AccountConnection.ReconnectRequired);
        var viewModel = kit.CreateMain();

        viewModel.SyncAllCommand.Execute(null);
        await kit.WaitIdleAsync(accounts[0].Id);
        await kit.WaitIdleAsync(accounts[1].Id);

        Assert.Equal(30, kit.Archive.Runtime.Messages.Count(accounts[0].Id));
        Assert.Equal(30, kit.Archive.Runtime.Messages.Count(accounts[1].Id));
        Assert.Equal(0, kit.Archive.Runtime.Messages.Count(accounts[2].Id));
    }

    [Fact]
    public void Sync_all_with_only_accounts_that_need_reconnecting_explains_why_nothing_happened()
    {
        using var kit = new TestKit(accounts: 1);
        kit.Archive.Runtime.Accounts.SetConnection(kit.Accounts.List()[0].Id, AccountConnection.ReconnectRequired);
        var viewModel = kit.CreateMain();

        viewModel.SyncAllCommand.Execute(null);

        Assert.Contains("reconnect", viewModel.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verifying_a_synced_account_turns_the_header_green()
    {
        using var kit = new TestKit(accounts: 1, messages: 40);
        var viewModel = kit.CreateMain();
        var row = viewModel.Accounts[0];
        row.SyncCommand.Execute(null);
        await kit.WaitIdleAsync(row.Id);
        await kit.UntilAsync(() => row.Display.CanVerify, what: "the card to allow verifying");

        row.VerifyCommand.Execute(null);
        await kit.WaitIdleAsync(row.Id);
        await kit.UntilAsync(() => row.Display.Verify == VerifyState.Verified, what: "the card to show Verified");

        Assert.Equal("ALL VERIFIED", viewModel.StatusText);
        Assert.Equal(StatusTone.Success, viewModel.StatusTone);
    }

    // --- Disconnecting and deleting ------------------------------------------------------------------------------------

    [Fact]
    public async Task Disconnecting_asks_first_and_keeps_every_downloaded_message()
    {
        using var kit = new TestKit(accounts: 1, messages: 25);
        var viewModel = kit.CreateMain();
        var row = viewModel.Accounts[0];
        row.SyncCommand.Execute(null);
        await kit.WaitIdleAsync(row.Id);
        await kit.Credentials.WriteAsync(row.Email, new StoredCredential("token"), TestContext.Current.CancellationToken);

        kit.Shell.ConfirmAnswer = false;
        await row.DisconnectCommand.ExecuteAsync(null);
        Assert.Equal(AccountConnection.Connected, kit.Archive.Runtime.Accounts.Get(row.Id)!.Connection);
        Assert.NotNull(await kit.Credentials.ReadAsync(row.Email, TestContext.Current.CancellationToken));

        kit.Shell.ConfirmAnswer = true;
        await row.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal(AccountConnection.ArchiveOnly, kit.Archive.Runtime.Accounts.Get(row.Id)!.Connection);
        Assert.Null(await kit.Credentials.ReadAsync(row.Email, TestContext.Current.CancellationToken));
        Assert.Equal(25, kit.Archive.Runtime.Messages.Count(row.Id));
        Assert.True(row.Display.IsArchiveOnly);
        Assert.False(row.Display.CanSync);
    }

    [Fact]
    public async Task Deleting_an_archive_requires_a_typed_phrase_and_removes_only_that_account()
    {
        using var kit = new TestKit(accounts: 2, messages: 20);
        var viewModel = kit.CreateMain();
        var doomed = viewModel.Accounts[0];
        var kept = viewModel.Accounts[1];
        doomed.SyncCommand.Execute(null);
        kept.SyncCommand.Execute(null);
        await kit.WaitIdleAsync(doomed.Id);
        await kit.WaitIdleAsync(kept.Id);
        var folder = kit.Archive.Runtime.Layout.AccountDirectory(doomed.Record.Folder);
        Assert.True(Directory.Exists(folder));

        await doomed.DeleteArchiveCommand.ExecuteAsync(null);

        var request = Assert.Single(kit.Shell.Confirms);
        Assert.Equal("DELETE", request.RequiredPhrase);
        Assert.True(request.IsDangerous);
        Assert.Contains("only copy", request.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));
        Assert.Single(viewModel.Accounts);
        Assert.Equal(20, kit.Archive.Runtime.Messages.Count(kept.Id));
    }

    [Fact]
    public async Task Declining_the_delete_confirmation_changes_nothing()
    {
        using var kit = new TestKit(accounts: 1, messages: 15);
        kit.Shell.ConfirmAnswer = false;
        var viewModel = kit.CreateMain();
        var row = viewModel.Accounts[0];
        row.SyncCommand.Execute(null);
        await kit.WaitIdleAsync(row.Id);

        await row.DeleteArchiveCommand.ExecuteAsync(null);

        Assert.Equal(15, kit.Archive.Runtime.Messages.Count(row.Id));
        Assert.Single(viewModel.Accounts);
    }

    // --- Details -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Details_open_over_the_list_and_close_again()
    {
        using var kit = new TestKit(accounts: 1);
        var viewModel = kit.CreateMain();
        Assert.True(viewModel.ShowList);

        viewModel.Accounts[0].ShowDetailsCommand.Execute(null);

        Assert.NotNull(viewModel.Details);
        Assert.True(viewModel.ShowDetails);
        Assert.False(viewModel.ShowList);

        viewModel.CloseDetailsCommand.Execute(null);

        Assert.Null(viewModel.Details);
        Assert.True(viewModel.ShowList);
    }

    [Fact]
    public async Task Recheck_everything_forgets_the_sync_position_and_syncs()
    {
        using var kit = new TestKit(accounts: 1, messages: 20);
        var viewModel = kit.CreateMain();
        var row = viewModel.Accounts[0];
        row.SyncCommand.Execute(null);
        await kit.WaitIdleAsync(row.Id);
        Assert.NotNull(kit.Archive.Runtime.Accounts.GetHistoryId(row.Id));

        ((IAccountCommands)viewModel).RecheckEverything(kit.Archive.Runtime.Accounts.Get(row.Id)!);
        await kit.WaitIdleAsync(row.Id);

        var result = kit.Sync.GetActivity(row.Id).LastSync!;
        Assert.False(result.UsedHistory); // a full pass: it listed the whole mailbox
        Assert.Equal(SyncOutcome.Completed, result.Outcome);
    }

    [Fact]
    public void The_header_summarizes_the_accounts()
    {
        using var kit = new TestKit(accounts: 2);

        var viewModel = kit.CreateMain();

        Assert.Equal("2 ACCOUNTS", viewModel.StatusText);
        Assert.Contains("archive", viewModel.FooterText, StringComparison.Ordinal);
    }

    [Fact]
    public void An_account_that_needs_reconnecting_shows_in_the_header()
    {
        using var kit = new TestKit(accounts: 1);
        kit.Archive.Runtime.Accounts.SetConnection(kit.Accounts.List()[0].Id, AccountConnection.ReconnectRequired);

        Assert.Equal("RECONNECT", kit.CreateMain().StatusText);
    }

    [Fact]
    public void The_update_pill_follows_the_update_service()
    {
        using var kit = new TestKit();
        var viewModel = kit.CreateMain();

        Assert.False(viewModel.UpdateAvailable);

        viewModel.OpenUpdateCommand.Execute(null);
        viewModel.OpenSettingsCommand.Execute(null);

        Assert.Equal(1, kit.Shell.UpdateOpened);
        Assert.Equal(1, kit.Shell.SettingsOpened);
    }
}
