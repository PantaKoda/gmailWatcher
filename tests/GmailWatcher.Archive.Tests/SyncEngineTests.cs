using GmailWatcher.Archive.Demo;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Sync;

namespace GmailWatcher.Archive.Tests;

public sealed class SyncEngineTests : IDisposable
{
    private const string Email = "me@example.com";
    private readonly ArchiveFixture _fixture = new();
    private readonly InMemoryMailbox _mailbox = new(Email);
    private int _counter;

    public SyncEngineTests() => _fixture.AddAccount(Email);

    public void Dispose() => _fixture.Dispose();

    private AccountRecord Account => _fixture.Accounts.Find(Email)!;

    private string Deliver(string subject = "", string[]? labels = null, IReadOnlyList<SyntheticAttachment>? attachments = null) =>
        _mailbox.Add(new MessageSpec
        {
            Subject = string.IsNullOrEmpty(subject) ? $"Message {++_counter}" : subject,
            Body = $"Body of message {_counter}",
            Date = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(_counter),
            Labels = labels ?? ["INBOX"],
            Attachments = attachments ?? [],
        });

    private Task<SyncResult> Sync() => _fixture.SyncAsync(Email, _mailbox, null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_first_sync_archives_everything_gmail_has_including_spam_and_trash()
    {
        Deliver(labels: ["INBOX", "UNREAD"]);
        Deliver(labels: ["SENT"]);
        Deliver(labels: ["SPAM"]);
        Deliver(labels: ["TRASH"]);
        Deliver(attachments: [new("a.pdf", "application/pdf", [1, 2, 3])]);

        var result = await Sync();

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.True(result.WasInitial);
        Assert.False(result.UsedHistory);
        Assert.Equal(5, result.MessagesAdded);
        Assert.Equal(_mailbox.Ids.ToHashSet(), _fixture.Messages.GetIds(Account.Id));
        Assert.Equal(5, _fixture.Messages.GetStats(Account.Id).Messages);
        Assert.Equal(1, _fixture.Messages.GetStats(Account.Id).Attachments);
        Assert.NotNull(Account.InitialSyncCompletedAt);
        Assert.Equal("Completed", Account.LastSyncOutcome);
        Assert.Equal(_mailbox.CurrentHistoryId.ToString(), _fixture.Accounts.GetHistoryId(Account.Id));
    }

    [Fact]
    public async Task Every_archived_message_has_an_intact_file_that_matches_its_checksum()
    {
        for (var i = 0; i < 12; i++)
        {
            Deliver();
        }

        await Sync();

        foreach (var page in _fixture.Messages.EnumerateFiles(Account.Id))
        {
            foreach (var file in page)
            {
                var path = _fixture.Layout.Resolve(Account.Folder, file.EmlPath);
                Assert.Equal(file.SizeBytes, new FileInfo(path).Length);
                Assert.Equal(file.Sha256, FileStore.Sha256Hex(path, TestContext.Current.CancellationToken));
            }
        }
    }

    [Fact]
    public async Task Labels_and_mailbox_settings_are_saved()
    {
        _mailbox.AddLabel("Label_1", "Receipts");
        Deliver(labels: ["INBOX", "Label_1"]);

        await Sync();

        Assert.Contains(_fixture.Accounts.GetLabels(Account.Id), l => l is { Id: "Label_1", Name: "Receipts", Kind: LabelKind.User });
        Assert.Contains(_fixture.Accounts.GetLabels(Account.Id), l => l is { Id: "INBOX", Kind: LabelKind.System });
        Assert.Equal(["filters", "sendAs"], _fixture.Accounts.LoadSettings(Account.Id).Select(d => d.Name));
        Assert.Single(_fixture.Messages.GetIdsWithLabel(Account.Id, "Label_1"));
    }

    [Fact]
    public async Task Later_syncs_download_only_what_is_new_using_history()
    {
        for (var i = 0; i < 6; i++)
        {
            Deliver();
        }

        await Sync();
        var callsAfterFirst = _mailbox.GetCalls;
        var listsAfterFirst = _mailbox.ListCalls;
        Deliver();
        Deliver();

        var result = await Sync();

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.True(result.UsedHistory);
        Assert.False(result.WasInitial);
        Assert.Equal(2, result.MessagesAdded);
        Assert.Equal(callsAfterFirst + 2, _mailbox.GetCalls);
        Assert.Equal(listsAfterFirst, _mailbox.ListCalls); // no listing of the whole mailbox
        Assert.Equal(8, _fixture.Messages.Count(Account.Id));
    }

    [Fact]
    public async Task A_sync_with_nothing_new_changes_nothing()
    {
        Deliver();
        await Sync();

        var result = await Sync();

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.MessagesAdded);
        Assert.Equal(1, _fixture.Messages.Count(Account.Id));
    }

