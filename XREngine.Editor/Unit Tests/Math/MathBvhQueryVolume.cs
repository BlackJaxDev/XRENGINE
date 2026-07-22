using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Editor;

/// <summary>
/// Mutable, allocation-free query shared by the CPU oracle and BVH traversal.
/// GPU paths upload the same shape parameters from this object.
/// </summary>
internal sealed class MathBvhQueryVolume : IVolume
{
    private Frustum _frustum = new();

    public MathBvhQueryShape Shape { get; private set; }
    public AABB Box { get; private set; }
    public Sphere Sphere { get; private set; }
    public Frustum Frustum => _frustum;
    public Segment Raycast { get; private set; }

    public void Update(MathBvhQueryShape shape, float time)
    {
        Shape = shape;
        switch (shape)
        {
            case MathBvhQueryShape.Frustum:
                UpdateFrustum(time);
                return;
            case MathBvhQueryShape.Raycast:
                Raycast = CreateRaycastSegment(time);
                return;
        }

        Vector3 center = new(
            MathF.Sin(time * 0.52f) * 3.4f,
            2.5f + MathF.Sin(time * 0.37f) * 0.55f,
            MathF.Cos(time * 0.41f) * 3.4f);
        if (shape == MathBvhQueryShape.Sphere)
        {
            Sphere = new Sphere(center, 2.0f);
            return;
        }

        Box = AABB.FromCenterSize(center, new Vector3(3.0f, 2.6f, 3.0f));
    }

    public EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
        => Shape switch
        {
            MathBvhQueryShape.Sphere => Sphere.ContainsAABB(box, tolerance),
            MathBvhQueryShape.Frustum => ClassifyFrustumAabb(_frustum, box, tolerance),
            MathBvhQueryShape.Raycast => SegmentIntersectsAabb(Raycast, box)
                ? EContainment.Intersects
                : EContainment.Disjoint,
            _ => GeoUtil.ContainmentOf.AABBWithinAABB(Box.Min, Box.Max, box.Min, box.Max),
        };

    public bool IntersectsTriangle(in Triangle triangle)
        => Shape switch
        {
            MathBvhQueryShape.Sphere => GeoUtil.Intersect.SphereWithTriangle(
                Sphere.Center,
                Sphere.Radius,
                triangle.A,
                triangle.B,
                triangle.C),
            MathBvhQueryShape.Frustum => FrustumIntersectsTriangle(_frustum, triangle),
            MathBvhQueryShape.Raycast => SegmentIntersectsTriangle(Raycast, triangle),
            _ => BoxIntersectsTriangle(Box, triangle),
        };

    public EContainment ContainsBox(Box box)
        => Shape switch
        {
            MathBvhQueryShape.Sphere => Sphere.ContainsBox(box),
            MathBvhQueryShape.Frustum => _frustum.ContainsBox(box),
            MathBvhQueryShape.Raycast => EContainment.Disjoint,
            _ => Box.ContainsBox(box),
        };

    public EContainment ContainsSphere(Sphere sphere)
        => Shape switch
        {
            MathBvhQueryShape.Sphere => Sphere.ContainsSphere(sphere),
            MathBvhQueryShape.Frustum => _frustum.ContainsSphere(sphere),
            MathBvhQueryShape.Raycast => EContainment.Disjoint,
            _ => Box.ContainsSphere(sphere),
        };

    public EContainment ContainsCone(Cone cone)
        => Shape switch
        {
            MathBvhQueryShape.Sphere => Sphere.ContainsCone(cone),
            MathBvhQueryShape.Frustum => _frustum.ContainsCone(cone),
            MathBvhQueryShape.Raycast => EContainment.Disjoint,
            _ => Box.ContainsCone(cone),
        };

    public EContainment ContainsCapsule(Capsule capsule)
        => Shape switch
        {
            MathBvhQueryShape.Sphere => Sphere.ContainsCapsule(capsule),
            MathBvhQueryShape.Frustum => _frustum.ContainsCapsule(capsule),
            MathBvhQueryShape.Raycast => EContainment.Disjoint,
            _ => Box.ContainsCapsule(capsule),
        };

    public Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
        => Shape switch
        {
            MathBvhQueryShape.Sphere => Sphere.ClosestPoint(point, clampToEdge),
            MathBvhQueryShape.Frustum => _frustum.ClosestPoint(point, clampToEdge),
            MathBvhQueryShape.Raycast => Raycast.ClosestPoint(point),
            _ => Box.ClosestPoint(point, clampToEdge),
        };

