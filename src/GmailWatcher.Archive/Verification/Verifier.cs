using System.Collections.Concurrent;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Verification;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Archive.Verification;

/// <summary>
/// Answers "can this archive replace the Gmail mailbox?". Two independent checks:
/// <list type="number">
/// <item><b>Complete:</b> every message ID Gmail lists right now (Spam and Trash included) has an archived copy.</item>
/// <item><b>Intact:</b> every archived file (message and attachments) still exists with the size and SHA-256 recorded when it
/// was downloaded, and the database itself passes SQLite's structural check.</item>
/// </list>
/// Only both together give <see cref="VerificationVerdict.Verified"/>. The result describes that moment: mail that arrives
/// afterwards is not covered. Read-only: nothing in the archive or in Gmail is changed (the report is saved).
/// </summary>
public sealed class Verifier(AccountStore accounts, MessageStore messages, ArchiveLayout layout, TimeProvider time, ILogger<Verifier> logger)
{
    private const int Parallelism = 4;

    /// <param name="mailbox">The account's Gmail mailbox, or null to check the files only (offline, or a disconnected account).</param>
    public async Task<VerificationReport> VerifyAsync(AccountRecord account, IMailbox? mailbox, IProgress<VerificationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var started = time.GetUtcNow();
        var issues = new ConcurrentQueue<VerificationIssue>();
        long issueCount = 0;
        long missingFromArchive = 0;
        long filesChecked = 0;
        long filesMissing = 0;
        long filesCorrupt = 0;
        long bytesChecked = 0;
        long gmailMessages = 0;
        var gmailChecked = false;
        string? gmailError = null;
        var databaseIntact = true;

        void Add(VerificationIssue issue)
        {
            if (Interlocked.Increment(ref issueCount) <= VerificationReport.MaxListedIssues)
            {
                issues.Enqueue(issue);
            }
        }

        try
        {
            progress?.Report(new VerificationProgress(VerificationPhase.Starting, 0, 0));

            if (mailbox is not null)
            {
                try
                {
                    progress?.Report(new VerificationProgress(VerificationPhase.ListingGmail, 0, 0));
                    var archived = messages.GetIds(account.Id);
                    var listed = 0L;
                    await foreach (var id in mailbox.ListMessageIdsAsync(null, cancellationToken).ConfigureAwait(false))
                    {
                        listed++;
                        if (!archived.Contains(id))
                        {
                            missingFromArchive++;
                            Add(new VerificationIssue(VerificationIssueKind.MissingFromArchive, id, "Gmail has this message but it is not downloaded yet."));
                        }

                        if (listed % 1000 == 0)
                        {
                            progress?.Report(new VerificationProgress(VerificationPhase.ListingGmail, listed, 0));
                        }
                    }

                    gmailMessages = listed;
                    gmailChecked = true;
                }
                catch (Exception ex) when (ex is ReconnectRequiredException or MailboxUnavailableException or QuotaExhaustedException)
                {
                    gmailError = ex.Message;
                    logger.LogInformation("Verification of {Account}: Gmail could not be checked ({Reason})", account.Email, ex.Message);
                }
            }

            var total = messages.Count(account.Id);
            long done = 0;
            progress?.Report(new VerificationProgress(VerificationPhase.CheckingFiles, 0, total));
            foreach (var page in messages.EnumerateFiles(account.Id))
            {
                var attachments = messages.GetAttachmentFiles(page);
                await Parallel.ForEachAsync(page, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken }, (file, token) =>
                {
                    CheckFile(file, token);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

                await Parallel.ForEachAsync(attachments, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken }, (file, token) =>
                {
                    CheckAttachment(file, token);
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);

                done += page.Count;
                progress?.Report(new VerificationProgress(VerificationPhase.CheckingFiles, done, total));
            }

            progress?.Report(new VerificationProgress(VerificationPhase.CheckingDatabase, 0, 0));
            if (messages.CheckIntegrity() is { } problem)
            {
                databaseIntact = false;
                Add(new VerificationIssue(VerificationIssueKind.DatabaseDamaged, null, $"The archive database reports: {problem}"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Build(VerificationVerdict.Cancelled);
        }

        var verdict = issueCount > 0
            ? VerificationVerdict.Problems
            : gmailChecked ? VerificationVerdict.Verified : VerificationVerdict.FilesIntactGmailNotChecked;
        var report = Build(verdict);
        accounts.SaveVerification(account.Id, report);
        logger.LogInformation("Verified {Account}: {Verdict}; {Files} files, {NotDownloaded} not yet downloaded, {Missing} missing, {Damaged} damaged",
            account.Email, verdict, filesChecked, missingFromArchive, filesMissing, filesCorrupt);
        return report;

        VerificationReport Build(VerificationVerdict result) => new(
            started, time.GetUtcNow(), result, gmailChecked, gmailMessages, messages.Count(account.Id), missingFromArchive,
            filesChecked, filesMissing, filesCorrupt, bytesChecked, databaseIntact, [.. issues],
            (int)Math.Max(0, issueCount - VerificationReport.MaxListedIssues), gmailError);

        void CheckFile(StoredMessageFile file, CancellationToken token)
        {
            var path = layout.Resolve(account.Folder, file.EmlPath);
            Interlocked.Increment(ref filesChecked);
            Examine(path, file.SizeBytes, file.Sha256, token, VerificationIssueKind.FileMissing, VerificationIssueKind.FileCorrupt, file.GmailId, "message file");
        }

        void CheckAttachment(StoredAttachmentFile file, CancellationToken token)
        {
            var path = layout.Resolve(account.Folder, file.Path);
            Interlocked.Increment(ref filesChecked);
            Examine(path, file.SizeBytes, file.Sha256, token, VerificationIssueKind.AttachmentMissing, VerificationIssueKind.AttachmentCorrupt, file.GmailId, $"attachment {file.FileName}");
        }

        void Examine(string path, long expectedSize, string expectedHash, CancellationToken token, VerificationIssueKind missing, VerificationIssueKind corrupt, string messageId, string what)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    Interlocked.Increment(ref filesMissing);
                    Add(new VerificationIssue(missing, messageId, $"The {what} is missing from the archive folder."));
                    return;
                }

                Interlocked.Add(ref bytesChecked, info.Length);
                if (info.Length != expectedSize)
                {
                    Interlocked.Increment(ref filesCorrupt);
                    Add(new VerificationIssue(corrupt, messageId, $"The {what} has a different size ({info.Length:N0} bytes) than when it was downloaded ({expectedSize:N0})."));
                    return;
                }

                if (!string.Equals(FileStore.Sha256Hex(path, token), expectedHash, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref filesCorrupt);
                    Add(new VerificationIssue(corrupt, messageId, $"The {what} no longer matches the checksum saved when it was downloaded."));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref filesCorrupt);
                Add(new VerificationIssue(corrupt, messageId, $"The {what} could not be read: {ex.Message}"));
            }
        }
    }
}