    [Fact]
    public async Task When_gmail_forgot_the_history_a_full_pass_finds_new_mail_and_loses_nothing()
    {
        Deliver();
        Deliver();
        await Sync();
        Deliver();
        _mailbox.ExpireHistory();

        var result = await Sync();

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.False(result.UsedHistory);
        Assert.Equal(1, result.MessagesAdded);
        Assert.Equal(3, _fixture.Messages.Count(Account.Id));
        Assert.Equal(_mailbox.CurrentHistoryId.ToString(), _fixture.Accounts.GetHistoryId(Account.Id));
    }

    [Fact]
    public async Task Mail_deleted_in_gmail_stays_in_the_archive_and_is_only_marked()
    {
        var keep = Deliver();
        var gone = Deliver();
        await Sync();

        _mailbox.Delete(gone);
        var result = await Sync();

        Assert.Equal(1, result.MarkedRemoteDeleted);
        Assert.Equal(2, _fixture.Messages.Count(Account.Id));
        Assert.Equal(1, _fixture.Messages.GetStats(Account.Id).RemoteDeleted);
        Assert.True(File.Exists(_fixture.EmlPath(Account, gone, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(2).ToUnixTimeMilliseconds())));
        Assert.Contains(keep, _fixture.Messages.GetIds(Account.Id));
    }

    [Fact]
    public async Task Emptying_the_whole_mailbox_cannot_empty_the_archive()
    {
        for (var i = 0; i < 8; i++)
        {
            Deliver();
        }

        await Sync();
        _mailbox.DeleteAll();

        var incremental = await Sync();
        _mailbox.ExpireHistory();
        var full = await Sync();

        Assert.Equal(SyncOutcome.Completed, incremental.Outcome);
        Assert.Equal(SyncOutcome.Completed, full.Outcome);
        var stats = _fixture.Messages.GetStats(Account.Id);
        Assert.Equal(8, stats.Messages);
        Assert.Equal(8, stats.RemoteDeleted);
    }

    [Fact]
    public async Task A_full_pass_marks_what_gmail_no_longer_lists()
    {
        var kept = Deliver();
        var removed = Deliver();
        await Sync();
        _mailbox.Delete(removed);
        _mailbox.ExpireHistory();

        var result = await Sync();

        Assert.False(result.UsedHistory);
        Assert.Equal(1, result.MarkedRemoteDeleted);
        Assert.Equal(1, _fixture.Messages.GetStats(Account.Id).RemoteDeleted);
        Assert.Contains(kept, _fixture.Messages.GetIds(Account.Id));
        Assert.Contains(removed, _fixture.Messages.GetIds(Account.Id));
    }

    [Fact]
    public async Task A_mark_is_cleared_when_a_listing_shows_the_message_is_in_gmail_after_all()
    {
        var id = Deliver();
        await Sync();
        _fixture.Messages.MarkRemoteDeleted(Account.Id, [id]);
        Assert.Equal(1, _fixture.Messages.GetStats(Account.Id).RemoteDeleted);

        var cleared = _fixture.Messages.UnmarkRemoteDeleted(Account.Id, new HashSet<string> { id });

        Assert.Equal(1, cleared);
        Assert.Equal(0, _fixture.Messages.GetStats(Account.Id).RemoteDeleted);
    }

    [Fact]
    public async Task Label_changes_in_gmail_reach_the_archive_through_history()
    {
        var id = Deliver(labels: ["INBOX", "UNREAD"]);
        await Sync();

        _mailbox.ChangeLabels(id, add: ["STARRED"], remove: ["UNREAD"]);
        var result = await Sync();

        Assert.Equal(1, result.LabelChanges);
        Assert.Contains(id, _fixture.Messages.GetIdsWithLabel(Account.Id, "STARRED"));
        Assert.DoesNotContain(id, _fixture.Messages.GetIdsWithLabel(Account.Id, "UNREAD"));
        Assert.Contains(id, _fixture.Messages.GetIdsWithLabel(Account.Id, "INBOX"));
    }

