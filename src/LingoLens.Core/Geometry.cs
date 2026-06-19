namespace LingoLens.Core;

/// <summary>A 2D point in device pixels (or DIPs, depending on context).</summary>
public readonly record struct Point2(double X, double Y)
{
    public static readonly Point2 Zero = new(0, 0);
}

/// <summary>An integer rectangle (typically frame/screen pixel coordinates).</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public static readonly RectI Empty = new(0, 0, 0, 0);

    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => (long)Width * Height;

    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

    public bool IntersectsWith(RectI o) =>
        !IsEmpty && !o.IsEmpty && X < o.Right && Right > o.X && Y < o.Bottom && Bottom > o.Y;

    public RectI Intersect(RectI o)
    {
        int x1 = Math.Max(X, o.X), y1 = Math.Max(Y, o.Y);
        int x2 = Math.Min(Right, o.Right), y2 = Math.Min(Bottom, o.Bottom);
        return (x2 > x1 && y2 > y1) ? new RectI(x1, y1, x2 - x1, y2 - y1) : Empty;
    }

    public RectI Union(RectI o)
    {
        if (IsEmpty) return o;
        if (o.IsEmpty) return this;
        int x1 = Math.Min(X, o.X), y1 = Math.Min(Y, o.Y);
        int x2 = Math.Max(Right, o.Right), y2 = Math.Max(Bottom, o.Bottom);
        return new RectI(x1, y1, x2 - x1, y2 - y1);
    }

    public RectI Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);

    public RectI ClampTo(RectI bounds) => Intersect(bounds);
}

/// <summary>A floating-point rectangle (layout/measurement space).</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public static readonly RectD Empty = new(0, 0, 0, 0);
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public RectI ToRectI() => new((int)Math.Floor(X), (int)Math.Floor(Y),
                                  (int)Math.Ceiling(Width), (int)Math.Ceiling(Height));
}

/// <summary>
/// A quadrilateral text box (supports rotated/skewed text). Corners are ordered clockwise
/// starting from the top-left.
/// </summary>
public readonly record struct Quad(Point2 TopLeft, Point2 TopRight, Point2 BottomRight, Point2 BottomLeft)
{
    /// <summary>Axis-aligned bounding box of the quad.</summary>
    public RectD Bounds
    {
        get
        {
            double minX = Math.Min(Math.Min(TopLeft.X, TopRight.X), Math.Min(BottomRight.X, BottomLeft.X));
            double minY = Math.Min(Math.Min(TopLeft.Y, TopRight.Y), Math.Min(BottomRight.Y, BottomLeft.Y));
            double maxX = Math.Max(Math.Max(TopLeft.X, TopRight.X), Math.Max(BottomRight.X, BottomLeft.X));
            double maxY = Math.Max(Math.Max(TopLeft.Y, TopRight.Y), Math.Max(BottomRight.Y, BottomLeft.Y));
            return new RectD(minX, minY, maxX - minX, maxY - minY);
        }
    }

    /// <summary>Approximate rotation in radians of the top edge.</summary>
    public double SkewRadians => Math.Atan2(TopRight.Y - TopLeft.Y, TopRight.X - TopLeft.X);

    public static Quad FromRect(RectD r) => new(
        new Point2(r.X, r.Y), new Point2(r.Right, r.Y),
        new Point2(r.Right, r.Bottom), new Point2(r.X, r.Bottom));

    public static Quad FromRect(RectI r) => FromRect(new RectD(r.X, r.Y, r.Width, r.Height));
}
