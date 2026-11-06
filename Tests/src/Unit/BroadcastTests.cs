namespace Markwardt.LiveAi.Tests.Unit;

public sealed class BroadcastTests
{
    private readonly Broadcast<int> broadcast = new();

    [Fact]
    public async Task Publish_ReachesEveryWatcherThatIsThereInOrder()
    {
        Recorder<int> first = new();
        Recorder<int> second = new();
        broadcast.Subscribe(first);
        broadcast.Publish(1);
        broadcast.Subscribe(second);

        broadcast.Publish(2);
        await broadcast.Delivered();

        Assert.Equal([1, 2], first.Values);
        Assert.Equal([2], second.Values);
    }

    [Fact]
    public async Task ANewWatcher_IsGivenOnlyWhatIsPublishedAfterItSubscribes()
    {
        broadcast.Publish(1);
        broadcast.Publish(2);
        Recorder<int> late = new();

        broadcast.Subscribe(late);
        broadcast.Publish(3);
        await broadcast.Delivered();

        Assert.Equal([3], late.Values);
    }

    [Fact]
    public async Task ANewWatcherWithNothingPublished_IsGivenNothing()
    {
        Recorder<int> recorder = new();

        broadcast.Subscribe(recorder);
        await broadcast.Delivered();

        Assert.Empty(recorder.Values);
    }

    [Fact]
    public async Task Dispose_StopsTheWatcherAndIsRepeatable()
    {
        Recorder<int> recorder = new();
        IDisposable subscription = broadcast.Subscribe(recorder);
        broadcast.Publish(1);
        await broadcast.Delivered();

        subscription.Dispose();
        subscription.Dispose();
        broadcast.Publish(2);
        await broadcast.Delivered();

        Assert.Equal([1], recorder.Values);
    }

    [Fact]
    public async Task Complete_TellsEveryoneOnceAndLaterWatchersAtOnce()
    {
        Recorder<int> recorder = new();
        broadcast.Subscribe(recorder);

        broadcast.Complete();
        broadcast.Complete();
        broadcast.Publish(1);
        Recorder<int> late = new();
        broadcast.Subscribe(late);
        await recorder.Ended();
        await late.Ended();

        Assert.True(recorder.IsCompleted);
        Assert.Empty(recorder.Values);
        Assert.Empty(late.Values);
        Assert.True(late.IsCompleted);
        Assert.Null(late.Error);
    }

    [Fact]
    public async Task Fail_TellsEveryoneTheErrorAndLaterWatchersAtOnce()
    {
        InvalidOperationException error = new("broke");
        Recorder<int> recorder = new();
        broadcast.Subscribe(recorder);

        broadcast.Fail(error);
        broadcast.Complete();
        Recorder<int> late = new();
        broadcast.Subscribe(late);
        await recorder.Ended();
        await late.Ended();

        Assert.Same(error, recorder.Error);
        Assert.False(recorder.IsCompleted);
        Assert.Same(error, late.Error);
        Assert.False(late.IsCompleted);
    }

    [Fact]
    public async Task AWatcherThatThrows_IsDroppedWithoutDisturbingTheOthers()
    {
        Mock<IObserver<int>> bad = new();
        bad.Setup(mock => mock.OnNext(It.IsAny<int>())).Throws(new InvalidOperationException("bad watcher"));
        Recorder<int> good = new();
        broadcast.Subscribe(bad.Object);
        broadcast.Subscribe(good);

        broadcast.Publish(1);
        await broadcast.Delivered();
        broadcast.Publish(2);
        await broadcast.Delivered();

        bad.Verify(mock => mock.OnNext(It.IsAny<int>()), Times.Once);
        Assert.Equal([1, 2], good.Values);
    }

    [Fact]
    public async Task AWatcherThatThrowsWhenTheSequenceEnds_DoesNotStopTheOthersFromBeingTold()
    {
        Mock<IObserver<int>> bad = new();
        bad.Setup(mock => mock.OnCompleted()).Throws(new InvalidOperationException("bad watcher"));
        Recorder<int> good = new();
        broadcast.Subscribe(bad.Object);
        broadcast.Subscribe(good);

        broadcast.Complete();
        await good.Ended();

        Assert.True(good.IsCompleted);
    }

    [Fact]
    public async Task Publish_FromSeveralThreadsWhileWatchersComeAndGo_NeverFails()
    {
        Recorder<int> steady = new();
        broadcast.Subscribe(steady);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (int index = 0; index < 500; index++)
            {
                broadcast.Subscribe(new Recorder<int>()).Dispose();
                broadcast.Publish(worker);
            }
        })));
        await steady.Until(values => values.Count == 2000);

        Assert.Equal(2000, steady.Values.Count);
    }

    [Fact]
    public async Task ASlowWatcher_NeverHoldsUpThePublisherOrTheOtherWatchers()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IObserver<int>> slow = new();
        slow.Setup(mock => mock.OnNext(It.IsAny<int>())).Callback(() => release.Task.Wait());
        Recorder<int> quick = new();
        broadcast.Subscribe(slow.Object);
        broadcast.Subscribe(quick);

        Task publishing = Task.Run(() =>
        {
            for (int value = 0; value < 100; value++)
            {
                broadcast.Publish(value);
            }
        });
        await publishing.WaitAsync(TimeSpan.FromSeconds(5));
        await quick.Until(values => values.Count == 100);

        Assert.Equal(Enumerable.Range(0, 100), quick.Values);
        release.SetResult();
    }

    [Fact]
    public async Task AWatcherThatPublishesFromItsCallback_DoesNotDeadlockOrReorderWhatTheOthersSee()
    {
        Recorder<int> other = new();
        broadcast.Subscribe(new Echo(broadcast));
        broadcast.Subscribe(other);

        broadcast.Publish(1);
        await other.Until(values => values.Count >= 2);

        Assert.Equal([1, 100], other.Values);
    }

    private sealed class Echo(Broadcast<int> broadcast) : IObserver<int>
    {
        public void OnNext(int value)
        {
            if (value < 100)
            {
                broadcast.Publish(100);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
