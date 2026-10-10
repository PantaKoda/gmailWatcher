using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using GmailWatcher.Archive.Ingest;
using GmailWatcher.Archive.Storage;
using GmailWatcher.Core.Accounts;
using GmailWatcher.Core.Configuration;
using GmailWatcher.Core.Mail;
using GmailWatcher.Core.Sync;
using Microsoft.Extensions.Logging;

namespace GmailWatcher.Archive.Sync;

/// <summary>Brings the derived analytics database up to date after a sync. Implemented by the DuckDB mirror.</summary>
public interface IAnalyticsSink
{
    void Update(int accountId, CancellationToken cancellationToken);

    /// <summary>Drops everything of an account (its archive was deleted).</summary>
    void Remove(int accountId);
}

/// <summary>A sync that cannot continue, with the outcome to report.</summary>
public sealed class SyncAbortedException(SyncOutcome outcome, string message) : Exception(message)
{
    public SyncOutcome Outcome { get; } = outcome;
}

/// <summary>
/// Downloads an account's mail into the archive.
/// <list type="bullet">
/// <item><b>First sync / history expired:</b> list every message ID in Gmail (Spam and Trash included), download the ones the archive lacks.</item>
/// <item><b>Later syncs:</b> read only what changed since the last sync (Gmail's history), falling back to the full pass when Gmail no longer has that history.</item>
/// <item><b>Never deletes anything.</b> A message Gmail no longer has is only marked, so emptying Gmail cannot empty the archive.</item>
/// <item><b>Resumable:</b> every batch is committed as it arrives; stopping at any moment loses at most the batch in flight, and the next sync continues by diffing.</item>
/// </list>
/// </summary>
public sealed class SyncEngine
{
    private const int BatchSize = 50;
    private const int MaxFileFailures = 25;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<int, byte> _running = new();
    private readonly AccountStore _accounts;
    private readonly MessageStore _messages;
    private readonly MessageIngestor _ingestor;
    private readonly ArchiveLayout _layout;
    private readonly SyncOptions _options;
    private readonly ILogger<SyncEngine> _logger;
    private readonly IAnalyticsSink? _analytics;
    private readonly TextRefresher? _textRefresher;

    public SyncEngine(
        AccountStore accounts,
        MessageStore messages,
        MessageIngestor ingestor,
        ArchiveLayout layout,
        SyncOptions options,
        ILogger<SyncEngine> logger,
        IAnalyticsSink? analytics = null,
        TextRefresher? textRefresher = null)
    {
        _accounts = accounts;
        _messages = messages;
        _ingestor = ingestor;
        _layout = layout;
        _options = options;
        _logger = logger;
        _analytics = analytics;
        _textRefresher = textRefresher;
    }

    public bool IsRunning(int accountId) => _running.ContainsKey(accountId);

    public async Task<SyncResult> RunAsync(AccountRecord account, IMailbox mailbox, IProgress<SyncProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(mailbox);
        if (!_running.TryAdd(account.Id, 0))
        {
            throw new InvalidOperationException($"{account.Email} is already being synced.");
        }

        try
        {
            return await new Run(this, account, mailbox, progress, cancellationToken).ExecuteAsync().ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(account.Id, out _);
        }
    }

