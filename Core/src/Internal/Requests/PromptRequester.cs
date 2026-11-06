namespace Markwardt.LiveAi;

/// <summary>
/// Runs requests on an executor: checks the options of a request, turns the input into a run, and gathers what the AI produced.
/// </summary>
internal interface IPromptRequester
{
    /// <summary>
    /// Sends a prompt and reports everything that happens while the AI works.
    /// </summary>
    /// <param name="input">The prompt: a string, or an object to write as JSON.</param>
    /// <param name="options">How to run it.</param>
    /// <param name="cancellation">Ends the work in progress.</param>
    /// <returns>Each event as soon as it happens; the sequence ends when the AI has finished.</returns>
    IAsyncEnumerable<PromptEvent> Events(object input, PromptRequestOptions options, CancellationToken cancellation = default);

    /// <summary>
    /// Sends a prompt, waits for the AI to finish it and returns everything it produced.
    /// </summary>
    /// <param name="input">The prompt: a string, or an object to write as JSON.</param>
    /// <param name="options">How to run it.</param>
    /// <param name="cancellation">Ends the work in progress.</param>
    /// <returns>The result.</returns>
    Task<PromptResult> Collect(object input, PromptRequestOptions options, CancellationToken cancellation = default);
}

/// <inheritdoc />
/// <param name="executor">Runs the prompts.</param>
internal sealed class PromptRequester(IPromptExecutor executor) : IPromptRequester
{
    /// <inheritdoc />
    public async IAsyncEnumerable<PromptEvent> Events(object input, PromptRequestOptions options, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        Check(options);

        await using IAsyncEnumerator<PromptEvent> events = executor.Execute(Prepare(input, options), cancellation).GetAsyncEnumerator(cancellation);

        while (await events.MoveNextAsync())
        {
            yield return events.Current;
        }
    }

    /// <inheritdoc />
    public async Task<PromptResult> Collect(object input, PromptRequestOptions options, CancellationToken cancellation = default)
    {
        List<PromptEvent> events = await Events(input, options, cancellation).ToListAsync(cancellation);
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
