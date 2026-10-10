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
/// <para>
/// A message's .eml file is the backup: it is byte-identical to what Gmail returned and contains every attachment. The
/// attachments are also saved as separate files for convenience. When such a copy is missing, changed or blocked (an
/// antivirus can refuse to let anyone read a file it flags) but the .eml is intact, nothing is lost: that is reported as a
/// note (<see cref="VerificationReport.Notices"/>) and does not stop the archive from being verified. If the .eml is damaged
/// too, the copy may be all that is left, and everything is a problem.
/// </para>
/// </summary>
public sealed class Verifier(AccountStore accounts, MessageStore messages, ArchiveLayout layout, TimeProvider time, ILogger<Verifier> logger)
{
    private const int Parallelism = 4;

    // Win32 ERROR_VIRUS_INFECTED / ERROR_VIRUS_DELETED, as the low 16 bits of the HRESULT .NET puts on the IOException.
    private const int ErrorVirusInfected = 225;
    private const int ErrorVirusDeleted = 226;

    /// <summary>What was wrong with one file.</summary>
    internal enum FileState
    {
        Missing,
        /// <summary>Its size or checksum differs from what was saved.</summary>
        Changed,
        /// <summary>Windows reported the file as containing a virus or unwanted software, and would not let it be read.</summary>
        Blocked,
        /// <summary>Some other reason it could not be read (in use, no permission, a disk error).</summary>
        Unreadable,
    }

    internal readonly record struct FileProblem(FileState State, string Detail);

    /// <summary>True when Windows (Defender or another antivirus) refused to open the file because it flagged it.</summary>
    internal static bool IsBlockedByAntivirus(Exception exception) =>
        exception is IOException && (exception.HResult & 0xFFFF) is ErrorVirusInfected or ErrorVirusDeleted;

    /// <param name="mailbox">The account's Gmail mailbox, or null to check the files only (offline, or a disconnected account).</param>
    public async Task<VerificationReport> VerifyAsync(AccountRecord account, IMailbox? mailbox, IProgress<VerificationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var started = time.GetUtcNow();
        var issues = new ConcurrentQueue<VerificationIssue>();
        var notices = new ConcurrentQueue<VerificationIssue>();
        // Messages whose own .eml is not intact: for those, a bad attachment copy may be the only copy left.
        var damagedMessages = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        long issueCount = 0;
        long noticeCount = 0;
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

        void AddNotice(VerificationIssue notice)
        {
            if (Interlocked.Increment(ref noticeCount) <= VerificationReport.MaxListedIssues)
            {
                notices.Enqueue(notice);
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
                // The message files first: whether an attachment copy matters depends on whether its message file is intact.
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
        logger.LogInformation("Verified {Account}: {Verdict}; {Files} files, {NotDownloaded} not yet downloaded, {Missing} missing, {Damaged} damaged, {Notes} attachment copies noted",
            account.Email, verdict, filesChecked, missingFromArchive, filesMissing, filesCorrupt, noticeCount);
        return report;

        VerificationReport Build(VerificationVerdict result) => new(
            started, time.GetUtcNow(), result, gmailChecked, gmailMessages, messages.Count(account.Id), missingFromArchive,
            filesChecked, filesMissing, filesCorrupt, bytesChecked, databaseIntact, [.. issues],
            (int)Math.Max(0, issueCount - VerificationReport.MaxListedIssues), gmailError,
            [.. notices], (int)Math.Max(0, noticeCount - VerificationReport.MaxListedIssues));

        void CheckFile(StoredMessageFile file, CancellationToken token)
        {
            Interlocked.Increment(ref filesChecked);
            if (Examine(layout.Resolve(account.Folder, file.EmlPath), file.SizeBytes, file.Sha256, token, "message file") is not { } problem)
            {
                return;
            }

            damagedMessages.TryAdd(file.GmailId, 0);
            if (problem.State == FileState.Missing)
            {
                Interlocked.Increment(ref filesMissing);
                Add(new VerificationIssue(VerificationIssueKind.FileMissing, file.GmailId, problem.Detail));
            }
            else
            {
                Interlocked.Increment(ref filesCorrupt);
                var detail = problem.State == FileState.Blocked
                    ? problem.Detail + " Open Windows Security, then Virus & threat protection, then Protection history: if you trust it, restore or allow it there, and verify again."
                    : problem.Detail;
                Add(new VerificationIssue(VerificationIssueKind.FileCorrupt, file.GmailId, detail));
            }
        }

        void CheckAttachment(StoredAttachmentFile file, CancellationToken token)
        {
            Interlocked.Increment(ref filesChecked);
            if (Examine(layout.Resolve(account.Folder, file.Path), file.SizeBytes, file.Sha256, token, $"attachment {file.FileName}") is not { } problem)
            {
                return;
            }

            if (damagedMessages.ContainsKey(file.GmailId))
            {
                // The message file is damaged too, so this copy may be all that is left of the attachment.
                if (problem.State == FileState.Missing)
                {
                    Interlocked.Increment(ref filesMissing);
                    Add(new VerificationIssue(VerificationIssueKind.AttachmentMissing, file.GmailId, problem.Detail));
                }
                else
                {
                    Interlocked.Increment(ref filesCorrupt);
                    Add(new VerificationIssue(VerificationIssueKind.AttachmentCorrupt, file.GmailId, problem.Detail));
                }

                return;
            }

            var kind = problem.State switch
            {
                FileState.Missing => VerificationIssueKind.AttachmentMissing,
                FileState.Blocked => VerificationIssueKind.AttachmentBlocked,
                _ => VerificationIssueKind.AttachmentCorrupt,
            };
            AddNotice(new VerificationIssue(kind, file.GmailId, problem.Detail));
        }

        FileProblem? Examine(string path, long expectedSize, string expectedHash, CancellationToken token, string what)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return new FileProblem(FileState.Missing, $"The {what} is missing from the archive folder (an antivirus or a cleanup tool may have removed it).");
                }

                Interlocked.Add(ref bytesChecked, info.Length);
                if (info.Length != expectedSize)
                {
                    return new FileProblem(FileState.Changed, $"The {what} has a different size ({info.Length:N0} bytes) than when it was downloaded ({expectedSize:N0}).");
                }

                return string.Equals(FileStore.Sha256Hex(path, token), expectedHash, StringComparison.Ordinal)
                    ? null
                    : new FileProblem(FileState.Changed, $"The {what} no longer matches the checksum saved when it was downloaded.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows' own wording ("contains a virus or potentially unwanted software") is the clearest thing to show, without the path.
                return IsBlockedByAntivirus(ex)
                    ? new FileProblem(FileState.Blocked, $"Windows or your antivirus blocked access to the {what}: it reports that the file contains a virus or potentially unwanted software.")
                    : new FileProblem(FileState.Unreadable, $"The {what} could not be read: {ex.Message}");
            }
        }
    }
}
