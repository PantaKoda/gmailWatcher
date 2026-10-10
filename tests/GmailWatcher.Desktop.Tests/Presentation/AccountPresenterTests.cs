using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Sync;
using GmailWatcher.Core.Verification;
using GmailWatcher.Desktop.Presentation;
using GmailWatcher.Desktop.Services;

namespace GmailWatcher.Desktop.Tests.Presentation;

/// <summary>The wording that tells a person whether their mail is safely archived. Tested because it must never over-promise.</summary>
public sealed class AccountPresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static AccountRecord Account(
        AccountConnection connection = AccountConnection.Connected,
        DateTimeOffset? synced = null,
        string? outcome = null,
        string? error = null,
        DateTimeOffset? verified = null,
        string? verdict = null) => new(
            1, "me@example.com", null, "me.example.com", connection, Now.AddDays(-30), synced, synced, synced, outcome, error, null, verified, verdict);

    private static AccountStats Stats(long messages = 1200, long remoteDeleted = 0, long attachments = 40) =>
        new(messages, 5_000_000, messages, attachments, 2_000_000, remoteDeleted, 12, Now.AddYears(-5), Now);

    private static AccountDisplay Present(AccountRecord account, AccountStats? stats = null, AccountActivity? activity = null) =>
        AccountPresenter.Present(account, stats, activity ?? AccountActivity.Idle, Now);

    private static VerificationReport Report(VerificationVerdict verdict, DateTimeOffset at) =>
        new(at.AddMinutes(-1), at, verdict, verdict != VerificationVerdict.FilesIntactGmailNotChecked, 10, 10, 0, 10, 0, 0, 1000, true, [], 0);

    // --- Before anything is downloaded ---------------------------------------------------------------------------------

    [Fact]
    public void A_new_account_says_nothing_is_downloaded_and_offers_sync_only()
    {
        var display = Present(Account(), Stats(0));

        Assert.Equal("Nothing downloaded yet", display.Summary);
        Assert.Contains("Not downloaded yet", display.SyncLine, StringComparison.Ordinal);
        Assert.Equal(StatusTone.Unknown, display.Tone);
        Assert.True(display.CanSync);
        Assert.False(display.CanVerify);
        Assert.Equal("", display.VerifyLine);
        Assert.False(display.IsBusy);
    }

    [Fact]
    public void While_stats_are_loading_the_card_says_so()
    {
        Assert.Equal("Reading the archive…", Present(Account(), null).Summary);
    }

    // --- After a sync --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_synced_account_summarizes_messages_size_attachments_and_years()
    {
        var display = Present(Account(synced: Now.AddMinutes(-5)), Stats());

        Assert.Contains("1,200 messages", display.Summary, StringComparison.Ordinal);
        Assert.Contains("40 attachments", display.Summary, StringComparison.Ordinal);
        Assert.Contains("2021–2026", display.Summary, StringComparison.Ordinal);
        Assert.Equal("Last synced 5 minutes ago", display.SyncLine);
        Assert.True(display.CanVerify);
    }

    [Fact]
    public void A_completed_sync_this_session_says_how_much_was_new()
    {
        var result = new SyncResult(SyncOutcome.Completed, 5, 0, 0, 0, 0, 1000, TimeSpan.FromSeconds(3), null, false, true);
        var withNew = Present(Account(synced: Now), Stats(), AccountActivity.Idle with { LastSync = result });
        var nothingNew = Present(Account(synced: Now), Stats(), AccountActivity.Idle with { LastSync = result with { MessagesAdded = 0 } });

        Assert.Equal("Last synced just now · 5 new messages", withNew.SyncLine);
        Assert.Equal("Last synced just now · already up to date", nothingNew.SyncLine);
    }

    [Fact]
    public void Mail_that_is_gone_from_gmail_is_called_out_as_kept_here()
    {
        var display = Present(Account(synced: Now), Stats(remoteDeleted: 800));

        Assert.EndsWith("800 no longer in Gmail", display.SyncLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Failed", StatusTone.Failure)]
    [InlineData("QuotaExhausted", StatusTone.Warning)]
    [InlineData("DiskFull", StatusTone.Warning)]
    public void A_sync_that_stopped_early_says_why_and_that_it_can_continue(string outcome, StatusTone tone)
    {
        var display = Present(Account(outcome: outcome, error: "The network went away."), Stats(300));

        Assert.StartsWith("The last sync stopped early: The network went away.", display.SyncLine, StringComparison.Ordinal);
        Assert.Contains("Press Sync to continue", display.SyncLine, StringComparison.Ordinal);
        Assert.True(display.SyncLineIsProblem);
        Assert.Equal(tone, display.Tone);
    }

    [Fact]
    public void A_stopped_first_sync_offers_to_continue_without_alarm()
    {
        var display = Present(Account(outcome: "Cancelled"), Stats(300));

        Assert.StartsWith("Stopped before it finished.", display.SyncLine, StringComparison.Ordinal);
        Assert.False(display.SyncLineIsProblem);
    }

    // --- Verification --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_fresh_clean_verification_is_the_only_green_state()
    {
        var synced = Now.AddHours(-3);
        var display = Present(Account(synced: synced, verified: Now.AddMinutes(-2), verdict: "Verified"), Stats());

        Assert.Equal(VerifyState.Verified, display.Verify);
        Assert.Equal(StatusTone.Success, display.Tone);
        Assert.StartsWith("Verified 2 minutes ago", display.VerifyLine, StringComparison.Ordinal);
    }

    [Fact]
    public void A_verification_older_than_the_last_sync_asks_for_another()
    {
        var display = Present(Account(synced: Now.AddMinutes(-1), verified: Now.AddHours(-2), verdict: "Verified"), Stats());

        Assert.Equal(VerifyState.Stale, display.Verify);
        Assert.Equal(StatusTone.Warning, display.Tone);
        Assert.Contains("Verify again before you delete anything in Gmail", display.VerifyLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Problems_are_red_and_point_to_the_details()
    {
        var display = Present(Account(synced: Now.AddHours(-1), verified: Now.AddMinutes(-1), verdict: "Problems"), Stats());

        Assert.Equal(VerifyState.Problems, display.Verify);
        Assert.Equal(StatusTone.Failure, display.Tone);
        Assert.Contains("found problems", display.VerifyLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Intact_files_without_a_gmail_check_never_claim_completeness()
    {
        var display = Present(Account(synced: Now.AddHours(-1), verified: Now.AddMinutes(-1), verdict: "FilesIntactGmailNotChecked"), Stats());

        Assert.Equal(VerifyState.FilesOnly, display.Verify);
        Assert.Contains("completeness isn't confirmed", display.VerifyLine, StringComparison.Ordinal);
        Assert.NotEqual(StatusTone.Success, display.Tone);
    }

    [Fact]
    public void A_cancelled_check_this_session_does_not_replace_the_saved_result()
    {
        var saved = Account(synced: Now.AddHours(-3), verified: Now.AddHours(-1), verdict: "Verified");
        var cancelled = AccountActivity.Idle with { LastReport = Report(VerificationVerdict.Cancelled, Now) };

        Assert.Equal(VerifyState.Verified, Present(saved, Stats(), cancelled).Verify);
    }

    [Fact]
    public void A_report_from_this_session_wins_over_the_saved_one()
    {
        var saved = Account(synced: Now.AddHours(-3), verified: Now.AddDays(-1), verdict: "Problems");
        var fresh = AccountActivity.Idle with { LastReport = Report(VerificationVerdict.Verified, Now) };

        Assert.Equal(VerifyState.Verified, Present(saved, Stats(), fresh).Verify);
    }

    // --- Connection ----------------------------------------------------------------------------------------------------

    [Fact]
    public void An_account_that_needs_reconnecting_says_nothing_is_lost()
    {
        var display = Present(Account(AccountConnection.ReconnectRequired, synced: Now.AddDays(-1)), Stats());

        Assert.Equal("RECONNECT", display.ConnectionTag);
        Assert.True(display.NeedsReconnect);
        Assert.False(display.CanSync);
        Assert.Equal(StatusTone.Warning, display.Tone);
        Assert.Contains("Nothing is lost", display.SyncLine, StringComparison.Ordinal);
        Assert.True(display.CanVerify); // the archive can still be checked
    }

    [Fact]
    public void A_disconnected_account_is_an_archive_that_can_still_be_verified()
    {
        var display = Present(Account(AccountConnection.ArchiveOnly, synced: Now.AddDays(-1)), Stats());

        Assert.Equal("ARCHIVE ONLY", display.ConnectionTag);
        Assert.True(display.IsArchiveOnly);
        Assert.True(display.NeedsReconnect);
        Assert.False(display.CanSync);
        Assert.True(display.CanVerify);
    }

    // --- Live activity -------------------------------------------------------------------------------------------------

    [Fact]
    public void Downloading_shows_counts_size_and_time_left()
    {
        var progress = new SyncProgress(SyncPhase.Downloading, 1204, 3400, 82_000_000, TimeSpan.FromMinutes(12));
        var display = Present(Account(), Stats(0), new AccountActivity(ActivityKind.Syncing, Sync: progress));

        Assert.True(display.IsBusy);
        Assert.True(display.ShowProgress);
        Assert.False(display.CanSync);
        Assert.True(display.CanStop);
        Assert.False(display.ProgressIsIndeterminate);
        Assert.Equal(35.4, display.ProgressPercent, 1);
        Assert.Contains("1,204 of 3,400", display.ProgressText.Replace(' ', ','), StringComparison.Ordinal);
        Assert.Contains("about 12 min left", display.ProgressText, StringComparison.Ordinal);
        Assert.Equal(StatusTone.Running, display.Tone);
        Assert.Equal("", display.Summary); // nothing stored yet: no stale "nothing downloaded" next to a running download
        Assert.Equal(display.ProgressText, display.SyncLine);
    }

    [Theory]
    [InlineData(SyncPhase.Starting, "Starting")]
    [InlineData(SyncPhase.Labels, "Reading labels and settings")]
    [InlineData(SyncPhase.Listing, "Asking Gmail what it has")]
    [InlineData(SyncPhase.Reconciling, "Checking labels")]
    [InlineData(SyncPhase.Analytics, "Updating analytics")]
    public void Every_phase_has_a_plain_description_and_an_indeterminate_bar_when_there_is_no_total(SyncPhase phase, string expected)
    {
        var display = Present(Account(), Stats(10), new AccountActivity(ActivityKind.Syncing, Sync: new SyncProgress(phase, 0, 0, 0, null)));

        Assert.Contains(expected, display.ProgressText, StringComparison.Ordinal);
        Assert.True(display.ProgressIsIndeterminate);
    }

    [Fact]
    public void A_queued_account_says_it_is_waiting()
    {
        var display = Present(Account(), Stats(10), new AccountActivity(ActivityKind.Queued));

        Assert.Contains("Waiting", display.ProgressText, StringComparison.Ordinal);
        Assert.True(display.IsBusy);
    }

    [Fact]
    public void Verifying_shows_file_counts()
    {
        var verifying = new AccountActivity(ActivityKind.Verifying, Verification: new VerificationProgress(VerificationPhase.CheckingFiles, 250, 1000));
        var display = Present(Account(), Stats(1000), verifying);

        Assert.Equal(25, display.ProgressPercent);
        Assert.Contains("250 of 1,000", display.ProgressText.Replace(' ', ','), StringComparison.Ordinal);
        Assert.False(display.CanVerify);
        Assert.False(display.CanSync);
    }
}
