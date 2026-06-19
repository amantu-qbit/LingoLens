using LingoLens.Core;

namespace LingoLens.Ocr.Internal;

/// <summary>
/// Converts a DBNet probability map into oriented text quadrilaterals. The pipeline is:
/// binarize at a probability threshold → label 4-connected components → for each large-enough component
/// compute a rotating-calipers minimum-area rectangle → "unclip" (expand) the box slightly → score it by
/// the mean probability under the box, dropping low-scoring regions.
/// </summary>
/// <remarks>
/// This is a clean, dependency-free approximation of PaddleOCR's DB post-processing (which uses
/// OpenCV <c>findContours</c> + <c>minAreaRect</c> + a Vatti/pyclipper unclip). The minimum-area rectangle
/// here is computed by rotating-calipers over the component's convex hull, which is exact for the bounding
/// rectangle; the unclip uses a perimeter/area ratio approximation rather than true polygon offsetting.
/// </remarks>
internal sealed class DbNetPostProcessor
{
    private readonly float _binThreshold;
    private readonly float _boxThreshold;
    private readonly double _unclipRatio;
    private readonly int _minBoxSidePx;

    public DbNetPostProcessor(
        float binThreshold = 0.3f,
        float boxThreshold = 0.5f,
        double unclipRatio = 1.6,
        int minBoxSidePx = 3)
    {
        _binThreshold = binThreshold;
        _boxThreshold = boxThreshold;
        _unclipRatio = unclipRatio;
        _minBoxSidePx = minBoxSidePx;
    }

    /// <summary>A detected box in detector-input pixel space, with its DB score.</summary>
    public readonly record struct DetectionBox(Quad Quad, double Score);

    /// <summary>
    /// Extracts boxes from a probability map of size <paramref name="mapW"/> x <paramref name="mapH"/>
    /// (row-major, values in [0,1]). Returned quads are in the probability map's pixel space; callers
    /// scale them to frame coordinates.
    /// </summary>
    public IReadOnlyList<DetectionBox> Extract(ReadOnlySpan<float> prob, int mapW, int mapH)
    {
        if (mapW <= 0 || mapH <= 0) return Array.Empty<DetectionBox>();

        // Binarize.
        var bin = new bool[mapW * mapH];
        for (int i = 0; i < bin.Length; i++) bin[i] = prob[i] >= _binThreshold;

        // Label 4-connected components via flood fill (iterative stack to avoid deep recursion).
        var labels = new int[mapW * mapH];
        var stack = new Stack<int>();
        var results = new List<DetectionBox>();
        int currentLabel = 0;

        // We need a managed copy of prob for scoring inside the loop.
        // (prob is a span; capture per-component pixel coordinates and score against it.)
        var componentPixels = new List<int>(256);

        for (int start = 0; start < bin.Length; start++)
        {
            if (!bin[start] || labels[start] != 0) continue;
            currentLabel++;
            componentPixels.Clear();
            stack.Push(start);
            labels[start] = currentLabel;

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            while (stack.Count > 0)
            {
                int p = stack.Pop();
                componentPixels.Add(p);
                int px = p % mapW;
                int py = p / mapW;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;

                // 4-neighbourhood.
                if (px > 0) Push(p - 1);
                if (px < mapW - 1) Push(p + 1);
                if (py > 0) Push(p - mapW);
                if (py < mapH - 1) Push(p + mapW);
            }

            // Reject tiny components early. Mirror IsDegenerate: a component is too small when EITHER the
            // width OR the height is below the minimum side, so use '||' (a '&&' would only reject when both
            // dimensions are tiny, letting thin slivers through).
            if (maxX - minX + 1 < _minBoxSidePx || maxY - minY + 1 < _minBoxSidePx) continue;

            // Build the point set and compute the min-area rectangle from its convex hull.
            var points = new Point2[componentPixels.Count];
            for (int k = 0; k < componentPixels.Count; k++)
            {
                int p = componentPixels[k];
                points[k] = new Point2(p % mapW, p / mapW);
            }

            Quad rect = MinAreaRect(points);
            if (IsDegenerate(rect)) continue;

            // Score: mean probability inside the (pre-unclip) component bounding region.
            double score = ScoreRegion(prob, mapW, mapH, minX, minY, maxX, maxY, bin, currentLabel, labels);
            if (score < _boxThreshold) continue;

            Quad unclipped = Unclip(rect, _unclipRatio);
            results.Add(new DetectionBox(unclipped, score));

            void Push(int q)
            {
                if (bin[q] && labels[q] == 0)
                {
                    labels[q] = currentLabel;
                    stack.Push(q);
                }
            }
        }

        return results;
    }

    private static double ScoreRegion(
        ReadOnlySpan<float> prob, int mapW, int mapH,
        int minX, int minY, int maxX, int maxY,
        bool[] bin, int label, int[] labels)
    {
        double sum = 0;
        int count = 0;
        for (int y = minY; y <= maxY; y++)
        {
            int row = y * mapW;
            for (int x = minX; x <= maxX; x++)
            {
                int idx = row + x;
                if (labels[idx] == label)
                {
                    sum += prob[idx];
                    count++;
                }
            }
        }
        return count > 0 ? sum / count : 0.0;
    }

    private bool IsDegenerate(in Quad q)
    {
        double w = Dist(q.TopLeft, q.TopRight);
        double h = Dist(q.TopLeft, q.BottomLeft);
        return Math.Min(w, h) < _minBoxSidePx;
    }

