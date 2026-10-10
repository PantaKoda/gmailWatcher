using GmailWatcher.Core.Verification;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.ViewModels;

namespace GmailWatcher.Desktop.Tests.ViewModels;

public sealed class DetailsViewModelTests
{
    private static async Task<(TestKit Kit, MainViewModel Main, AccountRowViewModel Row)> SyncedAsync(int messages = 120)
    {
        var kit = new TestKit(accounts: 1, messages: messages);
        var main = kit.CreateMain();
        var row = main.Accounts[0];
        row.SyncCommand.Execute(null);
        await kit.WaitIdleAsync(row.Id);
        await kit.UntilAsync(() => row.Display.CanVerify, what: "the archive to show up");
        return (kit, main, row);
    }

    private static async Task VerifyAsync(TestKit kit, MainViewModel main, AccountRowViewModel row)
    {
        row.VerifyCommand.Execute(null);
        await kit.WaitIdleAsync(row.Id);
        await kit.UntilAsync(() => row.Display.Verify != VerifyState.Never, what: "a verification result");
        main.Details?.Refresh();
    }

    // --- Overview ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Before_verifying_the_overview_says_so_and_after_it_says_verified_with_the_numbers()
    {
        var (kit, main, row) = await SyncedAsync();
        using var _ = kit;
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        var details = main.Details!;

        Assert.Equal("Not verified yet", details.VerifyHeadline);
        Assert.Contains("before you delete anything in Gmail", details.VerifyDetail, StringComparison.Ordinal);

        await VerifyAsync(kit, main, row);

        Assert.StartsWith("Verified", details.VerifyHeadline, StringComparison.Ordinal);
        Assert.Contains("Gmail listed 120 messages and every one is archived", details.VerifyDetail, StringComparison.Ordinal);
        Assert.Contains("sync and verify again right before you delete", details.VerifyDetail, StringComparison.Ordinal);
        Assert.False(details.HasIssues);
        Assert.False(details.CanRepair);
        Assert.Contains(details.StatLines, l => l.StartsWith("120 messages", StringComparison.Ordinal));
        Assert.Equal(kit.Archive.Runtime.Layout.AccountDirectory(row.Record.Folder), details.FolderText);
        Assert.Contains("filters", details.SavedSettingsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_damaged_file_turns_the_overview_red_names_the_problem_and_offers_the_repair()
    {
        var (kit, main, row) = await SyncedAsync(60);
        using var _ = kit;
        var runtime = kit.Archive.Runtime;
        var victim = runtime.Messages.EnumerateFiles(row.Id).First()[0];
        File.Delete(runtime.Layout.Resolve(row.Record.Folder, victim.EmlPath));
        main.Accounts[0].ShowDetailsCommand.Execute(null);

        await VerifyAsync(kit, main, row);
        var details = main.Details!;

        Assert.Equal("Problems found", details.VerifyHeadline);
        Assert.StartsWith("Don't delete anything in Gmail yet.", details.VerifyDetail, StringComparison.Ordinal);
        Assert.Contains("1 file missing from the archive folder", details.VerifyDetail, StringComparison.Ordinal);
        Assert.Equal("Message file missing", Assert.Single(details.Issues).Title);
        Assert.True(details.CanRepair);
        Assert.Equal(StatusTone.Failure, row.Display.Tone);

        details.RepairCommand.Execute(null);
        await kit.WaitIdleAsync(row.Id);

        Assert.Contains("1 message will be downloaded again", details.RepairResult, StringComparison.Ordinal);
        Assert.True(File.Exists(runtime.Layout.Resolve(row.Record.Folder, victim.EmlPath))); // the sync put it back
        await VerifyAsync(kit, main, row);
        Assert.StartsWith("Verified", details.VerifyHeadline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Messages_that_gmail_no_longer_has_cannot_be_repaired_and_the_overview_says_so()
    {
        var (kit, main, row) = await SyncedAsync(30);
        using var _ = kit;
        var runtime = kit.Archive.Runtime;
        var victim = runtime.Messages.EnumerateFiles(row.Id).First()[0];
        runtime.Messages.MarkRemoteDeleted(row.Id, [victim.GmailId]); // Gmail emptied it
        File.WriteAllBytes(runtime.Layout.Resolve(row.Record.Folder, victim.EmlPath), [1, 2, 3]);
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        await VerifyAsync(kit, main, row);
        var details = main.Details!;
        Assert.True(details.CanRepair);

        details.RepairCommand.Execute(null);

        Assert.StartsWith("None of the damaged messages can be downloaded again", details.RepairResult, StringComparison.Ordinal);
        Assert.Contains("Gmail no longer has them", details.RepairResult, StringComparison.Ordinal);
        Assert.Contains(victim.GmailId, runtime.Messages.GetIds(row.Id)); // the row is kept: it is all that is left
    }

    [Fact]
    public async Task Mail_that_arrived_after_the_sync_is_described_as_not_downloaded_yet()
    {
        var (kit, main, row) = await SyncedAsync(40);
        using var _ = kit;
        var mailbox = (GmailWatcher.Archive.Demo.InMemoryMailbox)await kit.Mailboxes.OpenAsync(row.Email, CancellationToken.None);
        mailbox.Add(new GmailWatcher.Archive.Demo.MessageSpec { Subject = "arrived late" });
        main.Accounts[0].ShowDetailsCommand.Execute(null);

        await VerifyAsync(kit, main, row);
        var details = main.Details!;

        Assert.Equal("Problems found", details.VerifyHeadline);
        Assert.Contains("1 message in Gmail isn't downloaded yet (sync, then verify again)", details.VerifyDetail, StringComparison.Ordinal);
        Assert.False(details.CanRepair); // nothing is damaged: a sync is what's needed
        Assert.Equal("Not downloaded yet", Assert.Single(details.Issues).Title);
    }

    // --- Browse --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Browsing_lists_the_newest_messages_pages_and_shows_a_plain_text_preview()
    {
        var (kit, main, row) = await SyncedAsync(130);
        using var _ = kit;
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        var browse = main.Details!.Browse;

        browse.EnsureLoaded();
        await kit.UntilAsync(() => browse.Results.Count == 50 && !browse.IsSearching, what: "the first page");

        Assert.True(browse.HasMore);
        Assert.Contains("130 messages", browse.ResultText, StringComparison.Ordinal);
        Assert.Contains(browse.Labels, l => l.Id == "INBOX");
        Assert.DoesNotContain(browse.Labels, l => l.Id == "UNREAD");

        browse.LoadMoreCommand.Execute(null);
        await kit.UntilAsync(() => browse.Results.Count == 100 && !browse.IsSearching, what: "the second page");

        browse.Selected = browse.Results[0];
        await kit.UntilAsync(() => browse.Preview is not null, what: "the preview");
        var preview = browse.Preview!;
        Assert.Equal(browse.Results[0].Subject, preview.Subject);
        Assert.NotEmpty(preview.Body);
        Assert.DoesNotContain("<html", preview.Body, StringComparison.OrdinalIgnoreCase);
        Assert.False(preview.HasProblem);
    }

    [Fact]
    public async Task The_open_message_can_be_copied_to_the_clipboard_as_text_or_as_a_whole()
    {
        var (kit, main, row) = await SyncedAsync(30);
        using var _ = kit;
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        var browse = main.Details!.Browse;
        browse.EnsureLoaded();
        await kit.UntilAsync(() => browse.Results.Count > 0 && !browse.IsSearching, what: "results");
        browse.Selected = browse.Results[0];
        await kit.UntilAsync(() => browse.Preview is not null, what: "the preview");
        var preview = browse.Preview!;
        Assert.Equal("", preview.CopyStatus);

        await preview.CopyBodyCommand.ExecuteAsync(null);

        Assert.Equal(preview.Body, Assert.Single(kit.Shell.Copied));
        Assert.Equal("Message text copied.", preview.CopyStatus);

        await preview.CopyAllCommand.ExecuteAsync(null);

        var all = kit.Shell.Copied[^1];
        Assert.StartsWith(preview.Subject, all, StringComparison.Ordinal);
        Assert.Contains($"From: {preview.From}", all, StringComparison.Ordinal);
        Assert.Contains($"To: {preview.To}", all, StringComparison.Ordinal);
        Assert.Contains($"Date: {preview.Date}", all, StringComparison.Ordinal);
        Assert.EndsWith(preview.Body, all, StringComparison.Ordinal);
        Assert.Equal("Whole message copied.", preview.CopyStatus);
    }

    [Fact]
    public async Task Searching_narrows_the_list_and_an_empty_result_says_so()
    {
        var (kit, main, row) = await SyncedAsync(100);
        using var _ = kit;
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        var browse = main.Details!.Browse;
        browse.EnsureLoaded();
        await kit.UntilAsync(() => browse.Results.Count > 0 && !browse.IsSearching, what: "results");
        var total = browse.Results.Count;
        // A sender's name is a deterministic query: from: searches the sender column, so every hit is from that sender.
        var sender = browse.Results.Select(r => r.Summary.FromAddress).First(a => a is { Length: > 0 } && !a.StartsWith("me@", StringComparison.Ordinal))!;
        var name = sender.Split('@')[0];

        browse.SearchText = "from:" + name;
        await kit.UntilAsync(() => !browse.IsSearching && browse.ResultText.Contains("match", StringComparison.Ordinal), what: "search results");

        Assert.NotEmpty(browse.Results);
        Assert.All(browse.Results, r => Assert.Contains(name, r.Summary.FromAddress, StringComparison.OrdinalIgnoreCase));

        browse.SearchText = "zzzzqqqqnotaword";
        await kit.UntilAsync(() => !browse.IsSearching && browse.ResultText == "No messages match.", what: "an empty result");
        Assert.Empty(browse.Results);
        Assert.True(browse.ShowNothingFound);

        browse.ClearCommand.Execute(null);
        await kit.UntilAsync(() => !browse.IsSearching && browse.Results.Count == total, what: "the full list again");
    }

    [Fact]
    public async Task A_missing_attachment_copy_is_a_note_and_the_archive_still_counts_as_verified()
    {
        var (kit, main, row) = await SyncedAsync(80);
        using var _ = kit;
        var runtime = kit.Archive.Runtime;
        var account = kit.Accounts.List()[0];
        var files = runtime.Messages.EnumerateFiles(account.Id).SelectMany(p => p).ToList();
        File.Delete(runtime.Layout.Resolve(account.Folder, runtime.Messages.GetAttachmentFiles(files).First().Path));
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        var details = main.Details!;

        await VerifyAsync(kit, main, row);

        Assert.Equal(VerifyState.Verified, row.Display.Verify);
        Assert.StartsWith("Verified", details.VerifyHeadline, StringComparison.Ordinal);
        Assert.Contains("still inside its saved message", details.VerifyDetail, StringComparison.Ordinal);
        Assert.Empty(details.Issues);
        Assert.False(details.CanRepair);
        Assert.True(details.HasNotices);
        Assert.Equal("Attachment copy missing", Assert.Single(details.Notices).Title);
        Assert.Contains("does not stop this archive from counting as verified", details.NoticeIntro, StringComparison.Ordinal);
        Assert.DoesNotContain("antivirus blocked", details.NoticeIntro, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_about_a_blocked_attachment_explains_the_antivirus_and_where_to_look()
    {
        var (kit, main, row) = await SyncedAsync(40);
        using var _ = kit;
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        await VerifyAsync(kit, main, row);
        var accounts = kit.Archive.Runtime.Accounts;
        var report = accounts.LoadVerification(row.Id)!;
        accounts.SaveVerification(row.Id, report with
        {
            Notices = [new VerificationIssue(VerificationIssueKind.AttachmentBlocked, "m1", "Windows or your antivirus blocked access to the attachment antivirus-test-file.txt: it reports that the file contains a virus or potentially unwanted software.")],
        });
        main.Details!.Refresh();

        var details = main.Details;

        Assert.Equal("Attachment copy blocked by Windows or your antivirus", Assert.Single(details.Notices).Title);
        Assert.Contains("If you don't recognise the file, leave it blocked", details.NoticeIntro, StringComparison.Ordinal);
        Assert.Contains("Protection history", details.NoticeIntro, StringComparison.Ordinal);
        Assert.Contains("everything Gmail listed", row.Display.VerifyLine, StringComparison.Ordinal);
        Assert.Contains("every saved message is intact", row.Display.VerifyLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_that_lost_the_race_cannot_leave_the_spinner_on()
    {
        var (kit, main, row) = await SyncedAsync(60);
        using var _ = kit;
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        var browse = main.Details!.Browse;
        browse.EnsureLoaded();
        await kit.UntilAsync(() => browse.Results.Count > 0 && !browse.IsSearching, what: "results");
        var shown = browse.Results.Select(r => r.Summary.Pk).ToList();

        // A newer search always wins. The loser can still reach the screen late (on a busy machine it only gets a thread after
        // its successor has finished), either cancelled or simply outdated. Version 0 is older than any real search.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        foreach (var token in new[] { cancelled.Token, CancellationToken.None })
        {
            try
            {
                await browse.RunAsync(append: false, version: 0, token);
            }
            catch (OperationCanceledException)
            {
                // fine: a cancelled search just stops
            }

            kit.Pump.Run();
            Assert.False(browse.IsSearching, $"the spinner was left on by a late search (cancelled: {token.IsCancellationRequested})");
            Assert.Equal(shown, browse.Results.Select(r => r.Summary.Pk).ToList());
        }
    }

    [Fact]
    public async Task The_attachment_filter_and_the_label_filter_apply()
    {
        var (kit, main, row) = await SyncedAsync(150);
        using var _ = kit;
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        var browse = main.Details!.Browse;
        browse.EnsureLoaded();
        await kit.UntilAsync(() => browse.Results.Count > 0 && !browse.IsSearching, what: "results");

        browse.OnlyWithAttachments = true;
        await kit.UntilAsync(() => !browse.IsSearching && browse.Results.Count > 0 && browse.Results.All(r => r.HasAttachments), what: "attachment results");
        browse.OnlyWithAttachments = false;

        browse.SelectedLabel = browse.Labels.First(l => l.Id == "SENT");
        await kit.UntilAsync(() => !browse.IsSearching && browse.Results.Count > 0 && browse.Results.All(r => r.Summary.Labels.Contains("SENT")), what: "SENT results");
    }

    [Fact]
    public async Task Attachments_open_only_when_safe_and_can_always_be_shown_in_the_folder()
    {
        var (kit, main, row) = await SyncedAsync(150);
        using var _ = kit;
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        var browse = main.Details!.Browse;
        browse.EnsureLoaded();
        browse.OnlyWithAttachments = true;
        await kit.UntilAsync(() => browse.Results.Count > 0 && !browse.IsSearching, what: "messages with attachments");
        browse.Selected = browse.Results[0];
        await kit.UntilAsync(() => browse.Preview is { HasAttachments: true }, what: "a preview with attachments");

        var attachment = browse.Preview!.Attachments.First();
        attachment.RevealCommand.Execute(null);
        Assert.Single(kit.Files.Revealed);
        Assert.True(File.Exists(kit.Files.Revealed[0]));

        if (attachment.CanOpen)
        {
            attachment.OpenCommand.Execute(null);
            Assert.Single(kit.Files.Opened);
        }

        browse.Preview.ShowFileCommand.Execute(null);
        Assert.EndsWith(".eml", kit.Files.Revealed[^1], StringComparison.Ordinal);
    }

    // --- Insights ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Insights_load_in_the_background_and_fill_the_lists()
    {
        var (kit, main, row) = await SyncedAsync(200);
        using var _ = kit;
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        var insights = main.Details!.Insights;

        insights.EnsureLoaded();
        await kit.UntilAsync(() => insights.ShowContent, what: "the insights");

        Assert.True(insights.ShowContent);
        Assert.Contains("200 messages", insights.Headline, StringComparison.Ordinal);
        Assert.NotEmpty(insights.MonthValues);
        Assert.Contains(insights.Lists, l => l.Title == "Who writes to you most" && l.HasRows);
        Assert.Contains(insights.Lists, l => l.Title == "Largest attachments" && l.HasRows);
        Assert.All(insights.Lists.Where(l => l.HasRows), l => Assert.Equal(100, l.Rows.Max(r => r.Percent)));
        Assert.EndsWith("analytics.duckdb", insights.DatabasePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Insights_of_an_empty_account_say_there_is_nothing_to_analyze()
    {
        using var kit = new TestKit(accounts: 1);
        var main = kit.CreateMain();
        main.Accounts[0].ShowDetailsCommand.Execute(null);
        var insights = main.Details!.Insights;

        insights.EnsureLoaded();
        await kit.UntilAsync(() => !insights.IsLoading && insights.ShowEmpty, what: "the empty state");

        Assert.False(insights.HasData);
    }
}
