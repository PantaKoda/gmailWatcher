using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Sync;
using GmailWatcher.Core.Verification;
using GmailWatcher.Desktop.Services;

namespace GmailWatcher.Desktop.Presentation;

public enum VerifyState
{
    /// <summary>Nothing to verify yet (no mail), or never verified.</summary>
    Never,
    /// <summary>Verified, and nothing was synced since.</summary>
    Verified,
    /// <summary>Verified, but more was synced afterwards: the result may no longer cover everything.</summary>
    Stale,
    Problems,
    /// <summary>The files are intact, but Gmail could not be asked.</summary>
    FilesOnly,
}

/// <summary>Everything a card shows for one account, as plain data.</summary>
public sealed record AccountDisplay(
    StatusTone Tone,
    string? ConnectionTag,
    string Summary,
    string SyncLine,
    bool SyncLineIsProblem,
    string VerifyLine,
    VerifyState Verify,
    bool HasMail,
    bool IsBusy,
    bool ShowProgress,
    bool ProgressIsIndeterminate,
    double ProgressPercent,
    string ProgressText,
    bool CanSync,
    bool CanStop,
    bool CanVerify,
    bool NeedsReconnect,
    bool IsArchiveOnly)
{
    /// <summary>False when there is nothing worth saying yet (a first download is running and nothing is stored).</summary>
    public bool HasSummary => Summary.Length > 0;
}

/// <summary>
/// Turns an account's stored state and live activity into what the card says. All the wording that tells a person whether
/// their mail is safely archived lives here, so it can be tested and kept consistent.
/// </summary>
public static class AccountPresenter
{
    public static AccountDisplay Present(AccountRecord account, AccountStats? stats, AccountActivity activity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(activity);
        var hasMail = stats is { Messages: > 0 };
        var busy = activity.IsBusy;
        var verify = VerifyOf(account, activity);
        var (progressText, percent, indeterminate) = busy ? ProgressOf(activity) : ("", 0, false);
        var (syncLine, problem) = busy ? (progressText, false) : SyncLineOf(account, stats, activity, now);

        return new AccountDisplay(
            Tone: ToneOf(account, stats, activity, verify),
            ConnectionTag: account.Connection switch
            {
                AccountConnection.ReconnectRequired => "RECONNECT",
                AccountConnection.ArchiveOnly => "ARCHIVE ONLY",
                _ => null,
            },
            Summary: busy && stats is not { Messages: > 0 } ? "" : SummaryOf(stats),
            SyncLine: syncLine,
            SyncLineIsProblem: problem,
            VerifyLine: hasMail || verify != VerifyState.Never ? VerifyLineOf(account, verify, activity, now) : "",
            Verify: verify,
            HasMail: hasMail,
            IsBusy: busy,
            ShowProgress: busy,
            ProgressIsIndeterminate: indeterminate,
            ProgressPercent: percent,
            ProgressText: progressText,
            CanSync: !busy && account.Connection == AccountConnection.Connected,
            CanStop: busy,
            CanVerify: !busy && hasMail,
            NeedsReconnect: account.Connection != AccountConnection.Connected && !busy,
            IsArchiveOnly: account.Connection == AccountConnection.ArchiveOnly);
    }

    public static string SummaryOf(AccountStats? stats)
    {
        if (stats is null)
        {
            return "Reading the archive…";
        }

        if (stats.Messages == 0)
        {
            return "Nothing downloaded yet";
        }

        var parts = new List<string>
        {
            Formatting.Plural(stats.Messages, "message"),
            Formatting.Bytes(stats.TotalBytes),
        };
        if (stats.Attachments > 0)
        {
            parts.Add(Formatting.Plural(stats.Attachments, "attachment"));
        }

        if (Formatting.Range(stats.Oldest, stats.Newest) is { Length: > 0 } range)
        {
            parts.Add(range);
        }

        return string.Join(" · ", parts);
    }

    internal static VerifyState VerifyOf(AccountRecord account, AccountActivity activity)
    {
        var verdict = activity.LastReport is { Verdict: not VerificationVerdict.Cancelled } report ? report.Verdict.ToString() : account.LastVerificationVerdict;
        var verifiedAt = activity.LastReport is { Verdict: not VerificationVerdict.Cancelled } r ? r.CompletedAt : account.LastVerifiedAt;
        if (verdict is null || verifiedAt is null)
        {
            return VerifyState.Never;
        }

        return verdict switch
        {
            nameof(VerificationVerdict.Verified) => account.LastSyncCompletedAt is { } synced && synced > verifiedAt ? VerifyState.Stale : VerifyState.Verified,
            nameof(VerificationVerdict.Problems) => VerifyState.Problems,
            nameof(VerificationVerdict.FilesIntactGmailNotChecked) => VerifyState.FilesOnly,
            _ => VerifyState.Never,
        };
    }

    private static string VerifyLineOf(AccountRecord account, VerifyState state, AccountActivity activity, DateTimeOffset now)
    {
        var at = activity.LastReport is { Verdict: not VerificationVerdict.Cancelled } r ? r.CompletedAt : account.LastVerifiedAt;
        var ago = at is { } when ? Formatting.Ago(when, now) : "";
        return state switch
        {
            VerifyState.Verified => $"Verified {ago}: everything Gmail listed is archived, and every file is intact.",
            VerifyState.Stale => $"Verified {ago}, but you have synced since. Verify again before you delete anything in Gmail.",
            VerifyState.Problems => $"Verification found problems {ago}. Open the details to see them.",
            VerifyState.FilesOnly => $"The files are intact ({ago}), but Gmail couldn't be checked, so completeness isn't confirmed.",
            _ => "Not verified yet. Verify before you delete anything in Gmail.",
        };
    }