    public bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
        => Shape switch
        {
            MathBvhQueryShape.Sphere => Sphere.ContainsPoint(point, tolerance),
            MathBvhQueryShape.Frustum => _frustum.ContainsPoint(point, tolerance),
            MathBvhQueryShape.Raycast => Vector3.DistanceSquared(Raycast.ClosestPoint(point), point) <= tolerance * tolerance,
            _ => Box.ContainsPoint(point, tolerance),
        };

    /// <summary>
    /// Classifies a queried point for semantic result rendering.
    /// A finite ray can intersect geometry, but cannot contain it.
    /// </summary>
    public EContainment ClassifyPoint(Vector3 point, float tolerance = float.Epsilon)
    {
        if (!ContainsPoint(point, tolerance))
            return EContainment.Disjoint;

        return Shape == MathBvhQueryShape.Raycast
            ? EContainment.Intersects
            : EContainment.Contains;
    }

    /// <summary>
    /// Classifies a segment as contained, partially intersected, or disjoint.
    /// All supported shape queries are convex, so two contained endpoints imply
    /// containment of the complete segment.
    /// </summary>
    public EContainment ClassifySegment(in Segment segment)
    {
        if (Shape == MathBvhQueryShape.Raycast)
            return EContainment.Disjoint;
        if (ContainsPoint(segment.Start) && ContainsPoint(segment.End))
            return EContainment.Contains;
        return IntersectsSegment(segment)
            ? EContainment.Intersects
            : EContainment.Disjoint;
    }

    /// <summary>
    /// Classifies a triangle as contained, partially intersected, or disjoint.
    /// A ray hit is always an intersection rather than containment.
    /// </summary>
    public EContainment ClassifyTriangle(in Triangle triangle)
    {
        if (Shape != MathBvhQueryShape.Raycast &&
            ContainsPoint(triangle.A) &&
            ContainsPoint(triangle.B) &&
            ContainsPoint(triangle.C))
        {
            return EContainment.Contains;
        }

        return IntersectsTriangle(triangle)
            ? EContainment.Intersects
            : EContainment.Disjoint;
    }

    public AABB GetAABB(bool transformed)
        => Shape switch
        {
            MathBvhQueryShape.Sphere => Sphere.GetAABB(transformed),
            MathBvhQueryShape.Frustum => _frustum.GetAABB(transformed),
            MathBvhQueryShape.Raycast => new AABB(
                Vector3.Min(Raycast.Start, Raycast.End),
                Vector3.Max(Raycast.Start, Raycast.End)),
            _ => Box,
        };

    public bool IntersectsSegment(Segment segment, out Vector3[] points)
        => Shape switch
        {
            MathBvhQueryShape.Sphere => SphereIntersectsSegment(Sphere, segment, out points),
            MathBvhQueryShape.Frustum => _frustum.IntersectsSegment(segment, out points),
            MathBvhQueryShape.Raycast => ReturnNoIntersections(out points),
            _ => Box.IntersectsSegment(segment, out points),
        };

    public bool IntersectsSegment(Segment segment)
        => Shape switch
        {
            MathBvhQueryShape.Sphere => SphereIntersectsSegment(Sphere, segment),
            MathBvhQueryShape.Frustum => _frustum.IntersectsSegment(segment),
            MathBvhQueryShape.Raycast => false,
            _ => Box.IntersectsSegment(segment),
        };

    private static Segment CreateRaycastSegment(float time)
    {
        Vector3 start = new(
            MathF.Sin(time * 0.47f) * 3.7f,
            6.0f,
            MathF.Cos(time * 0.39f) * 3.7f);
        Vector3 end = new(start.X + MathF.Sin(time * 0.23f) * 0.7f, -2.0f, start.Z);
        return new Segment(start, end);
    }

