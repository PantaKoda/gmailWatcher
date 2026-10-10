using System.Collections.Concurrent;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Sync;
using GmailWatcher.Core.Verification;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Desktop.Services;

public enum ActivityKind
{
    Idle,
    /// <summary>Waiting for a free slot: only a couple of accounts download at the same time.</summary>
    Queued,
    Syncing,
    Verifying,
}

/// <summary>What an account is doing right now, and what its last job in this session produced.</summary>
public sealed record AccountActivity(
    ActivityKind Kind,
    SyncProgress? Sync = null,
    VerificationProgress? Verification = null,
    SyncResult? LastSync = null,
    VerificationReport? LastReport = null)
{
    public static AccountActivity Idle { get; } = new(ActivityKind.Idle);

    public bool IsBusy => Kind != ActivityKind.Idle;
}

/// <summary>
/// Runs syncs and verifications in the background: at most one job per account, a couple of accounts downloading at once.
/// Jobs survive the window that started them (closing details doesn't stop a sync); quitting cancels them cleanly, and
/// what was downloaded is kept.
/// </summary>
public sealed class SyncService(ArchiveHost archive, IMailboxFactory mailboxes, ILogger<SyncService> logger) : IDisposable
{
    private const int ParallelAccounts = 2;

    private readonly ConcurrentDictionary<int, Job> _jobs = new();
    private readonly ConcurrentDictionary<int, AccountActivity> _activity = new();
    private readonly SemaphoreSlim _syncSlots = new(ParallelAccounts);
    private readonly SemaphoreSlim _verifySlot = new(1);
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Raised on a background thread with the account ID whose activity changed.</summary>
    public event EventHandler<int>? ActivityChanged;

    public bool HasWork => !_jobs.IsEmpty;

    public int BusyCount => _jobs.Count;

    public AccountActivity GetActivity(int accountId) => _activity.GetValueOrDefault(accountId, AccountActivity.Idle);

    public bool IsBusy(int accountId) => _jobs.ContainsKey(accountId);

    /// <summary>Starts syncing an account. False when it is already busy, or can't sync (not connected).</summary>
    public bool StartSync(AccountRecord account)
    {
        if (!archive.IsOpen || account.Connection != AccountConnection.Connected)
        {
            return false;
        }

        return Start(account.Id, ActivityKind.Queued, async job =>
        {
            await _syncSlots.WaitAsync(job.Token).ConfigureAwait(false);
            try
            {
                Set(account.Id, current => current with { Kind = ActivityKind.Syncing, Sync = SyncProgress.Starting });
                var result = await RunSyncAsync(account.Id, job.Token).ConfigureAwait(false);
                Set(account.Id, current => current with { Kind = ActivityKind.Idle, Sync = null, LastSync = result });
            }
            finally
            {
                _syncSlots.Release();
            }
        });
    }

    /// <summary>Verifies an account: Gmail and files when it is connected, files only otherwise.</summary>
    public bool StartVerify(AccountRecord account)
    {
        if (!archive.IsOpen)
        {
            return false;
        }

        return Start(account.Id, ActivityKind.Queued, async job =>
        {
            await _verifySlot.WaitAsync(job.Token).ConfigureAwait(false);
            try
            {
                Set(account.Id, current => current with { Kind = ActivityKind.Verifying, Verification = new VerificationProgress(VerificationPhase.Starting, 0, 0) });
                var report = await RunVerifyAsync(account.Id, job.Token).ConfigureAwait(false);
                Set(account.Id, current => current with { Kind = ActivityKind.Idle, Verification = null, LastReport = report });
            }
            finally
            {
                _verifySlot.Release();
            }
        });
    }

    /// <summary>Syncs every connected account that isn't busy. Returns how many were started.</summary>
    public int StartSyncAll() => archive.IsOpen ? archive.Runtime.Accounts.List().Count(StartSync) : 0;

    public void Cancel(int accountId)
    {
        if (_jobs.TryGetValue(accountId, out var job))
        {
            job.Cancel();
        }
    }

