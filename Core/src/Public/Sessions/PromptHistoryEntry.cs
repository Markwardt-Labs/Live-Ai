namespace Markwardt.LiveAi;

/// <summary>
/// One entry of a session's conversation, as the provider stored it: an event with the place and the time it has in the stored conversation.
/// </summary>
public sealed record PromptHistoryEntry
{
    /// <summary>
    /// Gets what the entry is, in the same vocabulary as the events of <see cref="IPromptSession" />: a <see cref="PromptInputEvent" /> for a prompt or message the AI was given, a <see cref="PromptMessageEvent" /> for what it wrote, a <see cref="PromptToolCallEvent" /> and its <see cref="PromptToolResultEvent" /> for a tool it used.
    /// </summary>
    public required PromptEvent Event { get; init; }

    /// <summary>
    /// Gets when the entry was written, or <see langword="null" /> when the provider did not store it.
    /// </summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary>
    /// Gets where the entry is in its session's stored conversation, to fork or rewind to it or to page the history from it. It is only meaningful for the session the entry came from: a branch has a conversation of its own.
    /// </summary>
    public PromptHistoryPoint Point { get; init; }

    /// <summary>
    /// Gets the text of the entry: the input, the message, the arguments of a tool call as JSON, or the output of a tool.
    /// </summary>
    public string Text
        => Event switch
        {
            PromptInputEvent input => input.Text,
            PromptMessageEvent message => message.Text,
            PromptToolCallEvent call => call.Input,
            PromptToolResultEvent result => result.Output,
            _ => string.Empty
        };
}

/// <summary>
/// Cuts a conversation that is read newest first (see <see cref="IPromptSession.GetHistory" />) at a place that was seen before.
/// </summary>
public static class PromptHistoryExtensions
{
    extension(IAsyncEnumerable<PromptHistoryEntry> newestFirst)
    {
        /// <summary>
        /// Keeps only the entries older than a point, to continue with an earlier part of a long conversation after one that was already seen.
        /// </summary>
        /// <param name="point">The place to continue from, as <see cref="PromptHistoryEntry.Point" /> gave it.</param>
        /// <returns>The entries older than the point, newest first.</returns>
        public IAsyncEnumerable<PromptHistoryEntry> OlderThan(PromptHistoryPoint point)
            => newestFirst.SkipWhile(entry => entry.Point.CompareTo(point) >= 0);

        /// <summary>
        /// Keeps only the entries newer than a point, to watch a conversation grow. The reading stops at the first entry that is not newer, so little of the file is read.
        /// </summary>
        /// <param name="point">The place to stop at, as <see cref="PromptHistoryEntry.Point" /> gave it.</param>
        /// <returns>The entries newer than the point, newest first.</returns>
        public IAsyncEnumerable<PromptHistoryEntry> NewerThan(PromptHistoryPoint point)
            => newestFirst.TakeWhile(entry => entry.Point.CompareTo(point) > 0);
    }
}