    [Fact]
    public async Task A_full_pass_reconciles_labels_changed_while_history_was_unavailable()
    {
        var id = Deliver(labels: ["INBOX", "UNREAD"]);
        var other = Deliver(labels: ["INBOX"]);
        await Sync();

        _mailbox.ChangeLabels(id, add: ["IMPORTANT"], remove: ["UNREAD"]);
        _mailbox.ChangeLabels(other, add: ["STARRED"]);
        _mailbox.ExpireHistory();
        var result = await Sync();

        Assert.False(result.UsedHistory);
        Assert.Equal(3, result.LabelChanges);
        Assert.Contains(id, _fixture.Messages.GetIdsWithLabel(Account.Id, "IMPORTANT"));
        Assert.DoesNotContain(id, _fixture.Messages.GetIdsWithLabel(Account.Id, "UNREAD"));
        Assert.Contains(other, _fixture.Messages.GetIdsWithLabel(Account.Id, "STARRED"));
    }

    [Fact]
    public async Task Stopping_keeps_what_was_downloaded_and_the_next_sync_continues_without_duplicates()
    {
        for (var i = 0; i < 40; i++)
        {
            Deliver();
        }

        _mailbox.Latency = TimeSpan.FromMilliseconds(15);
        using var cts = new CancellationTokenSource();
        var downloads = 0;
        _mailbox.FailGet = _ =>
        {
            if (Interlocked.Increment(ref downloads) == 10)
            {
                cts.Cancel(); // "the user pressed Stop" after ten downloads
            }

            return null;
        };

        var stopped = await _fixture.SyncAsync(Email, _mailbox, null, cts.Token);

        Assert.Equal(SyncOutcome.Cancelled, stopped.Outcome);
        var kept = _fixture.Messages.Count(Account.Id);
        Assert.InRange(kept, 1, 39);
        Assert.Null(Account.InitialSyncCompletedAt);
        Assert.Null(_fixture.Accounts.GetHistoryId(Account.Id));

        _mailbox.Latency = TimeSpan.Zero;
        _mailbox.FailGet = null;
        var resumed = await Sync();

        Assert.Equal(SyncOutcome.Completed, resumed.Outcome);
        Assert.Equal(40 - kept, resumed.MessagesAdded);
        Assert.Equal(40, _fixture.Messages.Count(Account.Id));
        Assert.NotNull(Account.InitialSyncCompletedAt);
    }

    [Fact]
    public async Task A_network_failure_stops_the_sync_but_keeps_everything_committed()
    {
        var ids = Enumerable.Range(0, 20).Select(_ => Deliver()).ToList();
        var failing = ids[0]; // the oldest: Gmail lists newest first, so this one is asked for last
        _mailbox.FailGet = id => id == failing ? new MailboxUnavailableException("The network went away.") : null;

        var result = await Sync();

        Assert.Equal(SyncOutcome.Failed, result.Outcome);
        Assert.Equal("The network went away.", result.Error);
        var kept = _fixture.Messages.Count(Account.Id);
        Assert.InRange(kept, 1, 19);
        Assert.Null(Account.InitialSyncCompletedAt);
        Assert.Equal("The network went away.", Account.LastError);

        _mailbox.FailGet = null;
        var again = await Sync();
        Assert.Equal(SyncOutcome.Completed, again.Outcome);
        Assert.Equal(20 - kept, again.MessagesAdded);
        Assert.Equal(20, _fixture.Messages.Count(Account.Id));
    }

    [Fact]
    public async Task A_revoked_sign_in_marks_the_account_for_reconnecting_and_keeps_the_archive()
    {
        Deliver();
        await Sync();
        _mailbox.FailProfile = new ReconnectRequiredException("Google no longer accepts the saved sign-in.");

        var result = await Sync();

        Assert.Equal(SyncOutcome.ReconnectRequired, result.Outcome);
        Assert.Equal(AccountConnection.ReconnectRequired, Account.Connection);
        Assert.Equal(1, _fixture.Messages.Count(Account.Id));
    }

