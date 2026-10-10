namespace GmailWatcher.Core.Sync;

public enum SyncPhase
{
    Starting,
    /// <summary>Reading the label list and the mailbox settings.</summary>
    Labels,
    /// <summary>Asking Gmail which messages exist (or what changed).</summary>
    Listing,
    /// <summary>Downloading messages and their attachments.</summary>
    Downloading,
    /// <summary>Refreshing label membership of already archived messages.</summary>
    Reconciling,
    /// <summary>Updating the analytics database.</summary>
    Analytics,
    Finishing,
}

/// <summary>
/// Where a sync is. <see cref="Total"/> is 0 while it is not known yet (listing); <see cref="Remaining"/> is null until
/// enough has been downloaded to estimate it.
/// </summary>
public sealed record SyncProgress(
    SyncPhase Phase,
    long Done,
    long Total,
    long BytesDownloaded,
    TimeSpan? Remaining,
    string? Detail = null)
{
    public static SyncProgress Starting { get; } = new(SyncPhase.Starting, 0, 0, 0, null);

    public double? Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : null;
}

public enum SyncOutcome
{
    /// <summary>Everything Gmail listed is now archived.</summary>
    Completed,
    /// <summary>Stopped by the user or by quitting. What was downloaded is kept; the next sync continues.</summary>
    Cancelled,
    /// <summary>Stopped by an error (<see cref="SyncResult.Error"/> says which). What was downloaded is kept.</summary>
    Failed,
    /// <summary>Google no longer accepts the saved sign-in.</summary>
    ReconnectRequired,
    /// <summary>Gmail's usage limit is used up; trying later works.</summary>
    QuotaExhausted,
    /// <summary>The archive drive is nearly full.</summary>
    DiskFull,
}

/// <summary>What one sync did. Counts describe this run only.</summary>
public sealed record SyncResult(
    SyncOutcome Outcome,
    int MessagesAdded,
    int LabelChanges,
    int MarkedRemoteDeleted,
    int VanishedBeforeDownload,
    int Failed,
    long BytesDownloaded,
    TimeSpan Elapsed,
    string? Error,
    bool WasInitial,
    bool UsedHistory)
{
    public bool IsSuccess => Outcome == SyncOutcome.Completed;
}
