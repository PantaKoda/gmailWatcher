namespace GmailWatcher.Core.Mail;

/// <summary>What Gmail says about the mailbox itself (users.getProfile).</summary>
/// <param name="EmailAddress">The mailbox's address. Used to confirm that a sign-in is for the account the user expects.</param>
/// <param name="MessagesTotal">Gmail's own message count. Informational: it is not compared for equality, because Gmail's count can differ from what <c>messages.list</c> returns (for example for chats).</param>
/// <param name="HistoryId">The mailbox's current history ID: the point from which later changes are read.</param>
public sealed record MailboxProfile(string EmailAddress, long MessagesTotal, long ThreadsTotal, string HistoryId);

public enum LabelKind
{
    System,
    User,
}

public sealed record RemoteLabel(string Id, string Name, LabelKind Kind, string? BackgroundColor = null, string? TextColor = null);

/// <summary>
/// One message exactly as Gmail stores it. <see cref="Raw"/> is the complete RFC 822 message (headers, bodies and
/// attachments) and is what the archive keeps; everything else is Gmail-side metadata that the raw text cannot carry.
/// </summary>
public sealed record RemoteMessage(
    string Id,
    string ThreadId,
    IReadOnlyList<string> LabelIds,
    string HistoryId,
    long InternalDateMs,
    long SizeEstimate,
    string Snippet,
    byte[] Raw);

public enum HistoryChangeKind
{
    MessageAdded,
    /// <summary>The message was permanently deleted in Gmail (not merely moved to Trash).</summary>
    MessageDeleted,
    LabelsChanged,
}

/// <summary>One change from users.history.list, in the order Gmail reported it.</summary>
public sealed record HistoryChange(
    HistoryChangeKind Kind,
    string MessageId,
    IReadOnlyList<string> LabelsAdded,
    IReadOnlyList<string> LabelsRemoved)
{
    public static HistoryChange Added(string id) => new(HistoryChangeKind.MessageAdded, id, [], []);

    public static HistoryChange Deleted(string id) => new(HistoryChangeKind.MessageDeleted, id, [], []);

    public static HistoryChange Labels(string id, IReadOnlyList<string> added, IReadOnlyList<string> removed) => new(HistoryChangeKind.LabelsChanged, id, added, removed);
}

/// <summary>Everything that changed since a history ID, and the ID to continue from next time.</summary>
public sealed record HistoryResult(IReadOnlyList<HistoryChange> Changes, string LatestHistoryId);

/// <summary>A piece of mailbox configuration saved with the archive (filters, send-as aliases, forwarding, vacation reply...).</summary>
/// <param name="Name">A stable name such as "filters" or "sendAs".</param>
/// <param name="Json">The API's JSON for it, unchanged.</param>
public sealed record SettingsDocument(string Name, string Json);

/// <summary>
/// Read-only access to one Gmail mailbox. Nothing here can change or delete anything in Gmail: the app only ever
/// asks for the read-only scope. Implemented over the Gmail REST API by <c>GmailWatcher.Google</c>.
/// </summary>
public interface IMailbox
{
    Task<MailboxProfile> GetProfileAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteLabel>> GetLabelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The IDs of every message, newest first, including Spam and Trash. With <paramref name="labelId"/>, only
    /// the messages that carry that label. Pages are fetched as the sequence is consumed.
    /// </summary>
    IAsyncEnumerable<string> ListMessageIdsAsync(string? labelId, CancellationToken cancellationToken);

    /// <exception cref="HistoryExpiredException">Gmail no longer has changes from <paramref name="startHistoryId"/>; do a full pass instead.</exception>
    Task<HistoryResult> GetHistoryAsync(string startHistoryId, CancellationToken cancellationToken);

    /// <summary>The complete message, or null when it no longer exists (deleted between listing and download).</summary>
    Task<RemoteMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>
    /// The mailbox's configuration. A setting the account's permissions or type don't allow is left out rather than
    /// failing the call.
    /// </summary>
    Task<IReadOnlyList<SettingsDocument>> GetSettingsAsync(CancellationToken cancellationToken);
}

/// <summary>Opens the mailbox of a connected account, signing in with its saved credentials.</summary>
public interface IMailboxFactory
{
    /// <exception cref="ReconnectRequiredException">The account's access was revoked or expired; the user must sign in again.</exception>
    Task<IMailbox> OpenAsync(string accountEmail, CancellationToken cancellationToken);
}

/// <summary>Google no longer accepts the saved sign-in (revoked, expired, password changed). Retrying cannot help; the user must reconnect.</summary>
public sealed class ReconnectRequiredException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Gmail no longer keeps the changes from the requested history ID.</summary>
public sealed class HistoryExpiredException(string message = "Gmail no longer has the history this sync would continue from.") : Exception(message);

/// <summary>Gmail or the network could not be used right now (after the usual retries). A later sync continues where this one stopped.</summary>
public sealed class MailboxUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Gmail's usage limit for this account is used up (for example the daily limit). Trying again later is the only fix.</summary>
public sealed class QuotaExhaustedException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
