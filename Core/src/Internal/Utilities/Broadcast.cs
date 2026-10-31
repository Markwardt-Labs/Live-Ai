namespace Markwardt.LiveAi;

/// <summary>
/// Sends values to everyone who is watching, as an <see cref="IObservable{T}" /> does: values, then at most one end, which is either a completion or an error. A new watcher is first given the most recent value, if there is one, so that one who arrives a moment late does not miss it. Every watcher has a queue and a task of its own, so the watchers are called in order but never by the thread that publishes and never under a lock: a watcher that is slow, blocks or calls back into the publisher cannot stall or deadlock it.
/// </summary>
/// <typeparam name="T">The type of the values.</typeparam>
internal interface IBroadcast<T> : IObservable<T>
{
    /// <summary>
    /// Sends a value to everyone who is watching now. Does nothing after the end.
    /// </summary>
    /// <param name="value">The value.</param>
    void Publish(T value);

    /// <summary>
    /// Waits until everything published so far has been handed to every watcher there is now.
    /// </summary>
    /// <returns>A task that completes when the watchers have been called with it.</returns>
    Task Delivered();

    /// <summary>
    /// Ends the sequence normally. Later watchers are told at once.
    /// </summary>
    void Complete();

    /// <summary>
    /// Ends the sequence with an error. Later watchers are told at once.
    /// </summary>
    /// <param name="exception">The error.</param>
    void Fail(Exception exception);
}

/// <inheritdoc />
/// <param name="replay">Whether a new watcher is first given the most recent value.</param>
internal sealed class Broadcast<T>(bool replay = true) : IBroadcast<T>
{
    private readonly object gate = new();

    private readonly List<Watcher> watchers = [];

    private T? latest;

    private bool hasLatest;

    private bool ended;

    private Exception? failure;

    /// <inheritdoc />
    public IDisposable Subscribe(IObserver<T> observer)
    {
        Watcher watcher = new(observer, Drop);

        lock (gate)
        {
            if (replay && hasLatest)
            {
                watcher.Post(Delivery.Of(latest!));
            }

            if (ended)
            {
                watcher.Post(Delivery.End(failure));
            }
            else
            {
                watchers.Add(watcher);
            }
        }

        return watcher;
    }

    /// <inheritdoc />
    public void Publish(T value)
    {
        lock (gate)
        {
            if (ended)
            {
                return;
            }

            latest = value;
            hasLatest = true;

            foreach (Watcher watcher in watchers)
            {
                watcher.Post(Delivery.Of(value));
            }
        }
    }

    /// <inheritdoc />
    public Task Delivered()
    {
        lock (gate)
        {
            return Task.WhenAll(watchers.Select(watcher => watcher.Mark()));
        }
    }

    /// <inheritdoc />
    public void Complete()
        => Finish(null);

    /// <inheritdoc />
    public void Fail(Exception exception)
        => Finish(exception);

    private void Finish(Exception? error)
    {
        lock (gate)
        {
            if (ended)
            {
                return;
            }

            ended = true;
            failure = error;

            foreach (Watcher watcher in watchers)
            {
                watcher.Post(Delivery.End(error));
            }

            watchers.Clear();
        }
    }

    private void Drop(Watcher watcher)
    {
        lock (gate)
        {
            watchers.Remove(watcher);
        }
    }

    private readonly record struct Delivery(T? Value, bool IsEnd, Exception? Error, TaskCompletionSource? Marker)
    {
        public static Delivery Of(T value)
            => new(value, false, null, null);

        public static Delivery End(Exception? error)
            => new(default, true, error, null);
    }

    private sealed class Watcher : IDisposable
    {
        private readonly IObserver<T> observer;

        private readonly Action<Watcher> dropped;

        private readonly Channel<Delivery> queue = Channel.CreateUnbounded<Delivery>(new UnboundedChannelOptions { SingleReader = true });

        private volatile bool stopped;

        public Watcher(IObserver<T> observer, Action<Watcher> dropped)
        {
            this.observer = observer;
            this.dropped = dropped;
            _ = Task.Run(Run);
        }

        public void Post(Delivery delivery)
            => queue.Writer.TryWrite(delivery);

        public Task Mark()
        {
            TaskCompletionSource marker = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return queue.Writer.TryWrite(new Delivery(default, false, null, marker)) ? marker.Task : Task.CompletedTask;
        }

        public void Dispose()
        {
            stopped = true;
            queue.Writer.TryComplete();
            dropped(this);
        }

        private async Task Run()
        {
            await foreach (Delivery delivery in queue.Reader.ReadAllAsync())
            {
                if (stopped)
                {
                    break;
                }

                if (delivery.Marker is { } marker)
                {
                    marker.TrySetResult();
                    continue;
                }

                try
                {
                    if (!delivery.IsEnd)
                    {
                        observer.OnNext(delivery.Value!);
                    }
                    else if (delivery.Error is null)
                    {
                        observer.OnCompleted();
                    }
                    else
                    {
                        observer.OnError(delivery.Error);
                    }
                }
                catch (Exception)
                {
                    stopped = true;
                    dropped(this);
                    break;
                }

                if (delivery.IsEnd)
                {
                    break;
                }
            }

            queue.Writer.TryComplete();

            while (queue.Reader.TryRead(out Delivery rest))
            {
                rest.Marker?.TrySetResult();
            }
        }
    }
}
