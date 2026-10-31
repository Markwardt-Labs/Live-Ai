namespace Markwardt.LiveAi;

/// <summary>
/// Runs requests on an executor and enforces the options of a request around it: the timeout, the window limit, the retries and the limit on tool calls.
/// </summary>
internal interface IPromptRequester
{
    /// <summary>
    /// Sends a prompt and reports everything that happens while the AI works.
    /// </summary>
    /// <param name="input">The prompt: a string, or an object to write as JSON.</param>
    /// <param name="options">How to run it.</param>
    /// <param name="limits">The limits to run it under.</param>
    /// <param name="cancellation">Ends the work in progress.</param>
    /// <returns>Each event as soon as it happens; the sequence ends when the AI has finished.</returns>
    IAsyncEnumerable<PromptEvent> Events(object input, PromptRequestOptions options, PromptRequestLimits limits, CancellationToken cancellation = default);

    /// <summary>
    /// Sends a prompt, waits for the AI to finish it and returns everything it produced.
    /// </summary>
    /// <param name="input">The prompt: a string, or an object to write as JSON.</param>
    /// <param name="options">How to run it.</param>
    /// <param name="limits">The limits to run it under.</param>
    /// <param name="cancellation">Ends the work in progress.</param>
    /// <returns>The result.</returns>
    Task<PromptResult> Collect(object input, PromptRequestOptions options, PromptRequestLimits limits, CancellationToken cancellation = default);
}

/// <inheritdoc />
internal sealed class PromptRequester(IPromptExecutor executor, IPromptLog? log = null, TimeProvider? time = null) : IPromptRequester
{
    private readonly IPromptLog log = log ?? new PromptLog(null);

    private readonly TimeProvider time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async IAsyncEnumerable<PromptEvent> Events(object input, PromptRequestOptions options, PromptRequestLimits limits, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);
        Check(options);

        await using PromptLimitWatch watch = new(executor.GetWindows, limits.Timeout, limits.WindowLimit?.Reached, limits.WindowLimit?.Interval ?? TimeSpan.FromMinutes(1), time, cancellation);
        PromptRun run = Prepare(input, options);

        int calls = 0;

        for (int attempt = 1; ; attempt++)
        {
            bool reported = false;
            bool retrying = false;
            await using IAsyncEnumerator<PromptEvent> events = executor.Execute(run, watch.Token).GetAsyncEnumerator(watch.Token);

            while (!retrying)
            {
                try
                {
                    if (!await events.MoveNextAsync())
                    {
                        yield break;
                    }
                }
                catch (OperationCanceledException exception)
                {
                    throw Stopped(watch.Explain(exception));
                }
                catch (Exception exception) when (!reported && Retries(limits, exception, attempt, watch))
                {
                    await Pause(limits, attempt, exception, watch);
                    retrying = true;
                }

                if (retrying)
                {
                    continue;
                }

                reported = true;

                if (events.Current is PromptToolCallEvent && limits.MaxToolCalls is { } allowed && ++calls > allowed)
                {
                    throw Stopped(new PromptLimitException(PromptLimit.ToolCalls, $"The AI made more than {allowed} tool calls."));
                }

                yield return events.Current;
            }
        }
    }

    /// <inheritdoc />
    public async Task<PromptResult> Collect(object input, PromptRequestOptions options, PromptRequestLimits limits, CancellationToken cancellation = default)
    {
        List<PromptEvent> events = await Events(input, options, limits, cancellation).ToListAsync(cancellation);
        List<string> messages = [.. events.OfType<PromptMessageEvent>().Select(message => message.Text)];

        return new PromptResult
        {
            Text = options.Schema is null ? string.Join("\n\n", messages) : messages.LastOrDefault() ?? string.Empty,
            Events = events,
            Usage = events.OfType<PromptUsageEvent>().Aggregate(new PromptUsage(), (total, usage) => total.Plus(usage.Usage))
        };
    }

    private void Check(PromptRequestOptions options)
    {
        foreach (string note in options.Notes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(note, nameof(options));
        }

        if (!Enum.IsDefined(options.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Mode, "The mode is not a defined value.");
        }
    }

    private Exception Stopped(Exception error)
    {
        if (error is PromptLimitException limit)
        {
            log.LimitReached(limit.Limit);
        }

        return error;
    }

    private bool Retries(PromptRequestLimits limits, Exception exception, int attempt, PromptLimitWatch watch)
        => limits.Retry is { } retry && attempt < retry.MaxAttempts && !watch.Token.IsCancellationRequested && (retry.ShouldRetry?.Invoke(exception) ?? exception is PromptSessionException and not (PromptLimitException or PromptSessionOutputException or PromptToolStartException or PromptSessionStateException));

    private async Task Pause(PromptRequestLimits limits, int attempt, Exception failure, PromptLimitWatch watch)
    {
        PromptRetry retry = limits.Retry!;
        TimeSpan delay = TimeSpan.FromTicks((long)Math.Min(retry.MaxDelay.Ticks, retry.InitialDelay.Ticks * Math.Pow(retry.BackoffFactor, attempt - 1)));
        log.Retrying(attempt, retry.MaxAttempts, delay, failure);

        try
        {
            await Task.Delay(delay, time, watch.Token);
        }
        catch (OperationCanceledException exception)
        {
            throw Stopped(watch.Explain(exception));
        }
    }

    private PromptRun Prepare(object input, PromptRequestOptions options)
        => new()
        {
            Text = input is string text ? text : PromptJson.Serialize(input),
            Attachments = options.Attachments,
            Notes = options.Notes,
            Schema = options.Schema,
            Mode = options.Mode
        };
}
