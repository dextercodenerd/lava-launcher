using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace GenericLauncher.Misc;

/// <summary>
/// A thread-safe, size-bounded LRU (Least Recently Used) cache.
///
/// Entries are evicted when the cache exceeds <c>maxSize</c>; the least
/// recently accessed entry is removed first. There is no TTL -- entries
/// remain until evicted by size pressure or explicitly cleared.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _maxSize;
    private readonly Dictionary<TKey, LinkedListNode<CacheEntry>> _map;
    private readonly LinkedList<CacheEntry> _order = [];
    private readonly Lock _lock = new();

    private readonly record struct CacheEntry(TKey Key, TValue Value);

    public LruCache(int maxSize, IEqualityComparer<TKey>? comparer = null)
    {
        if (maxSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSize), "Maximum size must be greater than 0.");
        }

        _maxSize = maxSize;
        _map = new Dictionary<TKey, LinkedListNode<CacheEntry>>(comparer);
    }

    /// <summary>
    /// Tries to retrieve a cached value. On hit the entry is promoted to
    /// the front of the recency list (most recently used).
    /// </summary>
    /// <returns><c>true</c> if the key exists in the cache; <c>false</c> otherwise.</returns>
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // Promote to front (most recently used).
                _order.Remove(node);
                _order.AddFirst(node);

                value = node.Value.Value;
                return true;
            }

            value = default;
            return false;
        }
    }

    /// <summary>
    /// Inserts or updates a cache entry.  The entry is placed at the front
    /// of the recency list.  If the cache exceeds <c>maxSize</c>, the least
    /// recently used entry (tail) is evicted.
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }

            var node = _order.AddFirst(new CacheEntry(key, value));
            _map[key] = node;

            while (_map.Count > _maxSize)
            {
                var tail = _order.Last!;
                _map.Remove(tail.Value.Key);
                _order.RemoveLast();
            }
        }
    }

    /// <summary>Removes all entries from the cache.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}