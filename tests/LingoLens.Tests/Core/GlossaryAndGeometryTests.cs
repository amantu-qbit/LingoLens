using LingoLens.Core;
using LingoLens.Core.Translation;
using Xunit;

namespace LingoLens.Tests.Core;

public class InMemoryGlossaryTests
{
    private static readonly LanguagePair Zh = LanguagePair.ZhToEn;

    [Fact]
    public void AddOrUpdate_then_resolve()
    {
        var g = new InMemoryGlossary();
        g.AddOrUpdate(Zh, "大佬", "expert");
        Assert.True(g.TryResolve(Zh, "大佬", out var v));
        Assert.Equal("expert", v);
    }

    [Fact]
    public void Resolve_is_normalization_insensitive()
    {
        var g = new InMemoryGlossary();
        g.AddOrUpdate(Zh, "  大佬 ", "expert");
        Assert.True(g.TryResolve(Zh, "大佬", out _));
    }

    [Fact]
    public void Remove_deletes_entry()
    {
        var g = new InMemoryGlossary();
        g.AddOrUpdate(Zh, "x", "y");
        g.Remove(Zh, "x");
        Assert.False(g.TryResolve(Zh, "x", out _));
    }
}

public class GeometryTests
{
    [Fact]
    public void RectI_Intersect_and_Union()
    {
        var a = new RectI(0, 0, 10, 10);
        var b = new RectI(5, 5, 10, 10);
        Assert.True(a.IntersectsWith(b));
        Assert.Equal(new RectI(5, 5, 5, 5), a.Intersect(b));
        Assert.Equal(new RectI(0, 0, 15, 15), a.Union(b));
    }

    [Fact]
    public void RectI_no_intersection_is_empty()
    {
        var a = new RectI(0, 0, 5, 5);
        var b = new RectI(100, 100, 5, 5);
        Assert.False(a.IntersectsWith(b));
        Assert.True(a.Intersect(b).IsEmpty);
    }

    [Fact]
    public void Quad_bounds_from_rect_roundtrips()
    {
        var r = new RectD(2, 3, 10, 6);
        var q = Quad.FromRect(r);
        var b = q.Bounds;
        Assert.Equal(2, b.X, 3);
        Assert.Equal(3, b.Y, 3);
        Assert.Equal(10, b.Width, 3);
        Assert.Equal(6, b.Height, 3);
    }
}
