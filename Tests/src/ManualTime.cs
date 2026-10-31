namespace Markwardt.LiveAi.Tests;

public sealed class ManualTime : TimeProvider
{
    private readonly object gate = new();

    private readonly List<ManualTimer> timers = [];

    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override long GetTimestamp()
        => GetUtcNow().Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;

        lock (gate)
        {
            target = now + by;
        }

        while (true)
        {
            ManualTimer? due;

            lock (gate)
            {
                due = timers.Where(timer => timer.Due is { } at && at <= target).OrderBy(timer => timer.Due).FirstOrDefault();

                if (due is null)
                {
                    now = target;
                    return;
                }

                now = due.Due!.Value > now ? due.Due.Value : now;
                due.Fired();
            }

            due.Run();
        }
    }

    private void Schedule(ManualTimer timer)
    {
        lock (gate)
        {
            if (!timers.Contains(timer))
            {
                timers.Add(timer);
            }
        }
    }

    private void Unschedule(ManualTimer timer)
    {
        lock (gate)
        {
            timers.Remove(timer);
        }
    }

    private DateTimeOffset Now()
        => GetUtcNow();

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
        {
            period = newPeriod;

            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                Due = null;
                owner.Unschedule(this);
                return true;
            }

            Due = owner.Now() + dueTime;
            owner.Schedule(this);
            return true;
        }

        public void Fired()
        {
            if (period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero)
            {
                Due = null;
                owner.Unschedule(this);
            }
            else
            {
                Due += period;
            }
        }

        public void Run()
            => callback(state);

        public void Dispose()
        {
            Due = null;
            owner.Unschedule(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