    private sealed class Run(SyncEngine engine, AccountRecord account, IMailbox mailbox, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Stopwatch _downloadClock = new();
        private long _lastReport = long.MinValue;
        private int _added;
        private int _labelChanges;
        private int _markedDeleted;
        private int _vanished;
        private int _failed;
        private long _bytes;
        private SyncProgress _current = SyncProgress.Starting;

        public async Task<SyncResult> ExecuteAsync()
        {
            var wasInitial = account.InitialSyncCompletedAt is null;
            var outcome = SyncOutcome.Completed;
            string? error = null;
            string? nextHistory = null;
            var fullPass = false;
            var usedHistory = false;
            long? remoteTotal = null;

            engine._accounts.SyncStarted(account.Id);
            Report(SyncProgress.Starting, force: true);
            try
            {
                // Local work that needs no Gmail: bring the text of earlier downloads up to date once (a no-op afterwards).
                if (engine._textRefresher is { } refresher)
                {
                    await Task.Run(
                        () => refresher.RefreshIfNeeded(account, (done, total) => Report(new SyncProgress(SyncPhase.Indexing, done, total, 0, null), force: done >= total), ct), ct)
                        .ConfigureAwait(false);
                }

                var profile = await mailbox.GetProfileAsync(ct).ConfigureAwait(false);
                remoteTotal = profile.MessagesTotal;
                if (!string.Equals(profile.EmailAddress, account.Email, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SyncAbortedException(SyncOutcome.Failed,
                        $"This sign-in is for {profile.EmailAddress}, but this archive belongs to {account.Email}. Nothing was changed. Reconnect with the right Google account.");
                }

                Report(new SyncProgress(SyncPhase.Labels, 0, 0, 0, null), force: true);
                var labels = await mailbox.GetLabelsAsync(ct).ConfigureAwait(false);
                engine._accounts.UpsertLabels(account.Id, labels);
                await SaveSettingsAsync().ConfigureAwait(false);

                var historyId = engine._accounts.GetHistoryId(account.Id);
                if (!wasInitial && historyId is not null)
                {
                    try
                    {
                        nextHistory = await IncrementalAsync(historyId).ConfigureAwait(false);
                        usedHistory = true;
                    }
                    catch (HistoryExpiredException)
                    {
                        engine._logger.LogInformation("History of {Account} expired; doing a full pass", account.Email);
                    }
                }

                if (!usedHistory)
                {
                    nextHistory = await FullPassAsync(profile, labels).ConfigureAwait(false);
                    fullPass = true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = SyncOutcome.Cancelled;
            }
            catch (ReconnectRequiredException ex)
            {
                (outcome, error) = (SyncOutcome.ReconnectRequired, ex.Message);
                engine._accounts.SetConnection(account.Id, AccountConnection.ReconnectRequired, ex.Message);
            }
            catch (QuotaExhaustedException ex)
            {
                (outcome, error) = (SyncOutcome.QuotaExhausted, ex.Message);
            }
            catch (MailboxUnavailableException ex)
            {
                (outcome, error) = (SyncOutcome.Failed, ex.Message);
            }
            catch (SyncAbortedException ex)
            {
                (outcome, error) = (ex.Outcome, ex.Message);
            }
            catch (Exception ex)
            {
                engine._logger.LogError(ex, "Sync of {Account} failed", account.Email);
                (outcome, error) = (SyncOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}");
            }

            engine._accounts.SyncFinished(account.Id, outcome, error,
                historyId: outcome == SyncOutcome.Completed ? nextHistory : null,
                initialComplete: outcome == SyncOutcome.Completed && fullPass,
                remoteTotal);

            // Whatever was archived, even by a stopped sync, belongs in the analytics too.
            if (engine._analytics is { } sink)
            {
                try
                {
                    Report(new SyncProgress(SyncPhase.Analytics, 0, 0, _bytes, null), force: true);
                    sink.Update(account.Id, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Derived data: it is rebuilt from the archive the next time it is needed.
                    engine._logger.LogWarning(ex, "Updating the analytics database failed");
                }
            }

            return new SyncResult(outcome, _added, _labelChanges, _markedDeleted, _vanished, _failed, _bytes, _clock.Elapsed, error, wasInitial, usedHistory);
        }

        private async Task SaveSettingsAsync()
        {
            try
            {
                engine._accounts.SaveSettings(account.Id, await mailbox.GetSettingsAsync(ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is MailboxUnavailableException)
            {
                // Mailbox settings are a bonus. Mail must still be downloaded when only they failed.
                engine._logger.LogWarning(ex, "Saving mailbox settings of {Account} failed", account.Email);
            }
        }

        private async Task<string> IncrementalAsync(string historyId)
        {
            Report(new SyncProgress(SyncPhase.Listing, 0, 0, 0, null), force: true);
            var history = await mailbox.GetHistoryAsync(historyId, ct).ConfigureAwait(false);
            var archived = engine._messages.GetIds(account.Id);

            var deleted = history.Changes.Where(c => c.Kind == HistoryChangeKind.MessageDeleted).Select(c => c.MessageId).ToHashSet(StringComparer.Ordinal);
            var added = history.Changes
                .Where(c => c.Kind == HistoryChangeKind.MessageAdded)
                .Select(c => c.MessageId)
                .Distinct(StringComparer.Ordinal)
                .Where(id => !archived.Contains(id))
                .ToList();
            var goneBeforeDownload = added.Count(deleted.Contains);
            added = added.Where(id => !deleted.Contains(id)).ToList();
            _vanished += goneBeforeDownload;

            _markedDeleted += engine._messages.MarkRemoteDeleted(account.Id, deleted);
            _labelChanges += engine._messages.ApplyLabelChanges(account.Id, history.Changes);

            await DownloadAsync(added).ConfigureAwait(false);
            return history.LatestHistoryId;
        }

        private async Task<string> FullPassAsync(MailboxProfile profile, IReadOnlyList<RemoteLabel> labels)
        {
            var archived = engine._messages.GetIds(account.Id);
            var remote = new HashSet<string>(StringComparer.Ordinal);
            var missing = new List<string>(); // newest first, as Gmail lists them: recent mail is archived first

            Report(new SyncProgress(SyncPhase.Listing, 0, 0, 0, null), force: true);
            await foreach (var id in mailbox.ListMessageIdsAsync(null, ct).ConfigureAwait(false))
            {
                if (remote.Add(id) && !archived.Contains(id))
                {
                    missing.Add(id);
                }

                if (remote.Count % 500 == 0)
                {
                    Report(new SyncProgress(SyncPhase.Listing, remote.Count, 0, 0, null, $"{missing.Count:N0} to download"));
                }
            }

            await DownloadAsync(missing).ConfigureAwait(false);

            // The listing finished, so it is Gmail's complete answer about what exists now.
            _markedDeleted += engine._messages.MarkRemoteDeleted(account.Id, archived.Where(id => !remote.Contains(id)));
            engine._messages.UnmarkRemoteDeleted(account.Id, remote);

            if (archived.Count > 0)
            {
                await ReconcileLabelsAsync(labels, archived, remote).ConfigureAwait(false);
            }

            return profile.HistoryId;
        }

        private async Task ReconcileLabelsAsync(IReadOnlyList<RemoteLabel> labels, ISet<string> archived, ISet<string> allRemote)
        {
            for (var i = 0; i < labels.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Report(new SyncProgress(SyncPhase.Reconciling, i, labels.Count, _bytes, null, labels[i].Name));
                var withLabel = new HashSet<string>(StringComparer.Ordinal);
                await foreach (var id in mailbox.ListMessageIdsAsync(labels[i].Id, ct).ConfigureAwait(false))
                {
                    withLabel.Add(id);
                }

                _labelChanges += engine._messages.ReconcileLabel(account.Id, labels[i].Id, withLabel, archived, allRemote);
            }
        }

        /// <summary>Downloads the given messages with a few workers and one database writer.</summary>
        private async Task DownloadAsync(IReadOnlyList<string> ids)
        {
            if (ids.Count == 0)
            {
                return;
            }

            long total = ids.Count;
            long done = 0;
            _downloadClock.Restart();
            Report(new SyncProgress(SyncPhase.Downloading, 0, total, 0, null), force: true);

            var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(engine._options.Workers * 2) { SingleWriter = true });
            var results = Channel.CreateBounded<IngestedMessage?>(new BoundedChannelOptions(engine._options.Workers * 4) { SingleReader = true });
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = stop.Token;
            ExceptionDispatchInfo? fatal = null;
            var folder = account.Folder;
            var floor = (long)engine._options.MinFreeDiskMegabytes * 1024 * 1024;
            var sinceSpaceCheck = 0;

            void Fail(Exception ex)
            {
                if (ex is OperationCanceledException && token.IsCancellationRequested)
                {
                    return;
                }

                Interlocked.CompareExchange(ref fatal, ExceptionDispatchInfo.Capture(ex), null);
                stop.Cancel();
            }

            var producer = Task.Run(async () =>
            {
                try
                {
                    foreach (var id in ids)
                    {
                        await queue.Writer.WriteAsync(id, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }
                finally
                {
                    queue.Writer.TryComplete();
                }
            }, CancellationToken.None);

            var workers = Enumerable.Range(0, engine._options.Workers).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await foreach (var id in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
                    {
                        var message = await mailbox.GetMessageAsync(id, token).ConfigureAwait(false);
                        if (message is null)
                        {
                            Interlocked.Increment(ref _vanished);
                            await results.Writer.WriteAsync(null, token).ConfigureAwait(false);
                            continue;
                        }

                        if (Interlocked.Increment(ref sinceSpaceCheck) % 25 == 1)
                        {
                            EnsureDiskSpace(floor);
                        }

                        try
                        {
                            var ingested = engine._ingestor.Ingest(folder, message, token);
                            Interlocked.Add(ref _bytes, message.Raw.LongLength);
                            await results.Writer.WriteAsync(ingested, token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !FileStore.IsDiskFull(ex))
                        {
                            // One unwritable file must not stop the rest, but a broken drive must not run forever.
                            engine._logger.LogWarning(ex, "Message {MessageId} could not be written", id);
                            if (Interlocked.Increment(ref _failed) > MaxFileFailures)
                            {
                                throw new SyncAbortedException(SyncOutcome.Failed, $"Too many files could not be written (last: {ex.Message}). Check the archive folder and drive.");
                            }

                            await results.Writer.WriteAsync(null, token).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (FileStore.IsDiskFull(ex))
                {
                    Fail(new SyncAbortedException(SyncOutcome.DiskFull, "The drive that holds the archive is full."));
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }
            }, CancellationToken.None)).ToArray();

            var close = Task.WhenAll(workers).ContinueWith(_ => results.Writer.TryComplete(), TaskScheduler.Default);

            // The writer ignores cancellation on purpose: messages already downloaded and written to disk are committed
            // even when the sync was stopped. It ends when the workers are done.
            var writer = Task.Run(async () =>
            {
                var batch = new List<IngestedMessage>(BatchSize);
                try
                {
                    while (await results.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        var ticks = 0;
                        while (batch.Count < BatchSize && results.Reader.TryRead(out var item))
                        {
                            ticks++;
                            if (item is not null)
                            {
                                batch.Add(item);
                            }
                        }

                        if (batch.Count > 0)
                        {
                            engine._messages.InsertBatch(account.Id, batch);
                            Interlocked.Add(ref _added, batch.Count);
                            batch.Clear();
                        }

                        done += ticks;
                        Report(new SyncProgress(SyncPhase.Downloading, done, total, Interlocked.Read(ref _bytes), Estimate(done, total)));
                    }
                }
                catch (Exception ex)
                {
                    Fail(ex);
                    results.Writer.TryComplete();
                }
            }, CancellationToken.None);

            await Task.WhenAll(producer, close, writer).ConfigureAwait(false);
            await Task.WhenAll(workers).ConfigureAwait(false);

            Report(new SyncProgress(SyncPhase.Downloading, done, total, _bytes, null), force: true);
            fatal?.Throw();
            ct.ThrowIfCancellationRequested();
        }

        private void EnsureDiskSpace(long floorBytes)
        {
            try
            {
                var root = Path.GetPathRoot(engine._layout.Root);
                if (root is not null && new DriveInfo(root).AvailableFreeSpace < floorBytes)
                {
                    throw new SyncAbortedException(SyncOutcome.DiskFull,
                        $"Less than {floorBytes / (1024 * 1024):N0} MB is free on the drive that holds the archive. Free some space and sync again; nothing was lost.");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A network path or an unusual volume: no free-space reading, so rely on the write errors instead.
            }
        }

        private TimeSpan? Estimate(long done, long total)
        {
            var seconds = _downloadClock.Elapsed.TotalSeconds;
            if (done < 10 || seconds < 5)
            {
                return null;
            }

            var rate = done / seconds;
            return TimeSpan.FromSeconds(Math.Max(0, total - done) / rate);
        }

        private void Report(SyncProgress value, bool force = false)
        {
            _current = value;
            var now = Stopwatch.GetTimestamp();
            if (!force && _lastReport != long.MinValue && Stopwatch.GetElapsedTime(_lastReport, now) < ProgressInterval)
            {
                return;
            }

            _lastReport = now;
            progress?.Report(_current);
        }
    }
}