    [Fact]
    public async Task Gmails_usage_limit_is_reported_as_try_again_later()
    {
        Deliver();
        _mailbox.FailGet = _ => new QuotaExhaustedException("Daily limit reached.");

        var result = await Sync();

        Assert.Equal(SyncOutcome.QuotaExhausted, result.Outcome);
        Assert.Equal("Daily limit reached.", result.Error);
        Assert.Equal(0, _fixture.Messages.Count(Account.Id));
    }

    [Fact]
    public async Task A_sign_in_for_a_different_google_account_is_refused_before_anything_is_written()
    {
        Deliver();
        _mailbox.Email = "someone.else@example.com";

        var result = await Sync();

        Assert.Equal(SyncOutcome.Failed, result.Outcome);
        Assert.Contains("someone.else@example.com", result.Error);
        Assert.Equal(0, _fixture.Messages.Count(Account.Id));
    }

    [Fact]
    public async Task A_message_deleted_between_listing_and_download_is_skipped_not_fatal()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Deliver()).ToList();
        _mailbox.FailGet = id =>
        {
            if (id == ids[2])
            {
                _mailbox.Delete(id); // gone by the time it is asked for
            }

            return null;
        };

        var result = await Sync();

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.VanishedBeforeDownload);
        Assert.Equal(4, _fixture.Messages.Count(Account.Id));
    }

    [Fact]
    public async Task Malformed_mail_is_archived_anyway()
    {
        var garbage = new byte[1500];
        new Random(7).NextBytes(garbage);
        var id = _mailbox.AddRaw(garbage, new DateTimeOffset(2022, 2, 2, 2, 2, 2, TimeSpan.Zero), "INBOX");

        var result = await Sync();

        Assert.Equal(SyncOutcome.Completed, result.Outcome);
        Assert.Contains(id, _fixture.Messages.GetIds(Account.Id));
        var file = Assert.Single(Assert.Single(_fixture.Messages.EnumerateFiles(Account.Id)));
        Assert.Equal(garbage, File.ReadAllBytes(_fixture.Layout.Resolve(Account.Folder, file.EmlPath)));
    }

    [Fact]
    public async Task A_second_sync_of_the_same_account_at_the_same_time_is_refused()
    {
        for (var i = 0; i < 20; i++)
        {
            Deliver();
        }

        _mailbox.Latency = TimeSpan.FromMilliseconds(20);
        var first = Sync();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Sync());
        Assert.Equal(SyncOutcome.Completed, (await first).Outcome);
        Assert.False(_fixture.Engine.IsRunning(Account.Id));
    }

    [Fact]
    public async Task Many_messages_downloaded_in_parallel_arrive_exactly_once()
    {
        using var fixture = new ArchiveFixture(new GmailWatcher.Core.Configuration.SyncOptions { Workers = 8 });
        var account = fixture.AddAccount("big@example.com");
        var mailbox = new InMemoryMailbox("big@example.com");
        for (var i = 0; i < 300; i++)
        {
            mailbox.Add(new MessageSpec { Subject = $"Bulk {i}", Date = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i), Labels = [i % 3 == 0 ? "SENT" : "INBOX"] });
        }

        var result = await fixture.SyncAsync("big@example.com", mailbox, null, TestContext.Current.CancellationToken);

        Assert.Equal(300, result.MessagesAdded);
        Assert.Equal(300, fixture.Messages.Count(account.Id));
        Assert.Equal(mailbox.Ids.ToHashSet(), fixture.Messages.GetIds(account.Id));
        Assert.Equal(300, mailbox.GetCalls);
    }

    [Fact]
    public async Task Progress_reports_listing_then_downloading_with_a_total()
    {
        for (var i = 0; i < 30; i++)
        {
            Deliver();
        }

        var seen = new List<SyncProgress>();
        await _fixture.SyncAsync(Email, _mailbox, new RecordingProgress(seen.Add), TestContext.Current.CancellationToken);

        Assert.Contains(seen, p => p.Phase == SyncPhase.Labels);
        Assert.Contains(seen, p => p.Phase == SyncPhase.Listing);
        var last = seen.Last(p => p.Phase == SyncPhase.Downloading);
        Assert.Equal(30, last.Total);
        Assert.Equal(30, last.Done);
        Assert.Equal(1.0, last.Fraction);
        Assert.True(last.BytesDownloaded > 0);
    }

    /// <summary>Synchronous progress: records every report so the test can look at the sequence.</summary>
    private sealed class RecordingProgress(Action<SyncProgress> record) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value) => record(value);
    }
}