    /// <summary>
    /// Computes the minimum-area enclosing rectangle of a point set using a convex-hull +
    /// rotating-calipers search, returning its four corners ordered clockwise from the top-left.
    /// </summary>
    public static Quad MinAreaRect(ReadOnlySpan<Point2> points)
    {
        var hull = ConvexHull(points);
        if (hull.Count < 3)
        {
            // Fall back to the axis-aligned bounding box.
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return Quad.FromRect(new RectD(minX, minY, maxX - minX, maxY - minY));
        }

        double bestArea = double.MaxValue;
        Quad best = default;

        int n = hull.Count;
        for (int i = 0; i < n; i++)
        {
            Point2 a = hull[i];
            Point2 b = hull[(i + 1) % n];
            // Edge direction (unit vector) and its normal.
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-9) continue;
            ex /= len; ey /= len;
            double nx = -ey, ny = ex;

            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;
            foreach (var p in hull)
            {
                double u = (p.X - a.X) * ex + (p.Y - a.Y) * ey;
                double v = (p.X - a.X) * nx + (p.Y - a.Y) * ny;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            double area = (maxU - minU) * (maxV - minV);
            if (area < bestArea)
            {
                bestArea = area;
                // Reconstruct the four corners in world space.
                Point2 Corner(double u, double v) =>
                    new(a.X + ex * u + nx * v, a.Y + ey * u + ny * v);
                var c0 = Corner(minU, minV);
                var c1 = Corner(maxU, minV);
                var c2 = Corner(maxU, maxV);
                var c3 = Corner(minU, maxV);
                best = OrderClockwiseFromTopLeft(c0, c1, c2, c3);
            }
        }

        return best;
    }

    /// <summary>Andrew's monotone-chain convex hull. Returns hull vertices in counter-clockwise order.</summary>
    private static List<Point2> ConvexHull(ReadOnlySpan<Point2> input)
    {
        // Deduplicate and sort by (x, then y).
        var pts = new List<Point2>(input.Length);
        foreach (var p in input) pts.Add(p);
        pts.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));

        int n = pts.Count;
        if (n < 3) return pts;

        var hull = new List<Point2>(2 * n);
        // Lower hull.
        for (int i = 0; i < n; i++)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], pts[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(pts[i]);
        }
        // Upper hull.
        int lower = hull.Count + 1;
        for (int i = n - 2; i >= 0; i--)
        {
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], pts[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(pts[i]);
        }
        hull.RemoveAt(hull.Count - 1); // last point equals the first
        return hull;
    }

    private static double Cross(Point2 o, Point2 a, Point2 b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>
    /// Expands a quad outward by an unclip ratio approximating PaddleOCR's polygon offset. The offset
    /// distance is derived from area/perimeter so wider boxes expand proportionally.
    /// </summary>
    public static Quad Unclip(in Quad q, double ratio)
    {
        // Polygon area (shoelace) and perimeter.
        Point2[] poly = { q.TopLeft, q.TopRight, q.BottomRight, q.BottomLeft };
        double area = 0, perim = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % 4];
            area += a.X * b.Y - b.X * a.Y;
            perim += Dist(a, b);
        }
        area = Math.Abs(area) * 0.5;
        if (perim < 1e-6) return q;

        // distance = area * ratio / perimeter  (the standard DB unclip distance).
        double distance = area * ratio / perim;

        // Offset each corner outward along the bisector from the polygon centroid.
        double cx = (q.TopLeft.X + q.TopRight.X + q.BottomRight.X + q.BottomLeft.X) / 4.0;
        double cy = (q.TopLeft.Y + q.TopRight.Y + q.BottomRight.Y + q.BottomLeft.Y) / 4.0;

        Point2 Push(Point2 p)
        {
            double dx = p.X - cx, dy = p.Y - cy;
            double l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-6) return p;
            return new Point2(p.X + dx / l * distance, p.Y + dy / l * distance);
        }

        return new Quad(Push(q.TopLeft), Push(q.TopRight), Push(q.BottomRight), Push(q.BottomLeft));
    }

    /// <summary>Orders four corners clockwise starting from the top-left (smallest x+y).</summary>
    public static Quad OrderClockwiseFromTopLeft(Point2 a, Point2 b, Point2 c, Point2 d)
    {
        Span<Point2> pts = stackalloc Point2[] { a, b, c, d };
        // Centroid.
        double cx = (a.X + b.X + c.X + d.X) / 4.0;
        double cy = (a.Y + b.Y + c.Y + d.Y) / 4.0;

        // Sort by angle to get a consistent ring, then rotate so the top-left corner is first.
        // Simple insertion sort on 4 elements by atan2.
        Span<double> ang = stackalloc double[4];
        for (int i = 0; i < 4; i++) ang[i] = Math.Atan2(pts[i].Y - cy, pts[i].X - cx);
        for (int i = 1; i < 4; i++)
        {
            var pv = pts[i]; double av = ang[i]; int j = i - 1;
            while (j >= 0 && ang[j] > av) { pts[j + 1] = pts[j]; ang[j + 1] = ang[j]; j--; }
            pts[j + 1] = pv; ang[j + 1] = av;
        }

        // Identify the top-left as the point minimizing (x + y).
        int tl = 0; double best = pts[0].X + pts[0].Y;
        for (int i = 1; i < 4; i++)
        {
            double s = pts[i].X + pts[i].Y;
            if (s < best) { best = s; tl = i; }
        }

        // The angle-sorted ring is counter-clockwise (screen y-down makes atan2 increase clockwise),
        // so emit in order to produce a clockwise quad starting from top-left. Copy out of the
        // stackalloc span before indexing (ref locals cannot be captured / re-indexed lazily).
        Point2 p0 = pts[(tl + 0) % 4];
        Point2 p1 = pts[(tl + 1) % 4];
        Point2 p2 = pts[(tl + 2) % 4];
        Point2 p3 = pts[(tl + 3) % 4];
        return new Quad(p0, p1, p2, p3);
    }

    private static double Dist(Point2 a, Point2 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