    private static (string Text, bool IsProblem) SyncLineOf(AccountRecord account, AccountStats? stats, AccountActivity activity, DateTimeOffset now)
    {
        switch (account.Connection)
        {
            case AccountConnection.ReconnectRequired:
                return ("Google no longer accepts the saved sign-in. Nothing is lost: reconnect to keep syncing.", true);
            case AccountConnection.ArchiveOnly:
                return ("Disconnected from Google. The archive stays here; connect again to sync more mail.", false);
        }

        var last = activity.LastSync;
        var outcome = last?.Outcome.ToString() ?? account.LastSyncOutcome;
        var error = last?.Error ?? account.LastError;
        var finished = account.LastSyncCompletedAt;

        if (outcome is nameof(SyncOutcome.Failed) or nameof(SyncOutcome.QuotaExhausted) or nameof(SyncOutcome.DiskFull) or nameof(SyncOutcome.ReconnectRequired))
        {
            var before = finished is { } f ? $" Last complete sync: {Formatting.Ago(f, now)}." : " Press Sync to continue where it left off.";
            return ($"The last sync stopped early: {error ?? "an error occurred"}.{before}", true);
        }

        if (finished is null)
        {
            return outcome == nameof(SyncOutcome.Cancelled)
                ? ("Stopped before it finished. Press Sync to continue where it left off.", false)
                : ("Not downloaded yet. Press Sync to start; a big mailbox can take hours, and you can stop and continue any time.", false);
        }

        var line = $"Last synced {Formatting.Ago(finished.Value, now)}";
        if (last is { Outcome: SyncOutcome.Completed } done)
        {
            line += done.MessagesAdded > 0 ? $" · {Formatting.Plural(done.MessagesAdded, "new message")}" : " · already up to date";
        }

        if (stats is { RemoteDeleted: > 0 })
        {
            line += $" · {Formatting.Count(stats.RemoteDeleted)} no longer in Gmail";
        }

        return (line, false);
    }

    private static StatusTone ToneOf(AccountRecord account, AccountStats? stats, AccountActivity activity, VerifyState verify)
    {
        if (activity.IsBusy)
        {
            return StatusTone.Running;
        }

        if (account.Connection == AccountConnection.ReconnectRequired)
        {
            return StatusTone.Warning;
        }

        if (account.Connection == AccountConnection.ArchiveOnly)
        {
            return verify == VerifyState.Verified ? StatusTone.Success : StatusTone.Neutral;
        }

        var outcome = activity.LastSync?.Outcome.ToString() ?? account.LastSyncOutcome;
        return outcome switch
        {
            nameof(SyncOutcome.Failed) => StatusTone.Failure,
            nameof(SyncOutcome.QuotaExhausted) or nameof(SyncOutcome.DiskFull) or nameof(SyncOutcome.ReconnectRequired) => StatusTone.Warning,
            _ => verify switch
            {
                VerifyState.Verified => StatusTone.Success,
                VerifyState.Problems => StatusTone.Failure,
                VerifyState.Stale or VerifyState.FilesOnly => StatusTone.Warning,
                _ => stats is { Messages: > 0 } ? StatusTone.Neutral : StatusTone.Unknown,
            },
        };
    }

    internal static (string Text, double Percent, bool Indeterminate) ProgressOf(AccountActivity activity)
    {
        if (activity.Kind == ActivityKind.Queued)
        {
            return ("Waiting for another account to finish…", 0, true);
        }

        if (activity.Kind == ActivityKind.Verifying)
        {
            var v = activity.Verification;
            return v?.Phase switch
            {
                VerificationPhase.ListingGmail => (v.Done > 0 ? $"Asking Gmail what it has now… {Formatting.Count(v.Done)} messages" : "Asking Gmail what it has now…", 0, true),
                VerificationPhase.CheckingFiles => ($"Checking files… {Formatting.Count(v.Done)} of {Formatting.Count(v.Total)} messages", v.Total > 0 ? 100.0 * v.Done / v.Total : 0, v.Total <= 0),
                VerificationPhase.CheckingDatabase => ("Checking the database…", 0, true),
                _ => ("Starting the check…", 0, true),
            };
        }

        var s = activity.Sync ?? SyncProgress.Starting;
        return s.Phase switch
        {
            SyncPhase.Labels => ("Reading labels and settings…", 0, true),
            SyncPhase.Listing => (s.Done > 0 ? $"Asking Gmail what it has… {Formatting.Count(s.Done)} messages so far" : "Asking Gmail what it has…", 0, true),
            SyncPhase.Downloading => (DownloadText(s), s.Fraction * 100 ?? 0, s.Total <= 0),
            SyncPhase.Reconciling => (s.Detail is { Length: > 0 } label ? $"Checking labels… ({label})" : "Checking labels…", s.Total > 0 ? 100.0 * s.Done / s.Total : 0, s.Total <= 0),
            SyncPhase.Analytics => ("Updating analytics…", 0, true),
            SyncPhase.Finishing => ("Finishing…", 0, true),
            _ => ("Starting…", 0, true),
        };
    }

    private static string DownloadText(SyncProgress s)
    {
        var text = $"Downloading {Formatting.Count(s.Done)} of {Formatting.Count(s.Total)}";
        if (s.BytesDownloaded > 0)
        {
            text += $" · {Formatting.Bytes(s.BytesDownloaded)}";
        }

        if (s.Remaining is { } remaining)
        {
            text += $" · {Formatting.Remaining(remaining)} left";
        }

        return text;
    }
}
