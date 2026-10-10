using GmailWatcher.Archive.Demo;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Archive.Verification;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Sync;
using GmailWatcher.Core.Verification;
using Microsoft.Extensions.Logging.Abstractions;

namespace GmailWatcher.Archive.Tests;

public sealed class VerifierTests : IDisposable
{
    private const string Email = "me@example.com";
    private readonly ArchiveFixture _fixture = new();
    private readonly InMemoryMailbox _mailbox = new(Email);
    private readonly Verifier _verifier;
    private readonly ArchiveMaintenance _maintenance;
    private int _counter;

    public VerifierTests()
    {
        _fixture.AddAccount(Email);
        _verifier = new Verifier(_fixture.Accounts, _fixture.Messages, _fixture.Layout, TimeProvider.System, NullLogger<Verifier>.Instance);
        _maintenance = new ArchiveMaintenance(_fixture.Accounts, _fixture.Messages, _fixture.Layout, NullLogger<ArchiveMaintenance>.Instance);
    }

    public void Dispose() => _fixture.Dispose();

    private AccountRecord Account => _fixture.Accounts.Find(Email)!;

    private string Deliver(params SyntheticAttachment[] attachments) =>
        _mailbox.Add(new MessageSpec
        {
            Subject = $"Message {++_counter}",
            Date = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero).AddHours(_counter),
            Attachments = attachments,
        });

    private async Task SyncAsync(InMemoryMailbox? mailbox)
    {
        var result = await _fixture.SyncAsync(Email, mailbox ?? _mailbox, null, TestContext.Current.CancellationToken);
        Assert.Equal(SyncOutcome.Completed, result.Outcome);
    }

    private Task<VerificationReport> VerifyAsync(IMailbox? mailbox) =>
        _verifier.VerifyAsync(Account, mailbox, null, TestContext.Current.CancellationToken);

    private string EmlOf(string messageId) =>
        _fixture.Layout.Resolve(Account.Folder, _fixture.Messages.EnumerateFiles(Account.Id).SelectMany(p => p).Single(f => f.GmailId == messageId).EmlPath);

    [Fact]
    public async Task A_complete_and_intact_archive_is_verified()
    {
        Deliver();
        Deliver(new SyntheticAttachment("a.pdf", "application/pdf", [1, 2, 3, 4]));
        Deliver();
        await SyncAsync(_mailbox);

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.Verified, report.Verdict);
        Assert.True(report.IsClean);
        Assert.True(report.GmailChecked);
        Assert.Equal(3, report.GmailMessages);
        Assert.Equal(3, report.ArchivedMessages);
        Assert.Equal(0, report.MissingFromArchive);
        Assert.Equal(4, report.FilesChecked); // three messages and one attachment
        Assert.True(report.BytesChecked > 0);
        Assert.True(report.DatabaseIntact);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task Mail_that_arrived_after_the_sync_is_reported_as_not_downloaded_yet()
    {
        Deliver();
        await SyncAsync(_mailbox);
        var late = Deliver();

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.Problems, report.Verdict);
        Assert.Equal(1, report.MissingFromArchive);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(VerificationIssueKind.MissingFromArchive, issue.Kind);
        Assert.Equal(late, issue.MessageId);
    }

    [Fact]
    public async Task Verification_still_passes_after_gmail_was_emptied_because_the_archive_is_complete()
    {
        Deliver();
        Deliver();
        await SyncAsync(_mailbox);
        _mailbox.DeleteAll();
        await SyncAsync(_mailbox);

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.Verified, report.Verdict);
        Assert.Equal(0, report.GmailMessages);
        Assert.Equal(2, report.ArchivedMessages);
    }

    [Fact]
    public async Task A_deleted_message_file_is_detected()
    {
        var id = Deliver();
        Deliver();
        await SyncAsync(_mailbox);
        File.Delete(EmlOf(id));

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.Problems, report.Verdict);
        Assert.Equal(1, report.FilesMissing);
        Assert.Equal(VerificationIssueKind.FileMissing, Assert.Single(report.Issues).Kind);
    }

    [Fact]
    public async Task A_truncated_file_is_detected_by_its_size()
    {
        var id = Deliver();
        await SyncAsync(_mailbox);
        var path = EmlOf(id);
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..20]);

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(1, report.FilesCorrupt);
        Assert.Equal(VerificationIssueKind.FileCorrupt, Assert.Single(report.Issues).Kind);
    }

    [Fact]
    public async Task A_flipped_byte_with_the_same_size_is_detected_by_the_checksum()
    {
        var id = Deliver();
        await SyncAsync(_mailbox);
        var path = EmlOf(id);
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.Problems, report.Verdict);
        Assert.Contains("checksum", Assert.Single(report.Issues).Detail);
    }

    private string AttachmentCopyOf(string messageId)
    {
        var files = _fixture.Messages.EnumerateFiles(Account.Id).SelectMany(p => p).ToList();
        return _fixture.Layout.Resolve(Account.Folder, _fixture.Messages.GetAttachmentFiles(files).Single(a => a.GmailId == messageId).Path);
    }

    [Fact]
    public async Task A_changed_attachment_copy_is_only_a_note_while_the_message_file_holding_it_is_intact()
    {
        var id = Deliver(new SyntheticAttachment("contract.pdf", "application/pdf", [10, 20, 30, 40, 50]));
        await SyncAsync(_mailbox);
        File.WriteAllBytes(AttachmentCopyOf(id), [10, 20, 30, 40, 51]);

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.Verified, report.Verdict);
        Assert.Empty(report.Issues);
        Assert.Equal(0, report.FilesCorrupt);
        var note = Assert.Single(report.NoticeList);
        Assert.Equal(VerificationIssueKind.AttachmentCorrupt, note.Kind);
        Assert.Equal(id, note.MessageId);
        Assert.Contains("contract.pdf", note.Detail, StringComparison.Ordinal);
        Assert.Equal(1, report.NoticeCount);
    }

    [Fact]
    public async Task A_missing_attachment_copy_is_only_a_note_too()
    {
        var id = Deliver(new SyntheticAttachment("photo.jpg", "image/jpeg", [1, 2, 3]));
        await SyncAsync(_mailbox);
        File.Delete(AttachmentCopyOf(id));

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.Verified, report.Verdict);
        Assert.Equal(0, report.FilesMissing);
        Assert.Equal(VerificationIssueKind.AttachmentMissing, Assert.Single(report.NoticeList).Kind);
    }

    [Fact]
    public async Task An_attachment_copy_that_cannot_be_read_is_a_note_not_a_failure()
    {
        var id = Deliver(new SyntheticAttachment("locked.bin", "application/octet-stream", [5, 6, 7]));
        await SyncAsync(_mailbox);
        using var hold = new FileStream(AttachmentCopyOf(id), FileMode.Open, FileAccess.Read, FileShare.None);

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.Verified, report.Verdict);
        var note = Assert.Single(report.NoticeList);
        Assert.Equal(VerificationIssueKind.AttachmentCorrupt, note.Kind);
        Assert.Contains("could not be read", note.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_message_file_is_damaged_too_its_attachment_copy_is_a_real_problem()
    {
        var id = Deliver(new SyntheticAttachment("contract.pdf", "application/pdf", [10, 20, 30]));
        await SyncAsync(_mailbox);
        File.WriteAllBytes(EmlOf(id), [0]);
        File.Delete(AttachmentCopyOf(id));

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.Problems, report.Verdict);
        Assert.Empty(report.NoticeList);
        Assert.Equal([VerificationIssueKind.FileCorrupt, VerificationIssueKind.AttachmentMissing], report.Issues.Select(i => i.Kind).Order());
    }

    [Fact]
    public async Task A_damaged_message_file_is_still_a_problem_even_when_its_attachment_copy_is_fine()
    {
        var id = Deliver(new SyntheticAttachment("contract.pdf", "application/pdf", [10, 20, 30]));
        await SyncAsync(_mailbox);
        File.WriteAllBytes(EmlOf(id), [0]);

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.Problems, report.Verdict);
        Assert.Equal(VerificationIssueKind.FileCorrupt, Assert.Single(report.Issues).Kind);
        Assert.Empty(report.NoticeList);
    }

    [Theory]
    [InlineData(unchecked((int)0x800700E1), true)]  // ERROR_VIRUS_INFECTED: "the file contains a virus or potentially unwanted software"
    [InlineData(unchecked((int)0x800700E2), true)]  // ERROR_VIRUS_DELETED
    [InlineData(unchecked((int)0x80070020), false)] // sharing violation
    [InlineData(unchecked((int)0x80070005), false)] // access denied
    [InlineData(unchecked((int)0x80131620), false)] // plain IOException
    public void Only_windows_virus_errors_count_as_blocked_by_an_antivirus(int hresult, bool blocked)
    {
        Assert.Equal(blocked, Verifier.IsBlockedByAntivirus(new IOException("x", hresult)));
        Assert.False(Verifier.IsBlockedByAntivirus(new UnauthorizedAccessException("x")));
    }

    [Fact]
    public async Task Notes_survive_being_saved_and_an_older_report_without_notes_still_loads()
    {
        var id = Deliver(new SyntheticAttachment("a.txt", "text/plain", [1]));
        await SyncAsync(_mailbox);
        File.Delete(AttachmentCopyOf(id));
        await VerifyAsync(_mailbox);

        var loaded = _fixture.Accounts.LoadVerification(Account.Id)!;
        Assert.Equal(VerificationIssueKind.AttachmentMissing, Assert.Single(loaded.NoticeList).Kind);

        var older = loaded with { Notices = null, NoticesNotListed = 0 };
        _fixture.Accounts.SaveVerification(Account.Id, older);
        Assert.Empty(_fixture.Accounts.LoadVerification(Account.Id)!.NoticeList);
    }

    [Fact]
    public async Task Without_gmail_only_the_files_are_checked_and_the_verdict_says_so()
    {
        Deliver();
        await SyncAsync(_mailbox);

        var report = await VerifyAsync(null);

        Assert.Equal(VerificationVerdict.FilesIntactGmailNotChecked, report.Verdict);
        Assert.False(report.IsClean);
        Assert.False(report.GmailChecked);
    }

    [Fact]
    public async Task When_gmail_cannot_be_reached_the_reason_is_kept_and_nothing_is_claimed()
    {
        Deliver();
        await SyncAsync(_mailbox);
        _mailbox.FailList = new MailboxUnavailableException("No internet connection.");

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.FilesIntactGmailNotChecked, report.Verdict);
        Assert.Equal("No internet connection.", report.GmailCheckError);
    }

    [Fact]
    public async Task The_report_is_saved_with_the_account_and_can_be_read_back()
    {
        Deliver();
        await SyncAsync(_mailbox);

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationVerdict.Verified.ToString(), Account.LastVerificationVerdict);
        Assert.NotNull(Account.LastVerifiedAt);
        var loaded = _fixture.Accounts.LoadVerification(Account.Id);
        Assert.NotNull(loaded);
        Assert.Equal(report.Verdict, loaded.Verdict);
        Assert.Equal(report.FilesChecked, loaded.FilesChecked);
        Assert.Equal(report.CompletedAt, loaded.CompletedAt);
    }

    [Fact]
    public async Task A_cancelled_verification_concludes_nothing_and_saves_nothing()
    {
        for (var i = 0; i < 5; i++)
        {
            Deliver();
        }

        await SyncAsync(_mailbox);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var report = await _verifier.VerifyAsync(Account, _mailbox, null, cts.Token);

        Assert.Equal(VerificationVerdict.Cancelled, report.Verdict);
        Assert.Null(Account.LastVerifiedAt);
    }

    [Fact]
    public async Task The_issue_list_is_capped_but_every_problem_is_counted()
    {
        for (var i = 0; i < VerificationReport.MaxListedIssues + 15; i++)
        {
            Deliver();
        }

        await SyncAsync(_mailbox);
        foreach (var page in _fixture.Messages.EnumerateFiles(Account.Id))
        {
            foreach (var file in page)
            {
                File.Delete(_fixture.Layout.Resolve(Account.Folder, file.EmlPath));
            }
        }

        var report = await VerifyAsync(_mailbox);

        Assert.Equal(VerificationReport.MaxListedIssues + 15, report.FilesMissing);
        Assert.Equal(VerificationReport.MaxListedIssues, report.Issues.Count);
        Assert.Equal(15, report.IssuesNotListed);
    }

    [Fact]
    public async Task Repair_makes_the_next_sync_download_damaged_messages_again()
    {
        var damaged = Deliver();
        var healthy = Deliver();
        await SyncAsync(_mailbox);
        File.Delete(EmlOf(damaged));
        var report = await VerifyAsync(_mailbox);

        var plan = _maintenance.Repair(Account, report);

        Assert.Equal(new RepairPlan(1, 0), plan);
        Assert.DoesNotContain(damaged, _fixture.Messages.GetIds(Account.Id));
        Assert.Contains(healthy, _fixture.Messages.GetIds(Account.Id));

        await SyncAsync(_mailbox);
        var after = await VerifyAsync(_mailbox);
        Assert.Equal(VerificationVerdict.Verified, after.Verdict);
        Assert.Contains(damaged, _fixture.Messages.GetIds(Account.Id));
    }

    [Fact]
    public async Task Repair_leaves_alone_a_damaged_message_gmail_no_longer_has()
    {
        var id = Deliver();
        await SyncAsync(_mailbox);
        _mailbox.Delete(id);
        await SyncAsync(_mailbox); // marks it as gone from Gmail
        var path = EmlOf(id);
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..10]);
        var report = await VerifyAsync(_mailbox);

        var plan = _maintenance.Repair(Account, report);

        Assert.Equal(new RepairPlan(0, 1), plan);
        Assert.Contains(id, _fixture.Messages.GetIds(Account.Id)); // the row stays: it is all that is left of the message
    }

    [Fact]
    public async Task Deleting_one_accounts_data_leaves_other_accounts_alone()
    {
        Deliver();
        await SyncAsync(_mailbox);
        var other = _fixture.AddAccount("other@example.com");
        var otherBox = new InMemoryMailbox("other@example.com");
        otherBox.Add(new MessageSpec { Subject = "Other" });
        var otherResult = await _fixture.SyncAsync("other@example.com", otherBox, null, TestContext.Current.CancellationToken);
        Assert.Equal(SyncOutcome.Completed, otherResult.Outcome);
        var mine = Account;

        _maintenance.DeleteAccountData(other);

        Assert.Null(_fixture.Accounts.Find("other@example.com"));
        Assert.False(Directory.Exists(_fixture.Layout.AccountDirectory(other.Folder)));
        Assert.Equal(1, _fixture.Messages.Count(mine.Id));
        Assert.True(Directory.Exists(_fixture.Layout.AccountDirectory(mine.Folder)));
    }

    [Fact]
    public void Deleting_refuses_a_folder_that_is_not_an_account_folder()
    {
        _fixture.AddAccount("x@example.com");
        var bogus = Account with { Folder = "" };

        Assert.Throws<InvalidOperationException>(() => _maintenance.DeleteAccountData(bogus));
        Assert.NotNull(_fixture.Accounts.Find(Email)); // nothing was touched
    }
}