    private void UpdateFrustum(float time)
    {
        Vector3 position = new(
            MathF.Sin(time * 0.35f) * 5.6f,
            3.9f + MathF.Sin(time * 0.27f) * 0.7f,
            MathF.Cos(time * 0.35f) * 5.6f);
        Vector3 target = new(
            MathF.Sin(time * 0.17f) * 1.2f,
            2.4f,
            MathF.Cos(time * 0.19f) * 1.2f);
        Vector3 forward = Vector3.Normalize(target - position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        const float nearDistance = 0.45f;
        const float farDistance = 7.4f;
        const float aspect = 1.15f;
        float tangent = MathF.Tan(42.0f * MathF.PI / 360.0f);
        float nearY = tangent * nearDistance;
        float nearX = aspect * nearY;
        float farY = tangent * farDistance;
        float farX = aspect * farY;
        Vector3 nearCenter = position + forward * nearDistance;
        Vector3 farCenter = position + forward * farDistance;

        _frustum.UpdatePoints(
            nearCenter - up * nearY - right * nearX,
            nearCenter - up * nearY + right * nearX,
            nearCenter + up * nearY - right * nearX,
            nearCenter + up * nearY + right * nearX,
            farCenter - up * farY - right * farX,
            farCenter - up * farY + right * farX,
            farCenter + up * farY - right * farX,
            farCenter + up * farY + right * farX);
    }

    /// <summary>
    /// Mirrors the GPU frustum/AABB SAT classifier operation-for-operation so
    /// the CPU oracle and compute traversal agree at separating-axis boundaries.
    /// </summary>
    private static EContainment ClassifyFrustumAabb(
        in Frustum frustum,
        in AABB box,
        float tolerance)
    {
        float comparisonTolerance = MathF.Max(tolerance, 1e-5f);
        bool contained = true;
        if (!AccumulateFrustumPlaneAabb(frustum.Left, box, comparisonTolerance, ref contained) ||
            !AccumulateFrustumPlaneAabb(frustum.Right, box, comparisonTolerance, ref contained) ||
            !AccumulateFrustumPlaneAabb(frustum.Bottom, box, comparisonTolerance, ref contained) ||
            !AccumulateFrustumPlaneAabb(frustum.Top, box, comparisonTolerance, ref contained) ||
            !AccumulateFrustumPlaneAabb(frustum.Near, box, comparisonTolerance, ref contained) ||
            !AccumulateFrustumPlaneAabb(frustum.Far, box, comparisonTolerance, ref contained))
        {
            return EContainment.Disjoint;
        }

        if (FrustumSeparatedOnAxis(frustum, box, Vector3.UnitX, comparisonTolerance) ||
            FrustumSeparatedOnAxis(frustum, box, Vector3.UnitY, comparisonTolerance) ||
            FrustumSeparatedOnAxis(frustum, box, Vector3.UnitZ, comparisonTolerance))
        {
            return EContainment.Disjoint;
        }

        for (int edgeIndex = 0; edgeIndex < 12; edgeIndex++)
        {
            GetFrustumEdge(frustum, edgeIndex, out Vector3 edgeStart, out Vector3 edgeEnd);
            Vector3 edge = edgeEnd - edgeStart;
            if (FrustumSeparatedOnAxis(frustum, box, Vector3.Cross(edge, Vector3.UnitX), comparisonTolerance) ||
                FrustumSeparatedOnAxis(frustum, box, Vector3.Cross(edge, Vector3.UnitY), comparisonTolerance) ||
                FrustumSeparatedOnAxis(frustum, box, Vector3.Cross(edge, Vector3.UnitZ), comparisonTolerance))
            {
                return EContainment.Disjoint;
            }
        }

        return contained ? EContainment.Contains : EContainment.Intersects;
    }

    private static bool AccumulateFrustumPlaneAabb(
        in Plane plane,
        in AABB box,
        float tolerance,
        ref bool contained)
    {
        Vector3 min = box.Min;
        Vector3 max = box.Max;
        Vector3 normal = plane.Normal;
        Vector3 positive = new(
            normal.X >= 0.0f ? max.X : min.X,
            normal.Y >= 0.0f ? max.Y : min.Y,
            normal.Z >= 0.0f ? max.Z : min.Z);
        if (Vector3.Dot(normal, positive) + plane.D < -tolerance)
            return false;

        Vector3 negative = new(
            normal.X >= 0.0f ? min.X : max.X,
            normal.Y >= 0.0f ? min.Y : max.Y,
            normal.Z >= 0.0f ? min.Z : max.Z);
        if (Vector3.Dot(normal, negative) + plane.D < -tolerance)
            contained = false;
        return true;
    }

    private static bool FrustumSeparatedOnAxis(
        in Frustum frustum,
        in AABB box,
        Vector3 axis,
        float tolerance)
    {
        if (axis.LengthSquared() <= 1e-12f)
            return false;

        float boxCenter = Vector3.Dot(box.Center, axis);
        float boxRadius = Vector3.Dot(box.HalfExtents, Vector3.Abs(axis));
        float frustumMin = Vector3.Dot(frustum.LeftBottomNear, axis);
        float frustumMax = frustumMin;
        AccumulateProjection(frustum.RightBottomNear, axis, ref frustumMin, ref frustumMax);
        AccumulateProjection(frustum.LeftTopNear, axis, ref frustumMin, ref frustumMax);
        AccumulateProjection(frustum.RightTopNear, axis, ref frustumMin, ref frustumMax);
        AccumulateProjection(frustum.LeftBottomFar, axis, ref frustumMin, ref frustumMax);
        AccumulateProjection(frustum.RightBottomFar, axis, ref frustumMin, ref frustumMax);
        AccumulateProjection(frustum.LeftTopFar, axis, ref frustumMin, ref frustumMax);
        AccumulateProjection(frustum.RightTopFar, axis, ref frustumMin, ref frustumMax);
        return frustumMax < boxCenter - boxRadius - tolerance ||
            frustumMin > boxCenter + boxRadius + tolerance;
    }

    private static void AccumulateProjection(
        Vector3 point,
        Vector3 axis,
        ref float minimum,
        ref float maximum)
    {
        float projection = Vector3.Dot(point, axis);
        minimum = MathF.Min(minimum, projection);
        maximum = MathF.Max(maximum, projection);
    }

    private static void GetFrustumEdge(
        in Frustum frustum,
        int edgeIndex,
        out Vector3 start,
        out Vector3 end)
    {
        (start, end) = edgeIndex switch
        {
            0 => (frustum.LeftBottomNear, frustum.RightBottomNear),
            1 => (frustum.RightBottomNear, frustum.RightTopNear),
            2 => (frustum.RightTopNear, frustum.LeftTopNear),
            3 => (frustum.LeftTopNear, frustum.LeftBottomNear),
            4 => (frustum.LeftBottomFar, frustum.RightBottomFar),
            5 => (frustum.RightBottomFar, frustum.RightTopFar),
            6 => (frustum.RightTopFar, frustum.LeftTopFar),
            7 => (frustum.LeftTopFar, frustum.LeftBottomFar),
            8 => (frustum.LeftBottomNear, frustum.LeftBottomFar),
            9 => (frustum.RightBottomNear, frustum.RightBottomFar),
            10 => (frustum.LeftTopNear, frustum.LeftTopFar),
            _ => (frustum.RightTopNear, frustum.RightTopFar),
        };
    }

    private static bool BoxIntersectsTriangle(in AABB box, in Triangle triangle)
    {
        if (box.ContainsPoint(triangle.A) || box.ContainsPoint(triangle.B) || box.ContainsPoint(triangle.C) ||
            box.IntersectsSegment(new Segment(triangle.A, triangle.B)) ||
            box.IntersectsSegment(new Segment(triangle.B, triangle.C)) ||
            box.IntersectsSegment(new Segment(triangle.C, triangle.A)))
        {
            return true;
        }

        Span<Vector3> corners = stackalloc Vector3[8];
        FillAabbCorners(box, corners);
        return ShapeEdgesIntersectTriangle(corners, triangle);
    }

    private static bool FrustumIntersectsTriangle(in Frustum frustum, in Triangle triangle)
    {
        if (frustum.ContainsPoint(triangle.A) || frustum.ContainsPoint(triangle.B) || frustum.ContainsPoint(triangle.C) ||
            frustum.IntersectsSegment(new Segment(triangle.A, triangle.B)) ||
            frustum.IntersectsSegment(new Segment(triangle.B, triangle.C)) ||
            frustum.IntersectsSegment(new Segment(triangle.C, triangle.A)))
        {
            return true;
        }

        Span<Vector3> corners = stackalloc Vector3[8]
        {
            frustum.LeftBottomNear,
            frustum.RightBottomNear,
            frustum.LeftTopNear,
            frustum.RightTopNear,
            frustum.LeftBottomFar,
            frustum.RightBottomFar,
            frustum.LeftTopFar,
            frustum.RightTopFar,
        };
        return ShapeEdgesIntersectTriangle(corners, triangle);
    }

    private static bool ShapeEdgesIntersectTriangle(ReadOnlySpan<Vector3> corners, in Triangle triangle)
    {
        ReadOnlySpan<int> edgeIndices = [
            0, 1, 1, 3, 3, 2, 2, 0,
            4, 5, 5, 7, 7, 6, 6, 4,
            0, 4, 1, 5, 2, 6, 3, 7,
        ];
        for (int edge = 0; edge < edgeIndices.Length; edge += 2)
        {
            if (SegmentIntersectsTriangle(
                new Segment(corners[edgeIndices[edge]], corners[edgeIndices[edge + 1]]),
                triangle))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SegmentIntersectsTriangle(in Segment segment, in Triangle triangle)
    {
        Vector3 direction = segment.End - segment.Start;
        float length = direction.Length();
        if (length <= 1e-6f)
            return false;

        direction /= length;
        return GeoUtil.Intersect.RayWithTriangle(
            segment.Start,
            direction,
            triangle.A,
            triangle.B,
            triangle.C,
            out float distance) &&
            distance >= 0.0f && distance <= length;
    }

    private static bool SegmentIntersectsAabb(in Segment segment, in AABB box)
        => GeoUtil.Intersect.SegmentWithAABB(
            segment.Start,
            segment.End,
            box.Min,
            box.Max,
            out _,
            out _);

    private static bool SphereIntersectsSegment(in Sphere sphere, in Segment segment)
    {
        Vector3 delta = segment.End - segment.Start;
        float lengthSquared = delta.LengthSquared();
        float t = lengthSquared > 1e-12f
            ? Math.Clamp(Vector3.Dot(sphere.Center - segment.Start, delta) / lengthSquared, 0.0f, 1.0f)
            : 0.0f;
        Vector3 difference = segment.Start + delta * t - sphere.Center;
        return difference.LengthSquared() <= sphere.Radius * sphere.Radius;
    }

    private static bool SphereIntersectsSegment(
        in Sphere sphere,
        in Segment segment,
        out Vector3[] points)
    {
        if (!SphereIntersectsSegment(sphere, segment))
        {
            points = [];
            return false;
        }

        Vector3 delta = segment.End - segment.Start;
        float a = delta.LengthSquared();
        if (a <= 1e-12f)
        {
            points = [segment.Start];
            return true;
        }

        Vector3 relativeStart = segment.Start - sphere.Center;
        float b = 2.0f * Vector3.Dot(relativeStart, delta);
        float c = relativeStart.LengthSquared() - sphere.Radius * sphere.Radius;
        float discriminant = MathF.Max(0.0f, b * b - 4.0f * a * c);
        float root = MathF.Sqrt(discriminant);
        float inverseDenominator = 0.5f / a;
        float t0 = (-b - root) * inverseDenominator;
        float t1 = (-b + root) * inverseDenominator;
        bool firstOnSegment = t0 is >= 0.0f and <= 1.0f;
        bool secondOnSegment = t1 is >= 0.0f and <= 1.0f;
        points = (firstOnSegment, secondOnSegment) switch
        {
            (true, true) when MathF.Abs(t1 - t0) > 1e-6f =>
                [segment.Start + delta * t0, segment.Start + delta * t1],
            (true, _) => [segment.Start + delta * t0],
            (_, true) => [segment.Start + delta * t1],
            _ => [segment.Start, segment.End],
        };
        return true;
    }

    private static void FillAabbCorners(in AABB box, Span<Vector3> corners)
    {
        Vector3 min = box.Min;
        Vector3 max = box.Max;
        corners[0] = new Vector3(min.X, min.Y, min.Z);
        corners[1] = new Vector3(max.X, min.Y, min.Z);
        corners[2] = new Vector3(min.X, max.Y, min.Z);
        corners[3] = new Vector3(max.X, max.Y, min.Z);
        corners[4] = new Vector3(min.X, min.Y, max.Z);
        corners[5] = new Vector3(max.X, min.Y, max.Z);
        corners[6] = new Vector3(min.X, max.Y, max.Z);
        corners[7] = new Vector3(max.X, max.Y, max.Z);
    }

    private static bool ReturnNoIntersections(out Vector3[] points)
    {
        points = [];
        return false;
    }
}
