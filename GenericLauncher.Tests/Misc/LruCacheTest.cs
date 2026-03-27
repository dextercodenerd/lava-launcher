using System;
using GenericLauncher.Misc;
using JetBrains.Annotations;
using Xunit;

namespace GenericLauncher.Tests.Misc;

[TestSubject(typeof(LruCache<,>))]
public class LruCacheTest
{
    [Fact]
    public void Set_And_TryGet_ReturnsValue()
    {
        var cache = new LruCache<string, int>(maxSize: 4);

        cache.Set("a", 1);

        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void TryGet_Miss_ReturnsFalse()
    {
        var cache = new LruCache<string, int>(maxSize: 4);

        Assert.False(cache.TryGet("missing", out _));
    }

    [Fact]
    public void Set_EvictsLru_WhenFull()
    {
        var cache = new LruCache<string, int>(maxSize: 2);

        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("c", 3); // "a" should be evicted (least recently used)

        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void TryGet_TouchesEntry_PreventsEviction()
    {
        var cache = new LruCache<string, int>(maxSize: 2);

        cache.Set("a", 1);
        cache.Set("b", 2);

        // Touch "a" so it becomes most recently used.
        cache.TryGet("a", out _);

        cache.Set("c", 3); // "b" should be evicted (now least recently used)

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void Set_UpdatesExisting()
    {
        var cache = new LruCache<string, int>(maxSize: 4);

        cache.Set("a", 1);
        cache.Set("a", 42);

        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var cache = new LruCache<string, int>(maxSize: 4);

        cache.Set("a", 1);
        cache.Set("b", 2);

        cache.Clear();

        Assert.False(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void CaseInsensitive_Comparer()
    {
        var cache = new LruCache<string, int>(maxSize: 4, StringComparer.OrdinalIgnoreCase);

        cache.Set("ABC", 1);

        Assert.True(cache.TryGet("abc", out var value));
        Assert.Equal(1, value);
    }
}
