using GmailWatcher.Archive.Ingest;
using GmailWatcher.Core.Accounts;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Archive.Storage;

/// <summary>
/// Brings the text-derived data of already archived messages (subject, sender and recipient names, the search index) up to
/// date when the way mail text is decoded improves. The saved .eml files are the source and are only read: they are byte
/// for byte what Gmail returned, so nothing is lost when an older version decoded them badly. Runs once per account and
/// version, then does nothing.
/// </summary>
public sealed class TextRefresher(MessageStore messages, MessageIngestor ingestor, ArchiveLayout layout, ILogger<TextRefresher> logger)
{
    /// <summary>
    /// Raise this when decoding gets better. 1 = how the first release read mail; 2 = legacy charsets (Greek, Cyrillic, ...)
    /// are decoded instead of coming out as Latin letters.
    /// </summary>
    public const int CurrentVersion = 2;

    private const int PageSize = 200;

    /// <summary>Returns how many messages had visible text that changed (0 when there was nothing to do).</summary>
    public int RefreshIfNeeded(AccountRecord account, Action<long, long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (messages.GetTextVersion(account.Id) >= CurrentVersion)
        {
            return 0;
        }

        var total = messages.Count(account.Id);
        long done = 0;
        var changed = 0;
        var unreadable = 0;
        foreach (var page in messages.EnumerateFiles(account.Id, PageSize))
        {
            var batch = new List<(long Pk, MessageText Text)>(page.Count);
            foreach (var file in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] raw;
                try
                {
                    raw = File.ReadAllBytes(layout.Resolve(account.Folder, file.EmlPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadable++; // Verify reports missing files; here the message simply keeps what it had
                    continue;
                }

                if (ingestor.ReadText(raw, cancellationToken) is { } text)
                {
                    batch.Add((file.Pk, text));
                }
            }

            changed += messages.ApplyText(batch);
            done += page.Count;
            progress?.Invoke(done, total);
        }

        // Only now: an interrupted refresh starts over next time, which is harmless because every step is a replacement.
        messages.SetTextVersion(account.Id, CurrentVersion);
        logger.LogInformation("Refreshed the text of {Messages} archived messages of {Account}: {Changed} had changed, {Unreadable} files could not be read",
            done, account.Email, changed, unreadable);
        return changed;
    }
}
