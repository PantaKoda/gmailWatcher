using System.Collections.Concurrent;
using GmailWatcher.Google.Gmail;
using GmailWatcher.Google.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace GmailWatcher.Google.Tests.Gmail;

public sealed class QuotaGovernorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestClock _time = new();

    /// <summary>600 units a minute: 10 units a second, a burst of 100 units (five message downloads).</summary>
    private QuotaGovernor SlowGovernor() => new(600, _time);

    private static async Task DrainAsync(QuotaGovernor governor, int units)
    {
        for (var taken = 0; taken < units; taken += 20)
        {
            await governor.AcquireAsync(20, Ct);
        }
    }

    // ---- burst and pacing ----

    [Fact]
    public async Task AFullBucketServesAOneSixthMinuteBurstAtOnce()
    {
        var governor = SlowGovernor();

        for (var i = 0; i < 5; i++)
        {
            var acquire = governor.AcquireAsync(20, Ct);
            Assert.True(acquire.IsCompletedSuccessfully);
            await acquire;
        }

        Assert.False(governor.AcquireAsync(20, Ct).IsCompleted);
    }

    [Fact]
    public void TheDefaultBudgetAllowsAboutFortyOneMessageDownloadsInABurst()
    {
        var governor = new QuotaGovernor(5000, _time);

        for (var i = 0; i < 41; i++)
        {
            Assert.True(governor.AcquireAsync(20, Ct).IsCompletedSuccessfully, $"download {i + 1}");
        }

        Assert.False(governor.AcquireAsync(20, Ct).IsCompleted);
    }

    [Fact]
    public async Task TheBurstIsNeverSmallerThanTheDearestSingleCall()
    {
        var governor = new QuotaGovernor(60, _time);

        Assert.True(governor.AcquireAsync(20, Ct).IsCompletedSuccessfully);

        var second = governor.AcquireAsync(20, Ct).AsTask();
        _time.Advance(TimeSpan.FromMilliseconds(19_999));
        Assert.False(second.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await second.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task AnEmptyBucketRefillsAtTheConfiguredRate()
    {
        var governor = SlowGovernor();
        await DrainAsync(governor, 100);

        var next = governor.AcquireAsync(20, Ct).AsTask();
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(next.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));

        await next.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task IdleTimeRefillsOnlyUpToTheBurstCapacity()
    {
        var governor = SlowGovernor();
        await DrainAsync(governor, 100);

        _time.Advance(TimeSpan.FromHours(1));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(governor.AcquireAsync(20, Ct).IsCompletedSuccessfully);
        }

        Assert.False(governor.AcquireAsync(20, Ct).IsCompleted);
    }

    [Fact]
    public async Task ACallDearerThanTheWholeBucketGoesThroughOnceAndIsPaidBackLater()
    {
        var governor = SlowGovernor();

        Assert.True(governor.AcquireAsync(150, Ct).IsCompletedSuccessfully);

        // 50 units in debt, so 20 more units need 70 units of refill: seven seconds.
        var next = governor.AcquireAsync(20, Ct).AsTask();
        _time.Advance(TimeSpan.FromMilliseconds(6999));
        Assert.False(next.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await next.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task NeverGrantsMoreThanTheBudgetAllowsEvenWithManyConcurrentCallers()
    {
        var governor = new QuotaGovernor(6000, _time);
        var start = _time.GetUtcNow();
        var grants = new ConcurrentQueue<DateTimeOffset>();

        var callers = Enumerable.Range(0, 400).Select(_ => Task.Run(async () =>
        {
            await governor.AcquireAsync(20, Ct);
            grants.Enqueue(_time.GetUtcNow());
        }, Ct)).ToArray();
        await _time.PumpAsync(Task.WhenAll(callers));

        // 100 units a second plus a 1,000 unit burst. The times were read after each grant, so they can only be later than
        // the governor's own reading; the bound therefore holds for a correct governor however the threads interleave.
        var times = grants.OrderBy(at => at).ToArray();
        Assert.Equal(400, times.Length);
        for (var k = 1; k <= times.Length; k++)
        {
            var allowed = 1000 + 100 * (times[k - 1] - start).TotalSeconds;
            Assert.True(20 * k <= allowed + 1e-6, $"grant {k} at {(times[k - 1] - start).TotalSeconds:F2}s exceeds the budget");
        }

        Assert.True((times[^1] - start).TotalSeconds >= 69.99, "8,000 units cannot be served in under 70 seconds");
    }

    // ---- fairness and cancellation ----

    // Ordering is checked against the clock, not against the order thread-pool continuations happen to run in. When waiter k
    // hands the turn on, waiter k+1 runs inline and may finish a moment before k's own task is marked complete, so the
    // completion order of the tasks can differ from the order the budget was granted in. What cannot differ: a waiter is never
    // served before the budget for everyone ahead of it, and itself, has accrued.
    [Fact]
    public async Task WaitersAreServedInArrivalOrderEachOnlyOnceTheBudgetForEveryoneAheadHasAccrued()
    {
        var governor = SlowGovernor(); // 10 units a second: each 20-unit call needs two seconds of budget
        await DrainAsync(governor, 100);
        var start = _time.GetUtcNow();

        var waiters = Enumerable.Range(0, 10).Select(_ => governor.AcquireAsync(20, Ct).AsTask()).ToArray();

        for (var k = 0; k < waiters.Length; k++)
        {
            await _time.PumpAsync(waiters[k]);
            var earliest = TimeSpan.FromSeconds(2 * (k + 1)) - TimeSpan.FromMilliseconds(5);
            Assert.True(_time.GetUtcNow() - start >= earliest, $"waiter {k} was served after {_time.GetUtcNow() - start}, before the budget for the {k + 1} calls up to it existed");
        }
    }

    [Fact]
    public async Task ACheapCallDoesNotOvertakeAnExpensiveOneThatArrivedFirst()
    {
        var governor = SlowGovernor();
        await DrainAsync(governor, 100);

        var expensive = governor.AcquireAsync(20, Ct).AsTask(); // two seconds of budget
        var cheap = governor.AcquireAsync(1, Ct).AsTask();      // a tenth of a second, if it were allowed to jump the queue

        _time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 20; i++)
        {
            await Task.Yield();
        }

        Assert.False(expensive.IsCompleted);
        Assert.False(cheap.IsCompleted, "the cheap call was served ahead of the expensive one that arrived first");
        await _time.PumpAsync(Task.WhenAll(expensive, cheap));
    }

    [Fact]
    public async Task ACancelledWaiterGetsACancellationAndTakesNoBudget()
    {
        var governor = SlowGovernor();
        await DrainAsync(governor, 100);
        using var cts = new CancellationTokenSource();

        var first = governor.AcquireAsync(20, Ct).AsTask();
        var cancelled = governor.AcquireAsync(20, cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        _time.Advance(TimeSpan.FromSeconds(2));
        await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var third = governor.AcquireAsync(20, Ct).AsTask();
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(third.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await third.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task TheWaiterAtTheFrontCanCancelToo()
    {
        var governor = SlowGovernor();
        await DrainAsync(governor, 100);
        using var cts = new CancellationTokenSource();

        var front = governor.AcquireAsync(20, cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => front);

        var next = governor.AcquireAsync(20, Ct).AsTask();
        _time.Advance(TimeSpan.FromSeconds(2));
        await next.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task AnAlreadyCancelledCallerIsRefusedWithoutTakingBudget()
    {
        var governor = SlowGovernor();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await governor.AcquireAsync(20, cts.Token));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(governor.AcquireAsync(20, Ct).IsCompletedSuccessfully);
        }
    }

    [Fact]
    public async Task RejectsNonsenseArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuotaGovernor(0, _time));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuotaGovernor(-5, _time));
        var governor = SlowGovernor();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await governor.AcquireAsync(0, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await governor.AcquireAsync(-1, Ct));
    }

    // ---- rate limits ----

    [Fact]
    public void ARateLimitPausesEverybodyForFiveSecondsAtFirst()
    {
        var governor = new QuotaGovernor(6000, _time);

        governor.ReportRateLimited();

        Assert.Equal(TimeSpan.FromSeconds(5), governor.PauseRemaining);
    }

    [Fact]
    public async Task NoOneIsServedDuringThePauseAndTheRefillRestartsAtTheLowerRate()
    {
        var governor = new QuotaGovernor(6000, _time);
        governor.ReportRateLimited();

        var waiting = governor.AcquireAsync(20, Ct).AsTask();
        _time.Advance(TimeSpan.FromMilliseconds(5200));
        Assert.False(waiting.IsCompleted);

        // After the pause the bucket starts empty and refills at 70 units a second: 20 units take 286 ms.
        _time.Advance(TimeSpan.FromMilliseconds(85));
        Assert.False(waiting.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task AWaiterAlreadyWaitingWhenTheLimitHitsIsPausedToo()
    {
        var governor = SlowGovernor();
        await DrainAsync(governor, 100);
        var waiting = governor.AcquireAsync(20, Ct).AsTask();
        _time.Advance(TimeSpan.FromSeconds(1));

        governor.ReportRateLimited(TimeSpan.FromSeconds(30));
        var reported = _time.GetUtcNow();
        var granted = DateTimeOffset.MinValue;
        var observed = Task.Run(async () =>
        {
            await waiting;
            granted = _time.GetUtcNow();
        }, Ct);
        await _time.PumpAsync(observed);

        Assert.True(granted - reported >= TimeSpan.FromSeconds(30), "the waiter went ahead during the pause");
    }

    [Fact]
    public void RepeatedRateLimitsBackOffExponentiallyUpToFiveMinutes()
    {
        var governor = new QuotaGovernor(6000, _time);
        var seen = new List<double>();

        for (var i = 0; i < 9; i++)
        {
            governor.ReportRateLimited();
            seen.Add(governor.PauseRemaining.TotalSeconds);
            _time.Advance(governor.PauseRemaining);
        }

        Assert.Equal([5d, 10, 20, 40, 80, 160, 300, 300, 300], seen);
    }

    [Fact]
    public void ASuccessAfterThePauseStartsTheBackoffOver()
    {
        var governor = new QuotaGovernor(6000, _time);
        governor.ReportRateLimited();
        _time.Advance(TimeSpan.FromSeconds(5));
        governor.ReportRateLimited();
        Assert.Equal(TimeSpan.FromSeconds(10), governor.PauseRemaining);
        _time.Advance(TimeSpan.FromSeconds(10));

        governor.ReportSuccess();
        governor.ReportRateLimited();

        Assert.Equal(TimeSpan.FromSeconds(5), governor.PauseRemaining);
    }

    [Fact]
    public void ReportsFromRequestsThatWereAlreadyInFlightCountAsOneIncident()
    {
        var governor = new QuotaGovernor(6000, _time);

        for (var i = 0; i < 6; i++)
        {
            governor.ReportRateLimited();
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(0.7, governor.CurrentRateMultiplier, 6);
        Assert.Equal(TimeSpan.FromMilliseconds(4400), governor.PauseRemaining);
    }

    [Fact]
    public void ASuccessThatWasSentBeforeTheLimitDoesNotEndThePause()
    {
        var governor = new QuotaGovernor(6000, _time);
        governor.ReportRateLimited();
        governor.ReportRateLimited();
        _time.Advance(TimeSpan.FromSeconds(5));
        governor.ReportRateLimited();
        Assert.Equal(TimeSpan.FromSeconds(10), governor.PauseRemaining);

        governor.ReportSuccess();
        _time.Advance(TimeSpan.FromSeconds(10));
        governor.ReportRateLimited();

        Assert.Equal(TimeSpan.FromSeconds(20), governor.PauseRemaining);
    }

    [Fact]
    public void ARetryAfterLongerThanTheBackoffWins()
    {
        var governor = new QuotaGovernor(6000, _time);

        governor.ReportRateLimited(TimeSpan.FromSeconds(42));

        Assert.Equal(TimeSpan.FromSeconds(42), governor.PauseRemaining);
    }

    [Fact]
    public void ARetryAfterShorterThanTheBackoffLoses()
    {
        var governor = new QuotaGovernor(6000, _time);

        governor.ReportRateLimited(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(5), governor.PauseRemaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public void ANonsensicalRetryAfterIsIgnored(int seconds)
    {
        var governor = new QuotaGovernor(6000, _time);

        governor.ReportRateLimited(TimeSpan.FromSeconds(seconds));

        Assert.Equal(TimeSpan.FromSeconds(5), governor.PauseRemaining);
    }

    [Fact]
    public void AnAbsurdRetryAfterIsCappedAtFifteenMinutes()
    {
        var governor = new QuotaGovernor(6000, _time);

        governor.ReportRateLimited(TimeSpan.FromHours(6));

        Assert.Equal(TimeSpan.FromMinutes(15), governor.PauseRemaining);
    }

    [Fact]
    public void ALaterReportOfTheSameIncidentCanExtendThePauseButNotShortenIt()
    {
        var governor = new QuotaGovernor(6000, _time);
        governor.ReportRateLimited(TimeSpan.FromSeconds(30));

        governor.ReportRateLimited(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(30), governor.PauseRemaining);

        governor.ReportRateLimited(TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(60), governor.PauseRemaining);
        Assert.Equal(0.7, governor.CurrentRateMultiplier, 6);
    }

    [Fact]
    public void EachNewIncidentSlowsTheRateDownTo70PercentWithAFloorOfTwentyPercent()
    {
        var governor = new QuotaGovernor(6000, _time);
        var seen = new List<double>();

        for (var i = 0; i < 6; i++)
        {
            governor.ReportRateLimited();
            seen.Add(governor.CurrentRateMultiplier);
            _time.Advance(governor.PauseRemaining);
        }

        Assert.Equal(1.0, new QuotaGovernor(6000, _time).CurrentRateMultiplier);
        Assert.Equal([0.7, 0.49, 0.343, 0.2401, 0.2, 0.2], seen.Select(value => Math.Round(value, 4)));
    }

    [Fact]
    public async Task ASlowedDownGovernorRefillsMoreSlowly()
    {
        var governor = SlowGovernor();
        governor.ReportRateLimited();
        _time.Advance(TimeSpan.FromSeconds(5));
        governor.ReportRateLimited();
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0.49, governor.CurrentRateMultiplier, 6);

        // 20 units at 10 * 0.49 = 4.9 units a second take 4.082 seconds, counted from the end of the pause.
        var next = governor.AcquireAsync(20, Ct).AsTask();
        _time.Advance(TimeSpan.FromMilliseconds(4081));
        Assert.False(next.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await next.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    // ---- recovery ----

    [Fact]
    public void SuccessfulCallsWinBackFivePercentOfTheFullRateEveryMinute()
    {
        var governor = new QuotaGovernor(6000, _time);
        governor.ReportRateLimited();
        _time.Advance(TimeSpan.FromSeconds(5));
        var seen = new List<double>();

        for (var i = 0; i < 8; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            governor.ReportSuccess();
            seen.Add(Math.Round(governor.CurrentRateMultiplier, 4));
        }

        Assert.Equal([0.75, 0.8, 0.85, 0.9, 0.95, 1.0, 1.0, 1.0], seen);
    }

    [Fact]
    public void RecoveryFollowsTheCallsNotTheWallClock()
    {
        var governor = new QuotaGovernor(6000, _time);
        governor.ReportRateLimited();
        _time.Advance(TimeSpan.FromMinutes(30));

        governor.ReportSuccess();

        // Half an hour without calls earns one minute of credit, not thirty.
        Assert.Equal(0.75, governor.CurrentRateMultiplier, 6);
    }

    [Fact]
    public void FrequentSuccessesEarnTheRecoveryInSmallSteps()
    {
        var governor = new QuotaGovernor(6000, _time);
        governor.ReportRateLimited();
        _time.Advance(TimeSpan.FromSeconds(5));

        for (var i = 0; i < 120; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(500));
            governor.ReportSuccess();
        }

        Assert.Equal(0.75, governor.CurrentRateMultiplier, 6);
    }

    [Fact]
    public void ARateLimitInterruptsTheRecovery()
    {
        var governor = new QuotaGovernor(6000, _time);
        governor.ReportRateLimited();
        _time.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromMinutes(2));
        governor.ReportSuccess();
        Assert.Equal(0.75, governor.CurrentRateMultiplier, 6);

        governor.ReportRateLimited();

        Assert.Equal(0.525, governor.CurrentRateMultiplier, 6);
    }

    [Fact]
    public void SuccessAtFullSpeedChangesNothing()
    {
        var governor = new QuotaGovernor(6000, _time);

        governor.ReportSuccess();
        _time.Advance(TimeSpan.FromMinutes(5));
        governor.ReportSuccess();

        Assert.Equal(1.0, governor.CurrentRateMultiplier);
        Assert.Equal(TimeSpan.Zero, governor.PauseRemaining);
    }
}
