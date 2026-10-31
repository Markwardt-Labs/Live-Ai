namespace Markwardt.LiveAi;

/// <summary>
/// Enforces the timeout and the window limit of one prompt: gives the token that ends the work when one is reached, and says afterwards which it was.
/// </summary>
internal sealed class PromptLimitWatch : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<PromptWindow>>> readWindows;

    private readonly TimeSpan? timeout;

    private readonly CancellationToken outer;

    private readonly TimeProvider time;

    private readonly CancellationTokenSource timer;

    private readonly CancellationTokenSource window = new();

    private readonly CancellationTokenSource linked;

    private readonly Task watching = Task.CompletedTask;

    private volatile bool reached;

    private volatile IReadOnlyList<PromptWindow>? reachedWindows;

    private volatile Exception? failure;

    /// <summary>
    /// Initializes a new instance of the <see cref="PromptLimitWatch" /> class and starts watching.
    /// </summary>
    /// <param name="readWindows">Reads the subscription's limit windows.</param>
    /// <param name="timeout">How long the prompt may take, or <see langword="null" /> for no limit.</param>
    /// <param name="windowLimit">Says whether the window limit is reached, or <see langword="null" /> for no limit.</param>
    /// <param name="interval">How often the windows are read.</param>
    /// <param name="time">The clock the timeout and the interval run on.</param>
    /// <param name="cancellation">The caller's token.</param>
    public PromptLimitWatch(Func<CancellationToken, Task<IReadOnlyList<PromptWindow>>> readWindows, TimeSpan? timeout, Func<IReadOnlyList<PromptWindow>, bool>? windowLimit, TimeSpan interval, TimeProvider time, CancellationToken cancellation = default)
    {
        this.time = time;
        timer = timeout is { } span ? new CancellationTokenSource(span, time) : new CancellationTokenSource();
        this.readWindows = readWindows;
        this.timeout = timeout;
        outer = cancellation;
        linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, timer.Token, window.Token);

        if (windowLimit is not null)
        {
            watching = Task.Run(() => Watch(windowLimit, interval), CancellationToken.None);
        }
    }

    /// <summary>
    /// Gets the token that is cancelled by the caller's token or when a limit is reached.
    /// </summary>
    public CancellationToken Token => linked.Token;

    /// <summary>
    /// Turns the cancellation of a limit into the error the caller sees.
    /// </summary>
    /// <param name="exception">The cancellation that ended the work.</param>
    /// <returns>The error to throw: a <see cref="PromptLimitException" />, the failure of the read, or the cancellation itself when the caller cancelled.</returns>
    public Exception Explain(OperationCanceledException exception)
        => outer.IsCancellationRequested ? exception
            : failure is { } broken ? broken
            : reached ? new PromptLimitException(PromptLimit.Window, "A subscription window reached the limit that was set.") { Windows = reachedWindows }
            : timer.IsCancellationRequested ? new PromptLimitException(PromptLimit.Timeout, $"The prompt took longer than {timeout}.")
            : exception;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await linked.CancelAsync();
        await watching;
        linked.Dispose();
        timer.Dispose();
        window.Dispose();
    }

    private async Task Watch(Func<IReadOnlyList<PromptWindow>, bool> windowLimit, TimeSpan interval)
    {
        try
        {
            while (true)
            {
                IReadOnlyList<PromptWindow> read = await readWindows(linked.Token);

                if (windowLimit(read))
                {
                    reachedWindows = read;
                    reached = true;
                    await window.CancelAsync();
                    return;
                }

                await Task.Delay(interval, time, linked.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            await window.CancelAsync();
        }
    }
}
