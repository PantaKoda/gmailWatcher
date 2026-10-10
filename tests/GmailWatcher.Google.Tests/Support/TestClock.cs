using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace GmailWatcher.Google.Tests.Support;

/// <summary>
/// A fake clock that also knows which timers are pending. That lets a test jump from one timer to the next
/// (<see cref="FakeTimeExtensions.PumpAsync(TestClock, Task)"/>) instead of guessing step sizes, so code that
/// waits on the <see cref="TimeProvider"/> finishes at exactly the moment it asked to wake up, without any real sleeping.
/// </summary>
internal sealed class TestClock(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly object _lock = new();
    private readonly List<Tracked> _pending = [];
    private int _fired;
    private int _created;

    public TestClock()
        : this(Fixtures.Start)
    {
    }

    /// <summary>How many timers have fired so far.</summary>
    public int Fired => Volatile.Read(ref _fired);

    /// <summary>How many timers have been created so far.</summary>
    public int Created => Volatile.Read(ref _created);

    /// <summary>When the earliest pending timer is due, or null when nothing is waiting on the clock.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count == 0 ? null : _pending.Min(timer => timer.Due);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var tracked = new Tracked(this);
        Interlocked.Increment(ref _created);
        tracked.Schedule(dueTime, period);
        tracked.Inner = base.CreateTimer(
            timerState =>
            {
                Interlocked.Increment(ref _fired);
                tracked.Fired();
                callback(timerState);
            },
            state,
            dueTime,
            period);
        return tracked;
    }

    private void Update(Tracked timer, DateTimeOffset? due)
    {
        lock (_lock)
        {
            if (due is null)
            {
                _pending.Remove(timer);
                return;
            }

            timer.Due = due.Value;
            if (!_pending.Contains(timer))
            {
                _pending.Add(timer);
            }
        }
    }

    private sealed class Tracked(TestClock clock) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public ITimer Inner { get; set; } = null!;

        public DateTimeOffset Due { get; set; }

        public void Schedule(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            clock.Update(this, dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime);
        }

        public void Fired()
        {
            clock.Update(this, _period > TimeSpan.Zero ? clock.GetUtcNow() + _period : null);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Schedule(dueTime, period);
            return Inner.Change(dueTime, period);
        }

        public void Dispose()
        {
            clock.Update(this, null);
            Inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class FakeTimeExtensions
{
    /// <summary>
    /// Runs the clock forward, timer by timer, until the task finishes. Between jumps it lets the code under test react
    /// (which can take a thread-pool hop), so a jump never skips past a timer that is about to be created.
    /// </summary>
    public static async Task PumpAsync(this TestClock clock, Task task)
    {
        var realDeadline = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Assert.True(realDeadline.Elapsed < TimeSpan.FromSeconds(30), $"The code under test stopped making progress (now {clock.GetUtcNow():O}, next timer {clock.NextDue:O}, {clock.Fired} timers fired, {clock.Created} created).");

            if (clock.NextDue is not { } due)
            {
                // Nothing is waiting on the clock: the code is between two steps (or finishing). Give it real time.
                await Task.Delay(1, TestContext.Current.CancellationToken);
                continue;
            }

            var firedBefore = clock.Fired;
            var createdBefore = clock.Created;
            var wait = due - clock.GetUtcNow();
            // A timer that is "due now" but has not fired was registered a moment after the clock moved: creep forward until it does.
            clock.Advance(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1));

            if (clock.Fired != firedBefore)
            {
                // Something woke up. Wait (briefly, in real time) until it has either finished or started waiting again.
                var reaction = Stopwatch.StartNew();
                while (!task.IsCompleted && clock.Created == createdBefore && reaction.ElapsedMilliseconds < 20)
                {
                    await Task.Yield();
                }
            }
        }

        await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    public static async Task<T> PumpAsync<T>(this TestClock clock, Task<T> task)
    {
        await clock.PumpAsync((Task)task);
        return await task;
    }
}
