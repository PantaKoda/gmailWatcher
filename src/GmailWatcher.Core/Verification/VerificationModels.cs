namespace GmailWatcher.Core.Verification;

public enum VerificationPhase
{
    Starting,
    /// <summary>Asking Gmail for the list of messages it holds right now.</summary>
    ListingGmail,
    /// <summary>Re-reading every archived file and comparing it with its saved checksum.</summary>
    CheckingFiles,
    CheckingDatabase,
}

public sealed record VerificationProgress(VerificationPhase Phase, long Done, long Total);

public enum VerificationIssueKind
{
    /// <summary>Gmail lists this message but the archive has no copy of it.</summary>
    MissingFromArchive,
    /// <summary>The archive has a record of the message but its .eml file is gone.</summary>
    FileMissing,
    /// <summary>The file's size or checksum differs from what was saved when it was downloaded.</summary>
    FileCorrupt,
    AttachmentMissing,
    AttachmentCorrupt,
    DatabaseDamaged,
    /// <summary>
    /// Windows or an antivirus refused to let the app read an extracted attachment copy. Only a note while the saved message
    /// file (which contains the attachment) is intact.
    /// </summary>
    AttachmentBlocked,
    /// <summary>
    /// Gmail's message list still names this ID, but asking Gmail for the message answers "no such message" (the list can lag behind
    /// after a large deletion). There is nothing to download, so it is only a note.
    /// </summary>
    ListedButGone,
}

public sealed record VerificationIssue(VerificationIssueKind Kind, string? MessageId, string Detail);

public enum VerificationVerdict
{
    /// <summary>Gmail was checked and the files were checked: every message Gmail listed is archived and intact.</summary>
    Verified,
    /// <summary>The files are intact, but Gmail could not be asked (offline, disconnected account), so completeness is unknown.</summary>
    FilesIntactGmailNotChecked,
    /// <summary>Something is missing or damaged. <see cref="VerificationReport.Issues"/> lists what.</summary>
    Problems,
    /// <summary>Stopped before it finished; nothing can be concluded.</summary>
    Cancelled,
}

/// <summary>
/// The result of checking that an account's archive can replace its Gmail mailbox. It describes the moment it ran:
/// mail that arrives later is not covered, so sync again right before deleting anything.
/// </summary>
public sealed record VerificationReport(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    VerificationVerdict Verdict,
    bool GmailChecked,
    long GmailMessages,
    long ArchivedMessages,
    long MissingFromArchive,
    long FilesChecked,
    long FilesMissing,
    long FilesCorrupt,
    long BytesChecked,
    bool DatabaseIntact,
    IReadOnlyList<VerificationIssue> Issues,
    int IssuesNotListed,
    /// <summary>Why Gmail could not be asked (offline, signed out...); null when it was, or was not meant to be.</summary>
    string? GmailCheckError = null,
    /// <summary>
    /// Findings that do not make the archive incomplete: an extracted attachment copy that is missing, changed or blocked while
    /// the saved message file, which contains the attachment byte for byte, is intact. Null in reports saved by earlier versions.
    /// </summary>
    IReadOnlyList<VerificationIssue>? Notices = null,
    int NoticesNotListed = 0)
{
    public const int MaxListedIssues = 200;

    public bool IsClean => Verdict == VerificationVerdict.Verified;

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<VerificationIssue> NoticeList => Notices ?? [];

    /// <summary>How many notes there are in all, listed or not.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int NoticeCount => NoticeList.Count + NoticesNotListed;
}
