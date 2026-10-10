using GmailWatcher.Archive.Sync;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Verification;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Archive.Storage;

public sealed record RepairPlan(int Redownloadable, int Unrecoverable);

/// <summary>
/// The only code that removes archived data, and only when the user asks. Nothing here runs because Gmail no longer
/// has a message.
/// </summary>
public sealed class ArchiveMaintenance(AccountStore accounts, MessageStore messages, ArchiveLayout layout, ILogger<ArchiveMaintenance> logger, IAnalyticsSink? analytics = null)
{
    /// <summary>
    /// Makes the next sync download damaged or missing messages again. A message Gmail has already deleted cannot be
    /// downloaded again: those are left alone (what is left of them is all there is) and counted as unrecoverable.
    /// </summary>
    public RepairPlan Repair(AccountRecord account, VerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(report);
        var damaged = report.Issues
            .Where(i => i.MessageId is not null && i.Kind is VerificationIssueKind.FileMissing or VerificationIssueKind.FileCorrupt
                or VerificationIssueKind.AttachmentMissing or VerificationIssueKind.AttachmentCorrupt)
            .Select(i => i.MessageId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var goneFromGmail = messages.GetRemoteDeleted(account.Id, damaged);
        var redownloadable = damaged.Where(id => !goneFromGmail.Contains(id)).ToList();
        messages.Forget(account.Id, redownloadable);
        if (redownloadable.Count > 0)
        {
            // Gmail's history shows no change for these messages, so only a full pass will notice they are missing.
            accounts.ClearHistory(account.Id);
        }

        logger.LogInformation("Repair of {Account}: {Count} messages will be downloaded again, {Gone} cannot be", account.Email, redownloadable.Count, goneFromGmail.Count);
        return new RepairPlan(redownloadable.Count, goneFromGmail.Count);
    }

    /// <summary>
    /// Deletes an account's archive from this PC: its database rows and its folder of mail and attachments. Gmail is not
    /// touched. The folder must be that account's own folder inside the archive.
    /// </summary>
    public void DeleteAccountData(AccountRecord account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var directory = layout.AccountDirectory(account.Folder);
        if (string.Equals(directory, Path.TrimEndingDirectorySeparator(layout.AccountsDirectory), StringComparison.OrdinalIgnoreCase)
            || !ArchiveLayout.IsInside(layout.AccountsDirectory, directory))
        {
            throw new InvalidOperationException($"'{directory}' is not an account folder; nothing was deleted.");
        }

        logger.LogWarning("Deleting the archive of {Account} from this PC", account.Email);
        accounts.DeleteRows(account.Id);
        analytics?.Remove(account.Id);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
