namespace Markwardt.LiveAi;

/// <summary>
/// A dictionary by name that checks what is put into it, is safe to use from several threads, and tells its owner when it changed. Enumerating it walks a copy.
/// </summary>
/// <typeparam name="TValue">The type of the values.</typeparam>
/// <param name="gate">The lock shared with the owner.</param>
/// <param name="check">Throws when an entry may not be put in.</param>
/// <param name="begin">Called with the lock held before every change; throws when changes are no longer allowed.</param>
/// <param name="changed">Called after every change, without the lock.</param>
/// <param name="initial">What it starts with, which is not checked.</param>
internal sealed class GuardedDictionary<TValue>(object gate, Action<string, TValue> check, Action begin, Action changed, IEnumerable<KeyValuePair<string, TValue>> initial) : IDictionary<string, TValue>
{
    private readonly Dictionary<string, TValue> entries = new(initial, StringComparer.Ordinal);

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public ICollection<string> Keys
    {
        get
        {
            lock (gate)
            {
                return [.. entries.Keys];
            }
        }
    }

    /// <inheritdoc />
    public ICollection<TValue> Values
    {
        get
        {
            lock (gate)
            {
                return [.. entries.Values];
            }
        }
    }

    /// <inheritdoc />
    public TValue this[string key]
    {
        get
        {
            lock (gate)
            {
                return entries[key];
            }
        }

        set
        {
            lock (gate)
            {
                begin();
                check(key, value);
                entries[key] = value;
            }

            changed();
        }
    }

    /// <inheritdoc />
    public void Add(string key, TValue value)
    {
        lock (gate)
        {
            begin();
            check(key, value);
            entries.Add(key, value);
        }

        changed();
    }

    /// <inheritdoc />
    public void Add(KeyValuePair<string, TValue> item)
        => Add(item.Key, item.Value);

    /// <inheritdoc />
    public bool Remove(string key)
    {
        bool removed;

        lock (gate)
        {
            begin();
            removed = entries.Remove(key);
        }

        if (removed)
        {
            changed();
        }

        return removed;
    }

    /// <inheritdoc />
    public bool Remove(KeyValuePair<string, TValue> item)
    {
        bool removed;

        lock (gate)
        {
            begin();
            removed = ((ICollection<KeyValuePair<string, TValue>>)entries).Remove(item);
        }

        if (removed)
        {
            changed();
        }

        return removed;
    }

    /// <inheritdoc />
    public void Clear()
    {
        bool any;

        lock (gate)
        {
            begin();
            any = entries.Count > 0;
            entries.Clear();
        }

        if (any)
        {
            changed();
        }
    }

    /// <inheritdoc />
    public bool ContainsKey(string key)
    {
        lock (gate)
        {
            return entries.ContainsKey(key);
        }
    }

    /// <inheritdoc />
    public bool Contains(KeyValuePair<string, TValue> item)
    {
        lock (gate)
        {
            return ((ICollection<KeyValuePair<string, TValue>>)entries).Contains(item);
        }
    }

    /// <inheritdoc />
    public bool TryGetValue(string key, out TValue value)
    {
        lock (gate)
        {
            return entries.TryGetValue(key, out value!);
        }
    }

    /// <inheritdoc />
    public void CopyTo(KeyValuePair<string, TValue>[] array, int arrayIndex)
    {
        lock (gate)
        {
            ((ICollection<KeyValuePair<string, TValue>>)entries).CopyTo(array, arrayIndex);
        }
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, TValue>>)Copy()).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    /// <summary>
    /// Takes a copy of the entries.
    /// </summary>
    /// <returns>The entries as they are now.</returns>
    public Dictionary<string, TValue> Copy()
    {
        lock (gate)
        {
            return new Dictionary<string, TValue>(entries, StringComparer.Ordinal);
        }
    }
}
