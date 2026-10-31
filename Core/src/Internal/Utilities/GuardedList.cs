namespace Markwardt.LiveAi;

/// <summary>
/// A list that checks what is put into it, is safe to use from several threads, and tells its owner when it changed. Enumerating it walks a copy.
/// </summary>
/// <typeparam name="T">The type of the items.</typeparam>
/// <param name="gate">The lock shared with the owner.</param>
/// <param name="check">Throws when an item may not be added, given the other items.</param>
/// <param name="begin">Called with the lock held before every change; throws when changes are no longer allowed.</param>
/// <param name="changed">Called after every change, without the lock.</param>
internal sealed class GuardedList<T>(object gate, Action<T, IReadOnlyList<T>> check, Action begin, Action changed) : IList<T>
{
    private readonly List<T> items = [];

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (gate)
            {
                return items.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public T this[int index]
    {
        get
        {
            lock (gate)
            {
                return items[index];
            }
        }

        set
        {
            lock (gate)
            {
                begin();
                check(value, [.. items.Where((_, other) => other != index)]);
                items[index] = value;
            }

            changed();
        }
    }

    /// <inheritdoc />
    public void Add(T item)
        => Insert(Count, item);

    /// <inheritdoc />
    public void Insert(int index, T item)
    {
        lock (gate)
        {
            begin();
            check(item, items);
            items.Insert(index, item);
        }

        changed();
    }

    /// <inheritdoc />
    public bool Remove(T item)
    {
        bool removed;

        lock (gate)
        {
            begin();
            removed = items.Remove(item);
        }

        if (removed)
        {
            changed();
        }

        return removed;
    }

    /// <inheritdoc />
    public void RemoveAt(int index)
    {
        lock (gate)
        {
            begin();
            items.RemoveAt(index);
        }

        changed();
    }

    /// <inheritdoc />
    public void Clear()
    {
        bool any;

        lock (gate)
        {
            begin();
            any = items.Count > 0;
            items.Clear();
        }

        if (any)
        {
            changed();
        }
    }

    /// <inheritdoc />
    public bool Contains(T item)
    {
        lock (gate)
        {
            return items.Contains(item);
        }
    }

    /// <inheritdoc />
    public int IndexOf(T item)
    {
        lock (gate)
        {
            return items.IndexOf(item);
        }
    }

    /// <inheritdoc />
    public void CopyTo(T[] array, int arrayIndex)
    {
        lock (gate)
        {
            items.CopyTo(array, arrayIndex);
        }
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
        => ((IEnumerable<T>)Copy()).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    /// <summary>
    /// Takes a copy of the items.
    /// </summary>
    /// <returns>The items as they are now.</returns>
    public T[] Copy()
    {
        lock (gate)
        {
            return [.. items];
        }
    }
}
