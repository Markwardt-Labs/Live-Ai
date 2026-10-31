namespace Markwardt.LiveAi;

/// <inheritdoc />
internal sealed class PromptLoop<TReply> : IPromptLoop<TReply>
{
    private readonly CancellationTokenSource stopping = new();

    private readonly CancellationTokenSource expiry;

    private readonly CancellationTokenSource running;

    private readonly Func<string, TReply> read;

    private readonly PromptLoopLimits<TReply> limits;

    private readonly SemaphoreSlim signal = new(0);

    private readonly object gate = new();

    private readonly Broadcast<TReply> replies = new();

    private readonly TimeSpan margin;

    private readonly IPromptSession session;

    private object input;

    private PromptRequestOptions options;

    private PromptLoopSchedule schedule;

    private readonly TimeProvider time;

    private long started;

    private bool sending = true;

    private bool triggered;

    private bool finishing;

    private int runs;

    private PromptUsage spent = new();

    private volatile PromptLoopStopReason reason;

    private DateTimeOffset? resumesAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="PromptLoop{TReply}" /> class and starts the first send.
    /// </summary>
    /// <param name="session">The session to send to.</param>
    /// <param name="input">The prompt to send: a string, or an object to write as JSON.</param>
    /// <param name="schedule">When the prompt is sent again.</param>
    /// <param name="options">How each send is made, or <see langword="null" /> for the session's defaults.</param>
    /// <param name="read">Turns the text a send returns into a reply.</param>
    /// <param name="limits">When the loop stops by itself, or <see langword="null" /> for never.</param>
    /// <param name="resetMargin">How long after the reset of the subscription the loop sends again when it waits for one, or <see langword="null" /> for five seconds.</param>
    /// <param name="time">The clock the loop's schedule, timeout and waits run on, or <see langword="null" /> for the system's.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive.</exception>
    public PromptLoop(IPromptSession session, object input, PromptLoopSchedule schedule, PromptRequestOptions? options, Func<string, TReply> read, PromptLoopLimits<TReply>? limits, TimeSpan? resetMargin = null, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        started = this.time.GetTimestamp();
        margin = resetMargin ?? TimeSpan.FromSeconds(5);
        this.limits = limits ?? new();

        if (this.limits.Timeout <= TimeSpan.Zero || this.limits.Timeout > TimeSpan.FromDays(24) || this.limits.MaxRuns < 1 || this.limits.WindowLimit is { IsValid: false } || this.limits.Budget is { IsValid: false })
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "The timeout and the window interval must be positive and at most 24 days, and the number of runs at least 1.");
        }

        if (this.limits.WaitForWindow is not null && this.limits.WindowLimit is null)
        {
            throw new ArgumentException("Waiting for a window needs a window limit that says when to wait.", nameof(limits));
        }

        ArgumentNullException.ThrowIfNull(input);
        this.session = session;
        this.input = input;
        this.options = options ?? new();
        this.schedule = schedule;
        this.read = read;
        expiry = this.limits.Timeout is { } timeout ? new CancellationTokenSource(timeout, this.time) : new CancellationTokenSource();
        running = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, expiry.Token);

        Task = Run();
    }

    /// <inheritdoc />
    public object Input
    {
        get
        {
            lock (gate)
            {
                return input;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);

            lock (gate)
            {
                input = value;
            }
        }
    }

    /// <inheritdoc />
    public PromptRequestOptions Options
    {
        get
        {
            lock (gate)
            {
                return options;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);

            lock (gate)
            {
                options = value;
            }
        }
    }

    /// <inheritdoc />
    public PromptLoopSchedule Schedule
    {
        get
        {
            lock (gate)
            {
                return schedule;
            }
        }

        set
        {
            lock (gate)
            {
                schedule = value;
            }

            signal.Release();
        }
    }

    /// <inheritdoc />
    public IObservable<TReply> Replies => replies;

    /// <inheritdoc />
    public DateTimeOffset? ResumesAt
    {
        get
        {
            lock (gate)
            {
                return resumesAt;
            }
        }
    }

    /// <inheritdoc />
    public PromptLoopStopReason StopReason => reason;

    /// <inheritdoc />
    public Task Task { get; }

    /// <inheritdoc />
    public void Trigger()
    {
        lock (gate)
        {
            if (sending || finishing || running.IsCancellationRequested)
            {
                return;
            }

            triggered = true;
        }

        signal.Release();
    }

    /// <inheritdoc />
    public void Dispose()
        => stopping.Cancel();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            finishing = true;
        }

        signal.Release();
        await Task.WhenAny(Task);
    }

    private async Task Run()
    {
        try
        {
            reason = await Repeat();
            replies.Complete();
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            reason = stopping.IsCancellationRequested ? PromptLoopStopReason.Disposed : PromptLoopStopReason.Timeout;
            replies.Complete();
        }
        catch (Exception exception)
        {
            reason = PromptLoopStopReason.Failed;
            replies.Fail(exception);
            throw;
        }
        finally
        {
            running.Dispose();
            expiry.Dispose();
        }
    }

    private async Task<PromptLoopStopReason> Repeat()
    {
        (object text, PromptRequestOptions how) = Current();

        while (true)
        {
            await Task.Yield();

            TReply reply;

            try
            {
                while (true)
                {
                    try
                    {
                        PromptResult result = await Collect(text, how, running.Token);
                        spent = spent.Plus(result.Usage);
                        reply = read(result.Text);
                        break;
                    }
                    catch (PromptLimitException exception) when (exception.Limit == PromptLimit.Window && limits.WaitForWindow is { } choose && ResetOf(choose, exception.Windows) is { } at)
                    {
                        lock (gate)
                        {
                            resumesAt = at;
                        }

                        try
                        {
                            await Task.Delay(at - time.GetUtcNow(), time, running.Token);
                        }
                        finally
                        {
                            lock (gate)
                            {
                                resumesAt = null;
                            }
                        }
                    }
                }
            }
            catch (PromptLimitException exception) when (exception.Limit == PromptLimit.Window)
            {
                return PromptLoopStopReason.WindowLimit;
            }
            finally
            {
                lock (gate)
                {
                    sending = false;
                }
            }

            replies.Publish(reply);
            runs++;

            if (limits.MaxRuns is { } maximum && runs >= maximum)
            {
                return PromptLoopStopReason.MaxRuns;
            }

            if (limits.Budget is { } budget && budget.IsReached(spent))
            {
                return PromptLoopStopReason.Budget;
            }

            if (limits.Until is { } until && until(reply))
            {
                return PromptLoopStopReason.Until;
            }

            if (await Next(running.Token) is not { } next)
            {
                return PromptLoopStopReason.Disposed;
            }

            (text, how) = next;
        }
    }

    private DateTimeOffset? ResetOf(Func<IReadOnlyList<PromptWindow>, PromptWindow?> choose, IReadOnlyList<PromptWindow>? windows)
    {
        DateTimeOffset now = time.GetUtcNow();
        return choose(windows ?? []) is { ResetsAt: { } reset } && reset > now ? new[] { reset + margin, now.AddDays(24) }.Min() : null;
    }

    private async Task<PromptResult> Collect(object text, PromptRequestOptions how, CancellationToken cancellation)
    {
        if (session is IPromptCollector collector)
        {
            return await collector.Collect(text, how, limits.WindowLimit is { } reached ? new PromptRequestLimits { WindowLimit = reached } : null, cancellation);
        }

        if (limits.WindowLimit is not null)
        {
            throw new NotSupportedException("A window limit of a loop needs a session that the library made.");
        }

        PromptUsage before = session.Usage;
        string answer = await session.Request(text, how, cancellation);
        return new PromptResult { Text = answer, Events = [], Usage = session.Usage.Minus(before) };
    }

    private (object Input, PromptRequestOptions Options) Current()
    {
        lock (gate)
        {
            return (input, options);
        }
    }

    private async Task<(object Input, PromptRequestOptions Options)?> Next(CancellationToken cancellation = default)
    {
        while (true)
        {
            TimeSpan? remaining;

            lock (gate)
            {
                if (finishing)
                {
                    return null;
                }

                remaining = schedule.Mode switch
                {
                    PromptLoopMode.Continuous => TimeSpan.Zero,
                    PromptLoopMode.Interval => schedule.Interval - time.GetElapsedTime(started),
                    _ => null
                };

                if (triggered || remaining <= TimeSpan.Zero)
                {
                    triggered = false;
                    sending = true;
                    started = time.GetTimestamp();
                    return (input, options);
                }
            }

            using CancellationTokenSource timeout = remaining is { } left ? new CancellationTokenSource(left > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : left, time) : new CancellationTokenSource();
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timeout.Token);

            try
            {
                await signal.WaitAsync(waiting.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
            }
        }
    }
}
