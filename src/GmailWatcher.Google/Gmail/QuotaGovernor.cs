namespace GmailWatcher.Google.Gmail;

/// <summary>
/// Paces one account's Gmail calls with a token bucket measured in quota units, and backs off for everybody when Gmail
/// says it is being asked too much. One instance per account, shared by all workers, so a rate limit hit by one request
/// slows the others at once and the memory of it survives between syncs.
/// </summary>
/// <remarks>
/// Waiters are served strictly in arrival order, so a stream of cheap calls cannot starve an expensive one. All waiting
/// uses the injected <see cref="TimeProvider"/>, so tests control the clock.
/// </remarks>
public sealed class QuotaGovernor
{
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>A Retry-After beyond this is not obeyed in full: a stuck sync is worse than one more 429.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(15);

    private const double SlowDownFactor = 0.7;
    private const double MinMultiplier = 0.2;
    private const double RecoveryPerMinute = 0.05;

    private readonly TimeProvider _time;
    private readonly long _origin;
    private readonly double _capacity;
    private readonly double _baseUnitsPerSecond;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _turn = new(1, 1);
    private double _tokens;
    private TimeSpan _lastRefill;
    private TimeSpan _pausedUntil;
    private TimeSpan _recoveryMark;
    private double _multiplier = 1.0;
    private int _consecutiveRateLimits;

    public QuotaGovernor(int unitsPerMinute, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unitsPerMinute);
        _time = time;
        _origin = time.GetTimestamp();
        UnitsPerMinute = unitsPerMinute;
        _baseUnitsPerSecond = unitsPerMinute / 60.0;
        _capacity = Math.Max(unitsPerMinute / 6.0, GmailQuotaCosts.LargestSingleCost);
        _tokens = _capacity;
    }

    public int UnitsPerMinute { get; }

    /// <summary>1.0 at full speed; lowered by every rate limit (down to 0.2) and restored while calls succeed. For diagnostics.</summary>
    public double CurrentRateMultiplier
    {
        get
        {
            lock (_lock)
            {
                return _multiplier;
            }
        }
    }

    /// <summary>How much longer everybody is paused after a rate limit; zero when calls may go out. For diagnostics.</summary>
    public TimeSpan PauseRemaining
    {
        get
        {
            lock (_lock)
            {
                var remaining = _pausedUntil - Now();
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    /// <summary>Waits until <paramref name="units"/> of quota are available, then takes them.</summary>
    public async ValueTask AcquireAsync(int units, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(units);
        cancellationToken.ThrowIfCancellationRequested();

        // One waiter at a time sleeps for the budget; the rest queue here in order.
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var wait = TryTake(units);
                if (wait == TimeSpan.Zero)
                {
                    return;
                }

                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// Gmail answered "too many requests". Everybody pauses until <paramref name="retryAfter"/> (when Gmail said) or an
    /// exponentially growing backoff (5 s, 10 s, ... at most 5 min) has passed, and the refill rate drops to 70%.
    /// </summary>
    public void ReportRateLimited(TimeSpan? retryAfter = null)
    {
        lock (_lock)
        {
            var now = Now();
            Refill(now);

            var hint = retryAfter is { } given && given > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Min(given.Ticks, MaxRetryAfter.Ticks)) : TimeSpan.Zero;

            if (now < _pausedUntil)
            {
                // The same incident: other requests that were in flight when the limit hit are reporting it too.
                // They may extend the pause but do not count as another failure.
                if (now + hint > _pausedUntil)
                {
                    _pausedUntil = now + hint;
                    _lastRefill = _pausedUntil;
                }

                return;
            }

            var backoff = TimeSpan.FromTicks(Math.Min(BaseBackoff.Ticks << Math.Min(_consecutiveRateLimits, 6), MaxBackoff.Ticks));
            _consecutiveRateLimits++;

            _pausedUntil = now + (hint > backoff ? hint : backoff);
            _lastRefill = _pausedUntil;
            _recoveryMark = _pausedUntil;
            _tokens = 0;
            _multiplier = Math.Max(MinMultiplier, _multiplier * SlowDownFactor);
        }
    }

    /// <summary>A call went through. Slowly earns back the rate lost to earlier limits: 5% of full speed per minute.</summary>
    public void ReportSuccess()
    {
        lock (_lock)
        {
            var now = Now();
            if (now < _pausedUntil)
            {
                // Sent before the limit hit; says nothing about the pace after it.
                return;
            }

            _consecutiveRateLimits = 0;
            if (_multiplier >= 1.0)
            {
                return;
            }

            Refill(now);
            var credited = now - _recoveryMark;
            if (credited > TimeSpan.FromMinutes(1))
            {
                credited = TimeSpan.FromMinutes(1);
            }

            if (credited > TimeSpan.Zero)
            {
                _multiplier = Math.Min(1.0, _multiplier + RecoveryPerMinute * credited.TotalMinutes);
            }

            _recoveryMark = now;
        }
    }

    /// <returns>Zero when the units were taken; otherwise how long to wait before asking again.</returns>
    private TimeSpan TryTake(int units)
    {
        lock (_lock)
        {
            var now = Now();
            Refill(now);

            // A call dearer than the whole bucket is let through once the bucket is full, and the surplus is paid back later.
            var needed = Math.Min(units, _capacity);
            var untilRefillStarts = _pausedUntil > now ? _pausedUntil - now : TimeSpan.Zero;
            if (untilRefillStarts == TimeSpan.Zero && _tokens + 1e-9 >= needed)
            {
                _tokens -= units;
                return TimeSpan.Zero;
            }

            var missing = Math.Max(0, needed - _tokens);
            var seconds = untilRefillStarts.TotalSeconds + missing / (_baseUnitsPerSecond * _multiplier);
            return TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(seconds * 1000)));
        }
    }

    private void Refill(TimeSpan now)
    {
        if (now <= _lastRefill)
        {
            return;
        }

        _tokens = Math.Min(_capacity, _tokens + (now - _lastRefill).TotalSeconds * _baseUnitsPerSecond * _multiplier);
        _lastRefill = now;
    }

    private TimeSpan Now() => _time.GetElapsedTime(_origin);
}
