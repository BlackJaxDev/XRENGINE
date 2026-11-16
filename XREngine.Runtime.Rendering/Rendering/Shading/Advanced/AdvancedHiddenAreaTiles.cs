using System.Globalization;
using System.Numerics;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering;

/// <summary>
/// Finds the part of an OpenXR eye image that the headset lens never shows, so that Advanced
/// native shading can skip it. The runtime describes that part with the XR_KHR_visibility_mask
/// meshes. This class reduces them to one conservative ellipse in eye-image UV space (x right,
/// y down, 0 to 1): every visible pixel lies inside the ellipse. Classification then skips the
/// 16x16 tiles that lie fully outside the ellipse, and the background pass fills them with black.
/// The ellipse can only keep tiles that the exact mask would skip; it never skips a visible pixel.
/// </summary>
public static class AdvancedHiddenAreaTiles
{
    /// <summary>Name of the auxiliary log that records the mask source once per session.</summary>
    public const string LogFileName = "advanced-hidden-area.log";

    // Grid size for a runtime that gives only the hidden mesh: the visible cells of this grid
    // supply the points that the ellipse must enclose.
    private const int HiddenMeshGridSize = 128;
    // Relative growth of the fitted radii. It covers float rounding at the tile test.
    private const float RadiusMargin = 0.01f;

