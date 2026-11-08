namespace Markwardt.LiveAi;

/// <summary>
/// The stream a file of a <see cref="TieredStore" /> is written through: it buffers in memory, switches to a file of the disk folder when the content grows past the memory limit, refuses to grow past the file limit, and sets the content of the file when it is closed.
/// </summary>
internal sealed class SpoolStream : Stream
{
    private readonly TieredStore owner;

    private readonly string path;

    private Stream inner;

    private string? spoolPath;

    private readonly bool placeholder;

    private bool finished;

    private bool failed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpoolStream" /> class.
    /// </summary>
    /// <param name="owner">The store the file belongs to.</param>
    /// <param name="path">The path of the file.</param>
    /// <param name="seed">The bytes the file has now, in memory, or <see langword="null" />.</param>
    /// <param name="seedFile">The file that holds the bytes the file has now, or <see langword="null" />.</param>
    /// <param name="atEnd">Whether writing starts at the end of what the file has now.</param>
    /// <param name="placeholder">Whether opening the stream created the file, which is then removed if the stream fails.</param>
    public SpoolStream(TieredStore owner, string path, byte[]? seed, string? seedFile, bool atEnd, bool placeholder)
    {
        this.owner = owner;
        this.path = path;
        this.placeholder = placeholder;
        inner = new MemoryStream();

        if (seed is not null)
        {
            inner.Write(seed, 0, seed.Length);
        }
        else if (seedFile is not null)
        {
            SeedFromFile(seedFile);
        }

        inner.Position = atEnd ? inner.Length : 0;
    }

    /// <inheritdoc />
    public override bool CanRead => !finished;

    /// <inheritdoc />
    public override bool CanSeek => !finished;

    /// <inheritdoc />
    public override bool CanWrite => !finished;

    /// <inheritdoc />
    public override long Length => inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
        => inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
        => inner.Read(buffer);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
        => inner.ReadAsync(buffer, cancellation);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
        => inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        Reserve(value);
        inner.SetLength(value);
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        Reserve(inner.Position + count);
        inner.Write(buffer, offset, count);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Reserve(inner.Position + buffer.Length);
        inner.Write(buffer);
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default)
    {
        Reserve(inner.Position + buffer.Length);
        return inner.WriteAsync(buffer, cancellation);
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
        => WriteAsync(buffer.AsMemory(offset, count), cancellation).AsTask();

    /// <inheritdoc />
    public override void WriteByte(byte value)
    {
        Reserve(inner.Position + 1);
        inner.WriteByte(value);
    }

    /// <inheritdoc />
    public override void Flush()
    {
        if (inner is MemoryStream memory)
        {
            owner.Commit(path, memory.ToArray(), null, memory.Length);
        }
        else
        {
            inner.Flush();
        }
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellation)
    {
        Flush();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        Finish();
        await base.DisposeAsync();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Finish();
        }

        base.Dispose(disposing);
    }

    private void SeedFromFile(string seedFile)
    {
        string? folder = owner.PoolOf.Folder(owner.Name);

        if (folder is null || new FileInfo(seedFile).Length <= owner.PoolOf.Settings.MemoryLimit)
        {
            inner.Dispose();
            MemoryStream memory = new();

            using (FileStream source = new(seedFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                source.CopyTo(memory);
            }

            inner = memory;
            return;
        }

        inner.Dispose();
        spoolPath = Path.Combine(folder, $"{Guid.NewGuid():N}.bin");
        File.Copy(seedFile, spoolPath);
        inner = new FileStream(spoolPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, true);
    }

    private void Reserve(long end)
    {
        long limit = owner.PoolOf.Settings.FileLimit;

        if (end > limit)
        {
            failed = true;
            throw new IOException($"The file would be larger than the limit of {limit} bytes for a file in {owner.Name.ToUpperInvariant()}, so the write was refused and the file was not stored. Keep large data in the workspace.");
        }

        if (inner is MemoryStream memory && end > owner.PoolOf.Settings.MemoryLimit && owner.PoolOf.Folder(owner.Name) is { } folder)
        {
            spoolPath = Path.Combine(folder, $"{Guid.NewGuid():N}.bin");
            FileStream file = new(spoolPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, true);
            long position = memory.Position;
            memory.Position = 0;
            memory.CopyTo(file);
            file.Position = position;
            inner = file;
            memory.Dispose();
        }
    }

    private void Finish()
    {
        if (finished)
        {
            return;
        }

        finished = true;

        if (failed)
        {
            inner.Dispose();

            if (spoolPath is not null)
            {
                try
                {
                    File.Delete(spoolPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            owner.Abandon(path, placeholder);
            return;
        }

        if (inner is MemoryStream memory)
        {
            byte[] bytes = memory.ToArray();
            memory.Dispose();
            owner.Commit(path, bytes, null, bytes.Length);
            return;
        }

        FileStream file = (FileStream)inner;
        long length = file.Length;
        file.Flush();
        file.Dispose();
        owner.Commit(path, null, spoolPath, length);
    }
}