    /// <summary>Stops everything and waits (a few seconds at most) for the jobs to commit what they already downloaded.</summary>
    public async Task StopAllAsync(TimeSpan timeout)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        var tasks = _jobs.Values.Select(j => j.Completion).ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeout)).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private bool Start(int accountId, ActivityKind initial, Func<Job, Task> work)
    {
        var job = new Job(CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        if (!_jobs.TryAdd(accountId, job))
        {
            job.Dispose();
            return false;
        }

        Set(accountId, current => current with { Kind = initial });
        job.Completion = Task.Run(async () =>
        {
            try
            {
                await work(job).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
            {
                // Stopped by the user or by quitting; the work itself already reported what it kept.
                Set(accountId, current => current with { Kind = ActivityKind.Idle, Sync = null, Verification = null });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A background job failed");
                Set(accountId, current => current with
                {
                    Kind = ActivityKind.Idle,
                    Sync = null,
                    Verification = null,
                    LastSync = current.Kind == ActivityKind.Verifying ? current.LastSync : Failure(SyncOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}"),
                });
            }
            finally
            {
                _jobs.TryRemove(accountId, out _);
                job.Dispose();
                ActivityChanged?.Invoke(this, accountId);
            }
        });
        return true;
    }

    private async Task<SyncResult> RunSyncAsync(int accountId, CancellationToken token)
    {
        var runtime = archive.Runtime;
        var account = runtime.Accounts.Get(accountId) ?? throw new InvalidOperationException("The account was removed.");
        IMailbox mailbox;
        try
        {
            mailbox = await mailboxes.OpenAsync(account.Email, token).ConfigureAwait(false);
        }
        catch (ReconnectRequiredException ex)
        {
            runtime.Accounts.SetConnection(account.Id, AccountConnection.ReconnectRequired, ex.Message);
            return Failure(SyncOutcome.ReconnectRequired, ex.Message);
        }

        var progress = new SynchronousProgress<SyncProgress>(p => Set(accountId, current => current with { Sync = p }));
        return await runtime.Engine.RunAsync(account, mailbox, progress, token).ConfigureAwait(false);
    }

    private async Task<VerificationReport> RunVerifyAsync(int accountId, CancellationToken token)
    {
        var runtime = archive.Runtime;
        var account = runtime.Accounts.Get(accountId) ?? throw new InvalidOperationException("The account was removed.");
        IMailbox? mailbox = null;
        if (account.Connection == AccountConnection.Connected)
        {
            try
            {
                mailbox = await mailboxes.OpenAsync(account.Email, token).ConfigureAwait(false);
            }
            catch (ReconnectRequiredException ex)
            {
                // Still worth checking the files; the report will say Gmail wasn't asked.
                runtime.Accounts.SetConnection(account.Id, AccountConnection.ReconnectRequired, ex.Message);
            }
        }

        var progress = new SynchronousProgress<VerificationProgress>(p => Set(accountId, current => current with { Verification = p }));
        return await runtime.Verifier.VerifyAsync(account, mailbox, progress, token).ConfigureAwait(false);
    }

    private void Set(int accountId, Func<AccountActivity, AccountActivity> change)
    {
        _activity.AddOrUpdate(accountId, _ => change(AccountActivity.Idle), (_, current) => change(current));
        ActivityChanged?.Invoke(this, accountId);
    }

    private static SyncResult Failure(SyncOutcome outcome, string message) =>
        new(outcome, 0, 0, 0, 0, 0, 0, TimeSpan.Zero, message, WasInitial: false, UsedHistory: false);

    /// <summary>Reports on the calling thread, immediately (the framework's Progress posts to a context and can reorder reports).</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class Job(CancellationTokenSource cts) : IDisposable
    {
        public CancellationToken Token => cts.Token;

        public Task Completion { get; set; } = Task.CompletedTask;

        public void Cancel() => cts.Cancel();

        public void Dispose() => cts.Dispose();
    }
}
