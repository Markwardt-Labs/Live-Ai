namespace Markwardt.LiveAi;

/// <summary>
/// A place in a session's stored conversation, which is what <see cref="IPromptSession.Fork(PromptHistoryPoint?, CancellationToken)" />, <see cref="IPromptSession.Rewind" /> and the <c>before</c> and <c>after</c> of <see cref="IPromptSession.GetHistory" /> take. Get one from <see cref="PromptHistoryEntry.Point" />; it can be kept without keeping the entry, and the session's extension methods also take the entry itself. It is only meaningful for the session it came from: a branch has a conversation of its own.
/// </summary>
/// <param name="Position">Where the record that holds the entry starts in the provider's stored conversation, in bytes.</param>
/// <param name="Index">The place of the entry among the entries of its record (an assistant message with text and a tool call is one record), counted from zero.</param>
public readonly record struct PromptHistoryPoint(long Position, int Index = 0) : IComparable<PromptHistoryPoint>
{
    /// <summary>
    /// Compares the place of this point in the conversation with another one.
    /// </summary>
    /// <param name="other">The other point.</param>
    /// <returns>A negative number when this point is earlier, zero when they are the same and a positive number when it is later.</returns>
    public int CompareTo(PromptHistoryPoint other)
        => (Position, Index).CompareTo((other.Position, other.Index));
}
