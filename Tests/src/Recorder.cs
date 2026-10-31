namespace Markwardt.LiveAi.Tests;

public sealed class Recorder<T> : IObserver<T>
{
    private readonly object gate = new();

    private readonly List<T> values = [];

    public IReadOnlyList<T> Values
    {
        get
        {
            lock (gate)
            {
                return values.ToArray();
            }
        }
    }

    public bool IsCompleted { get; private set; }

    public Exception? Error { get; private set; }

    public async Task Until(Func<IReadOnlyList<T>, bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(Values); attempt++)
        {
            await Task.Delay(10);
        }
    }

    public async Task Ended()
    {
        for (int attempt = 0; attempt < 500 && !IsCompleted && Error is null; attempt++)
        {
            await Task.Delay(10);
        }
    }

    public void OnNext(T value)
    {
        lock (gate)
        {
            values.Add(value);
        }
    }

    public void OnCompleted()
        => IsCompleted = true;

    public void OnError(Exception error)
        => Error = error;
}
