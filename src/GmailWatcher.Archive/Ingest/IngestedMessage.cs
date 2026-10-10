namespace GmailWatcher.Archive.Ingest;

public sealed record MailAddress(string? Name, string Address);

/// <summary>The fields of a message that come from decoding its text: what the list, the search index and the analytics show.</summary>
public sealed record MessageText(
    string? Subject,
    MailAddress? From,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    IReadOnlyList<MailAddress> Bcc,
    IReadOnlyList<MailAddress> ReplyTo,
    string BodyText);

public sealed record IngestedAttachment(
    int Index,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    bool IsInline,
    string? ContentId,
    /// <summary>Relative to the account folder (forward slashes); null when the attachment was not saved as a separate file.</summary>
    string? RelativePath);

/// <summary>
/// A downloaded message after its files were written: everything the database needs, and no MIME objects, so it can be
/// handed from the download workers to the database writer.
/// </summary>
public sealed record IngestedMessage(
    string GmailId,
    string ThreadId,
    long InternalDateMs,
    IReadOnlyList<string> LabelIds,
    string? GmailHistoryId,
    string Snippet,
    string? Subject,
    MailAddress? From,
    IReadOnlyList<MailAddress> To,
    IReadOnlyList<MailAddress> Cc,
    IReadOnlyList<MailAddress> Bcc,
    IReadOnlyList<MailAddress> ReplyTo,
    string? MessageIdHeader,
    string? InReplyTo,
    long SizeBytes,
    string EmlRelativePath,
    string EmlSha256,
    IReadOnlyList<IngestedAttachment> Attachments,
    string BodyText,
    /// <summary>Set when the message could not be parsed. The complete message is still archived; only search and analytics lose detail.</summary>
    string? ParseError)
{
    /// <summary>Real attachments: embedded images and similar (Content-ID) are kept but not counted.</summary>
    public int AttachmentCount => Attachments.Count(a => !a.IsInline);

    public long AttachmentBytes => Attachments.Where(a => !a.IsInline).Sum(a => a.SizeBytes);
}
