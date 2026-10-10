namespace GmailWatcher.Core.Accounts;

/// <summary>How an account relates to Gmail right now.</summary>
public enum AccountConnection
{
    /// <summary>Signed in; it can be synced.</summary>
    Connected,

    /// <summary>Google no longer accepts the saved sign-in. Nothing is lost: the archive stays, and Reconnect signs in again.</summary>
    ReconnectRequired,

    /// <summary>Disconnected on purpose. The archive stays browsable and verifiable; connect again to sync more.</summary>
    ArchiveOnly,
}

/// <summary>An account as stored in the archive database.</summary>
public sealed record AccountRecord(
    int Id,
    string Email,
    string? DisplayName,
    string Folder,
    AccountConnection Connection,
    DateTimeOffset AddedAt,
    DateTimeOffset? InitialSyncCompletedAt,
    DateTimeOffset? LastSyncStartedAt,
    DateTimeOffset? LastSyncCompletedAt,
    string? LastSyncOutcome,
    string? LastError,
    long? RemoteMessagesTotal,
    DateTimeOffset? LastVerifiedAt,
    string? LastVerificationVerdict)
{
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Email : DisplayName;
}

/// <summary>What is archived for one account. Computed from the database, never from Gmail.</summary>
public sealed record AccountStats(
    long Messages,
    long MessageBytes,
    long Threads,
    long Attachments,
    long AttachmentBytes,
    long RemoteDeleted,
    long Labels,
    DateTimeOffset? Oldest,
    DateTimeOffset? Newest)
{
    public static AccountStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, null, null);

    public long TotalBytes => MessageBytes + AttachmentBytes;
}

/// <summary>
/// The refresh token of one account. Secret: kept only in <see cref="ICredentialStore"/>, never in SQLite, settings,
/// logs, URLs or the clipboard. <see cref="ToString"/> is redacted.
/// </summary>
public sealed record StoredCredential(string RefreshToken)
{
    public override string ToString() => "StoredCredential { redacted }";
}

/// <summary>Secure per-account token storage (Windows Credential Manager, or memory for a session-only fallback).</summary>
public interface ICredentialStore
{
    /// <summary>False for session-only storage: tokens are lost when the app exits.</summary>
    bool IsPersistent { get; }

    /// <summary>User-facing name of where tokens are kept, e.g. "Windows Credential Manager".</summary>
    string Description { get; }

    Task<StoredCredential?> ReadAsync(string accountEmail, CancellationToken cancellationToken = default);

    Task WriteAsync(string accountEmail, StoredCredential credential, CancellationToken cancellationToken = default);

    Task DeleteAsync(string accountEmail, CancellationToken cancellationToken = default);
}