    private static readonly SyntheticMask? s_synthetic = ParseSyntheticMask(
        Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.TestHiddenAreaMask));

    private static readonly EyeCache s_left = new();
    private static readonly EyeCache s_right = new();
    private static int s_loggedSource;

    /// <summary>
    /// Resolves the ellipses of the views of one pipeline instance. A stereo instance whose right
    /// camera is the OpenXR right eye renders the left eye in view 0 and the right eye in view 1.
    /// A mono instance whose camera is an OpenXR eye camera renders that eye in view 0. Any other
    /// instance, for example the desktop output, gets no ellipse.
    /// </summary>
    public static AdvancedHiddenAreaEllipses ResolveViewEllipses(
        IOpenXrRuntime? runtime, XRCamera? camera, XRCamera? stereoRightCamera)
    {
        if (runtime is null)
            return default;
        if (stereoRightCamera is not null && runtime.ResolveEyeIndex(stereoRightCamera) == 1)
            return new AdvancedHiddenAreaEllipses(ResolveEyeEllipse(runtime, true), ResolveEyeEllipse(runtime, false));
        int eyeIndex = runtime.ResolveEyeIndex(camera);
        return eyeIndex < 0
            ? default
            : new AdvancedHiddenAreaEllipses(ResolveEyeEllipse(runtime, eyeIndex == 0), Vector4.Zero);
    }

    /// <summary>
    /// Returns the conservative visible ellipse of one OpenXR eye as (center u, center v,
    /// radius u, radius v), or <see cref="Vector4.Zero"/> when the eye has no hidden-area mask.
    /// The result is cached per mask revision and field of view, so the per-frame call does not
    /// allocate.
    /// </summary>
    private static Vector4 ResolveEyeEllipse(IOpenXrRuntime runtime, bool leftEye)
    {
        if (!runtime.TryGetEyeFovAngles(leftEye, out float angleLeft, out float angleRight, out float angleUp, out float angleDown))
            return Vector4.Zero;
        Vector4 tangents = new(MathF.Tan(angleLeft), MathF.Tan(angleRight), MathF.Tan(angleUp), MathF.Tan(angleDown));
        if (!(tangents.Y > tangents.X) || !(tangents.Z > tangents.W))
            return Vector4.Zero;

        EyeCache cache = leftEye ? s_left : s_right;
        if (s_synthetic is { } synthetic)
        {
            if (cache.Matches(ulong.MaxValue, tangents))
                return cache.Ellipse;
            LogSourceOnce(
                $"WARNING: {XREngineEnvironmentVariables.TestHiddenAreaMask} is set. The OpenXR eyes use a synthetic " +
                $"hidden-area mask (hidden fraction {synthetic.HiddenFraction:F3}, offset {synthetic.Offset.X:F3},{synthetic.Offset.Y:F3}) " +
                "instead of the runtime mask. This is a test hook only.");
            Vector4 syntheticEllipse = FitEllipse(SyntheticVisiblePoints(synthetic, tangents), tangents);
            LogEyeMeasurement(leftEye, "synthetic", synthetic.HiddenFraction, syntheticEllipse, tangents);
            return cache.Store(ulong.MaxValue, tangents, syntheticEllipse);
        }

        if (!runtime.TryGetVisibilityMaskMeshes(leftEye, out Vector2[] hiddenVertices, out uint[] hiddenIndices,
                out Vector2[] visibleVertices, out uint[] visibleIndices, out ulong revision))
        {
            LogSourceOnce("The OpenXR runtime gives no hidden-area mask (XR_KHR_visibility_mask), so the Advanced hidden-area tile reject is off.");
            return Vector4.Zero;
        }
        if (cache.Matches(revision, tangents))
            return cache.Ellipse;
        LogSourceOnce("The Advanced hidden-area tile reject uses the XR_KHR_visibility_mask meshes of the OpenXR runtime.");
        List<Vector2> points = visibleVertices.Length > 0 && visibleIndices.Length >= 3
            ? MeshVertices(visibleVertices, visibleIndices, tangents)
            : VisibleGridPoints(hiddenVertices, hiddenIndices, tangents);
        Vector4 ellipse = FitEllipse(points, tangents);
        LogEyeMeasurement(leftEye, "runtime", TriangleAreaUv(hiddenVertices, hiddenIndices, tangents), ellipse, tangents);
        return cache.Store(revision, tangents, ellipse);
    }

    /// <summary>
    /// Writes one line per eye each time its ellipse changes: the mask source, the hidden fraction
    /// of the eye image that the mask covers, the field of view, the ellipse, and the fraction of the
    /// image outside the ellipse. The last value is the upper bound of the work that the tile reject
    /// can skip; the tiles that the ellipse edge crosses stay shaded.
    /// </summary>
    private static void LogEyeMeasurement(bool leftEye, string source, float maskHiddenFraction, Vector4 ellipse, Vector4 tangents)
    {
        string message = string.Create(CultureInfo.InvariantCulture,
            $"eye={(leftEye ? "left" : "right")} source={source} maskHiddenFraction={maskHiddenFraction:F4} " +
            $"ellipseOutsideFraction={EllipseOutsideFraction(ellipse):F4} " +
            $"ellipse(center={ellipse.X:F4},{ellipse.Y:F4} radius={ellipse.Z:F4},{ellipse.W:F4}) " +
            $"fovTangents(left={tangents.X:F4} right={tangents.Y:F4} up={tangents.Z:F4} down={tangents.W:F4})");
        Debug.WriteAuxiliaryLog(LogFileName, message);
    }

    /// <summary>Area of the triangles in UV, which is their fraction of the eye image.</summary>
    private static float TriangleAreaUv(Vector2[] vertices, uint[] indices, Vector4 tangents)
    {
        float area = 0f;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            if (indices[i] >= (uint)vertices.Length || indices[i + 1] >= (uint)vertices.Length || indices[i + 2] >= (uint)vertices.Length)
                continue;
            Vector2 a = TangentToUv(vertices[indices[i]], tangents);
            Vector2 b = TangentToUv(vertices[indices[i + 1]], tangents);
            Vector2 c = TangentToUv(vertices[indices[i + 2]], tangents);
            area += MathF.Abs(Cross(b - a, c - a)) * 0.5f;
        }
        return area;
    }

    /// <summary>Fraction of the unit UV square outside the ellipse, by sampling a 256x256 grid.</summary>
    private static float EllipseOutsideFraction(Vector4 ellipse)
    {
        const int samples = 256;
        int outside = 0;
        for (int y = 0; y < samples; y++)
        {
            for (int x = 0; x < samples; x++)
            {
                Vector2 d = (new Vector2((x + 0.5f) / samples, (y + 0.5f) / samples) - new Vector2(ellipse.X, ellipse.Y)) /
                    new Vector2(ellipse.Z, ellipse.W);
                if (d.LengthSquared() > 1f)
                    outside++;
            }
        }
        return outside / (float)(samples * samples);
    }

    /// <summary>
    /// Maps a view-space point on the plane z = -1 to eye-image UV. The image stores its first
    /// row at the top, so v grows downward while the view-space y grows upward.
    /// </summary>
    private static Vector2 TangentToUv(Vector2 point, Vector4 tangents)
        => new((point.X - tangents.X) / (tangents.Y - tangents.X),
            (tangents.Z - point.Y) / (tangents.Z - tangents.W));

    private static Vector2 UvToTangent(Vector2 uv, Vector4 tangents)
        => new(tangents.X + uv.X * (tangents.Y - tangents.X),
            tangents.Z - uv.Y * (tangents.Z - tangents.W));

    /// <summary>The vertices of the visible-area mesh triangles, in UV.</summary>
    private static List<Vector2> MeshVertices(Vector2[] vertices, uint[] indices, Vector4 tangents)
    {
        List<Vector2> points = new(indices.Length);
        foreach (uint index in indices)
            if (index < (uint)vertices.Length)
                points.Add(TangentToUv(vertices[index], tangents));
        return points;
    }

    /// <summary>
    /// Covers the image with a grid and keeps the corners of each cell that the hidden triangles
    /// do not fully cover. A cell counts as hidden only when its four corners and its center all
    /// lie in hidden triangles.
    /// </summary>
    private static List<Vector2> VisibleGridPoints(Vector2[] vertices, uint[] indices, Vector4 tangents)
    {
        int triangleCount = indices.Length / 3;
        Vector2[] triangles = new Vector2[triangleCount * 3];
        for (int i = 0; i < triangleCount * 3; i++)
            triangles[i] = indices[i] < (uint)vertices.Length ? TangentToUv(vertices[indices[i]], tangents) : Vector2.Zero;

        const int n = HiddenMeshGridSize;
        const float cell = 1.0f / n;
        List<Vector2> points = [];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                Vector2 min = new(x * cell, y * cell);
                Vector2 max = min + new Vector2(cell);
                if (InsideAny(triangles, min) && InsideAny(triangles, max) &&
                    InsideAny(triangles, new Vector2(min.X, max.Y)) && InsideAny(triangles, new Vector2(max.X, min.Y)) &&
                    InsideAny(triangles, (min + max) * 0.5f))
                    continue;
                points.Add(min);
                points.Add(max);
                points.Add(new Vector2(min.X, max.Y));
                points.Add(new Vector2(max.X, min.Y));
            }
        }
        return points;
    }

    private static bool InsideAny(Vector2[] triangles, Vector2 p)
    {
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            Vector2 a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            float d0 = Cross(b - a, p - a), d1 = Cross(c - b, p - b), d2 = Cross(a - c, p - c);
            bool negative = d0 < 0f || d1 < 0f || d2 < 0f;
            bool positive = d0 > 0f || d1 > 0f || d2 > 0f;
            if (!(negative && positive))
                return true;
        }
        return false;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>
    /// Fits an axis-aligned ellipse that contains every point: the center is the center of the
    /// bounding box, the radii keep the box proportions and grow until the farthest point is inside.
    /// An ellipse is convex, so it then also contains the convex hull of the points, which contains
    /// the visible area.
    /// </summary>
    private static Vector4 FitEllipse(List<Vector2> points, Vector4 tangents)
    {
        if (points.Count == 0)
            return Vector4.Zero;
        Vector2 min = points[0], max = points[0];
        foreach (Vector2 p in points)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        Vector2 center = (min + max) * 0.5f;
        Vector2 half = Vector2.Max((max - min) * 0.5f, new Vector2(1e-4f));
        float farthest = 0f;
        foreach (Vector2 p in points)
        {
            Vector2 d = (p - center) / half;
            farthest = MathF.Max(farthest, d.LengthSquared());
        }
        Vector2 radius = half * MathF.Sqrt(farthest) * (1f + RadiusMargin);
        return new Vector4(center.X, center.Y, radius.X, radius.Y);
    }

    /// <summary>
    /// Visible points of the synthetic test mask: a circle in UV whose outside covers the requested
    /// fraction of the image. The circle goes through the same view-space mapping as a runtime mask.
    /// </summary>
    private static List<Vector2> SyntheticVisiblePoints(SyntheticMask mask, Vector4 tangents)
    {
        Vector2 center = new(0.5f + mask.Offset.X, 0.5f - mask.Offset.Y);
        float radius = SolveSyntheticRadius(center, mask.HiddenFraction);
        const int segments = 64;
        List<Vector2> points = new(segments);
        for (int i = 0; i < segments; i++)
        {
            float angle = i * (MathF.Tau / segments);
            // Through view space and back, so the test hook checks the same mapping as a runtime mask.
            Vector2 uv = center + radius * new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            points.Add(TangentToUv(UvToTangent(uv, tangents), tangents));
        }
        return points;
    }

    /// <summary>Finds by bisection the circle radius whose outside covers the fraction of the unit square.</summary>
    private static float SolveSyntheticRadius(Vector2 center, float hiddenFraction)
    {
        float low = 0.05f, high = 1.5f;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            float mid = (low + high) * 0.5f;
            if (OutsideFraction(center, mid) > hiddenFraction)
                low = mid;
            else
                high = mid;
        }
        return (low + high) * 0.5f;
    }

    private static float OutsideFraction(Vector2 center, float radius)
    {
        const int samples = 256;
        int outside = 0;
        for (int y = 0; y < samples; y++)
        {
            for (int x = 0; x < samples; x++)
            {
                Vector2 p = new((x + 0.5f) / samples, (y + 0.5f) / samples);
                if ((p - center).LengthSquared() > radius * radius)
                    outside++;
            }
        }
        return outside / (float)(samples * samples);
    }

    private static SyntheticMask? ParseSyntheticMask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 ||
            !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float fraction) ||
            !(fraction > 0f) || fraction > 0.5f)
            return null;
        float offsetX = 0f, offsetY = 0f;
        if (parts.Length >= 3 &&
            (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out offsetX) ||
             !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out offsetY)))
            return null;
        return new SyntheticMask(fraction, new Vector2(offsetX, offsetY));
    }

    private static void LogSourceOnce(string message)
    {
        if (Interlocked.Exchange(ref s_loggedSource, 1) != 0)
            return;
        Debug.WriteAuxiliaryLog(LogFileName, message);
        Debug.RenderingWarning(message);
    }

    private readonly record struct SyntheticMask(float HiddenFraction, Vector2 Offset);

    /// <summary>The last ellipse of one eye and the mask revision and field of view it came from.</summary>
    private sealed class EyeCache
    {
        private ulong _revision;
        private Vector4 _tangents;
        private bool _valid;

        public Vector4 Ellipse { get; private set; }

        public bool Matches(ulong revision, Vector4 tangents)
            => Volatile.Read(ref _valid) && _revision == revision && _tangents == tangents;

        public Vector4 Store(ulong revision, Vector4 tangents, Vector4 ellipse)
        {
            _revision = revision;
            _tangents = tangents;
            Ellipse = ellipse;
            Volatile.Write(ref _valid, true);
            return ellipse;
        }
    }
}
