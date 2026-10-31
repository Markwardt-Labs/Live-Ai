namespace Markwardt.LiveAi;

/// <summary>
/// A stream over a copy of a file's bytes that hands them back to be stored when it is flushed and when it is disposed.
/// </summary>
internal sealed class CommitStream : MemoryStream
{
    private readonly Action<byte[]> commit;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommitStream" /> class.
    /// </summary>
    /// <param name="content">The bytes the stream starts with.</param>
    /// <param name="atEnd">Whether the position starts at the end of the bytes, instead of at their start.</param>
    /// <param name="commit">Stores the bytes the stream holds.</param>
    public CommitStream(byte[] content, bool atEnd, Action<byte[]> commit)
    {
        this.commit = commit;
        Write(content, 0, content.Length);

        if (!atEnd)
        {
            Position = 0;
        }
    }

    /// <inheritdoc />
    public override void Flush()
    {
        commit(ToArray());
        base.Flush();
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellation)
    {
        Flush();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && CanWrite)
        {
            commit(ToArray());
        }

        base.Dispose(disposing);
    }
}
