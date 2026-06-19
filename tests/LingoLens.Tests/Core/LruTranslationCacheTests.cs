using LingoLens.Core.Translation;
using Xunit;

namespace LingoLens.Tests.Core;

public class LruTranslationCacheTests
{
    private static readonly LanguagePair Zh = LanguagePair.ZhToEn;

    [Fact]
    public void Set_then_Get_returns_value()
    {
        var cache = new LruTranslationCache(8);
        cache.Set(Zh, "你好", "Hello");

        Assert.True(cache.TryGet(Zh, "你好", out var v));
        Assert.Equal("Hello", v);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Miss_returns_false()
    {
        var cache = new LruTranslationCache(8);
        Assert.False(cache.TryGet(Zh, "不存在", out _));
    }

    [Fact]
    public void Distinct_pairs_do_not_collide()
    {
        var cache = new LruTranslationCache(8);
        cache.Set(new LanguagePair("zh", "en"), "x", "english");
        cache.Set(new LanguagePair("zh", "ja"), "x", "japanese");

        Assert.True(cache.TryGet(new LanguagePair("zh", "en"), "x", out var en));
        Assert.True(cache.TryGet(new LanguagePair("zh", "ja"), "x", out var ja));
        Assert.Equal("english", en);
        Assert.Equal("japanese", ja);
    }

    [Fact]
    public void Evicts_least_recently_used_beyond_capacity()
    {
        var cache = new LruTranslationCache(2);
        cache.Set(Zh, "a", "1");
        cache.Set(Zh, "b", "2");
        // touch "a" so "b" becomes LRU
        Assert.True(cache.TryGet(Zh, "a", out _));
        cache.Set(Zh, "c", "3"); // evicts "b"

        Assert.True(cache.TryGet(Zh, "a", out _));
        Assert.False(cache.TryGet(Zh, "b", out _));
        Assert.True(cache.TryGet(Zh, "c", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Set_existing_updates_value()
    {
        var cache = new LruTranslationCache(4);
        cache.Set(Zh, "k", "v1");
        cache.Set(Zh, "k", "v2");
        Assert.True(cache.TryGet(Zh, "k", out var v));
        Assert.Equal("v2", v);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void HitRate_tracks_hits_and_misses()
    {
        var cache = new LruTranslationCache(4);
        cache.Set(Zh, "k", "v");
        cache.TryGet(Zh, "k", out _);   // hit
        cache.TryGet(Zh, "miss", out _); // miss
        Assert.Equal(0.5, cache.HitRate, 3);
    }

    [Fact]
    public void Clear_empties_cache()
    {
        var cache = new LruTranslationCache(4);
        cache.Set(Zh, "k", "v");
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(Zh, "k", out _));
    }
}
