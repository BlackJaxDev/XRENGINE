using Extensions;
using System.Numerics;

namespace XREngine.Data.Geometry
{
    public struct Cone(Vector3 center, Vector3 up, float height, float radius) : IShape
    {
        public Vector3 Center = center;
        public Vector3 Up = up;
        public float Height = height;
        public float Radius = radius;

        public float Diameter
        {
            readonly get => Radius * 2.0f;
            set => Radius = value / 2.0f;
        }

        public Segment Axis
        {
            readonly get => new(Center, Center + Up * Height);
            set
            {
                Center = value.Start;
                Up = Vector3.Normalize(value.End - value.Start);
                Height = value.Length;
            }
        }

        /// <summary>
        /// At t1, radius is 0 (the tip)
        /// At t0, radius is Radius (the base)
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        public readonly float GetRadiusAlongAxisNormalized(float t)
            => Interp.Lerp(Radius, 0.0f, t);

        public readonly float GetRadiusAlongAxisAtHeight(float height)
        {
            if (Height <= 1e-8f)
                return 0.0f;
            return GetRadiusAlongAxisNormalized(height / Height);
        }

        private readonly Vector3 AxisDirection
        {
            get
            {
                float upLengthSquared = Up.LengthSquared();
                return upLengthSquared > 1e-12f
                    ? Up / MathF.Sqrt(upLengthSquared)
                    : Vector3.UnitY;
            }
        }

        private readonly Vector3 Tip
            => Center + AxisDirection * MathF.Max(0.0f, Height);

        private readonly bool IsDegenerate
            => Height <= 1e-8f || Radius <= 1e-8f;

        private static Vector3 GetAnyPerpendicular(Vector3 axis)
        {
            Vector3 basis = MathF.Abs(axis.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            return Vector3.Normalize(Vector3.Cross(axis, basis));
        }

        private readonly float RadialDistanceAtPoint(Vector3 point, out float axisDistance)
        {
            Vector3 axisDir = AxisDirection;
            Vector3 toPoint = point - Center;
            axisDistance = Vector3.Dot(toPoint, axisDir);
            Vector3 radial = toPoint - axisDir * axisDistance;
            return radial.Length();
        }

        public readonly Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
        {
            Vector3 axisDir = AxisDirection;
            float height = MathF.Max(0.0f, Height);
            float radius = MathF.Max(0.0f, Radius);

            if (height <= 1e-8f)
            {
                Vector3 toPoint = point - Center;
                float h = Vector3.Dot(toPoint, axisDir);
                Vector3 planar = toPoint - axisDir * h;
                float planarLen = planar.Length();
                if (planarLen <= radius && !clampToEdge)
                    return point;

                Vector3 radialDir = planarLen > 1e-8f ? planar / planarLen : GetAnyPerpendicular(axisDir);
                float clamped = MathF.Min(planarLen, radius);
                return Center + radialDir * clamped;
            }

            Vector3 tip = Center + axisDir * height;
            Vector3 dir = point - Center;
            float axisDistance = Vector3.Dot(dir, axisDir);
            Vector3 radial = dir - axisDir * axisDistance;
            float radialLength = radial.Length();
            Vector3 radialDirOnAxis = radialLength > 1e-8f ? radial / radialLength : GetAnyPerpendicular(axisDir);

            float clampedAxis = Math.Clamp(axisDistance, 0.0f, height);
            float localRadius = radius * (1.0f - (clampedAxis / height));

            Vector3 baseCandidate = Center + radialDirOnAxis * MathF.Min(radialLength, radius);
            Vector3 tipCandidate = tip;
            Vector3 sideCandidate = Center + axisDir * clampedAxis + radialDirOnAxis * localRadius;

            if (!clampToEdge && ContainsPoint(point))
                return point;

            float dBase = Vector3.DistanceSquared(point, baseCandidate);
            float dTip = Vector3.DistanceSquared(point, tipCandidate);
            float dSide = Vector3.DistanceSquared(point, sideCandidate);

            if (dBase <= dTip && dBase <= dSide)
                return baseCandidate;
            if (dTip <= dSide)
                return tipCandidate;
            return sideCandidate;
        }

        public readonly EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
        {
            var corners = box.GetCorners();
            foreach (Vector3 corner in corners)
                if (!ContainsPoint(corner, tolerance))
                    return EContainment.Disjoint;
            return EContainment.Contains;
        }

        public EContainment ContainsSphere(Sphere sphere)
        {
            AABB coneAabb = GetAABB(true);
            EContainment broadPhase = coneAabb.ContainsSphere(sphere);
            if (broadPhase == EContainment.Disjoint)
                return EContainment.Disjoint;

            if (ContainsPoint(sphere.Center))
            {
                Vector3 r = new(sphere.Radius);
                if (ContainsPoint(sphere.Center + new Vector3(r.X, 0.0f, 0.0f)) &&
                    ContainsPoint(sphere.Center - new Vector3(r.X, 0.0f, 0.0f)) &&
                    ContainsPoint(sphere.Center + new Vector3(0.0f, r.Y, 0.0f)) &&
                    ContainsPoint(sphere.Center - new Vector3(0.0f, r.Y, 0.0f)) &&
                    ContainsPoint(sphere.Center + new Vector3(0.0f, 0.0f, r.Z)) &&
                    ContainsPoint(sphere.Center - new Vector3(0.0f, 0.0f, r.Z)))
                    return EContainment.Contains;
                return EContainment.Intersects;
            }

            Vector3 closest = ClosestPoint(sphere.Center, true);
            return Vector3.DistanceSquared(closest, sphere.Center) <= sphere.Radius * sphere.Radius
                ? EContainment.Intersects
                : EContainment.Disjoint;
        }

        public EContainment ContainsCone(Cone cone)
        {
            if (GetAABB(true).ContainsAABB(cone.GetAABB(true)) == EContainment.Disjoint)
                return EContainment.Disjoint;

            Vector3 axisDir = cone.AxisDirection;
            Vector3 tip = cone.Tip;

            Vector3 tangentA = GetAnyPerpendicular(axisDir);
            Vector3 tangentB = Vector3.Normalize(Vector3.Cross(axisDir, tangentA));
            float radius = MathF.Max(0.0f, cone.Radius);

            Vector3[] samplePoints =
            [
                cone.Center,
                tip,
                cone.Center + tangentA * radius,
                cone.Center - tangentA * radius,
                cone.Center + tangentB * radius,
                cone.Center - tangentB * radius,
            ];

            bool allInside = true;
            bool anyInside = false;
            for (int i = 0; i < samplePoints.Length; i++)
            {
                bool inside = ContainsPoint(samplePoints[i]);
                allInside &= inside;
                anyInside |= inside;
            }

            if (allInside)
                return EContainment.Contains;

            if (anyInside)
                return EContainment.Intersects;

            Vector3[] thisSamples =
            [
                Center,
                Tip,
                Center + GetAnyPerpendicular(AxisDirection) * MathF.Max(0.0f, Radius),
            ];

            for (int i = 0; i < thisSamples.Length; i++)
                if (cone.ContainsPoint(thisSamples[i]))
                    return EContainment.Intersects;

            return EContainment.Disjoint;
        }

        public EContainment ContainsCapsule(Capsule shape)
        {
            Vector3 top = shape.GetTopCenterPoint();
            Vector3 bottom = shape.GetBottomCenterPoint();
            float radius = MathF.Max(0.0f, shape.Radius);

            bool topInside = ContainsSphere(new Sphere(top, radius)) == EContainment.Contains;
            bool bottomInside = ContainsSphere(new Sphere(bottom, radius)) == EContainment.Contains;
            if (topInside && bottomInside)
                return EContainment.Contains;

            bool topIntersects = ContainsSphere(new Sphere(top, radius)) != EContainment.Disjoint;
            bool bottomIntersects = ContainsSphere(new Sphere(bottom, radius)) != EContainment.Disjoint;
            if (topIntersects || bottomIntersects)
                return EContainment.Intersects;

            return EContainment.Disjoint;
        }

        public readonly bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
        {
            Vector3 axisDir = AxisDirection;
            float height = MathF.Max(0.0f, Height);
            float radius = MathF.Max(0.0f, Radius);

            Vector3 dir = point - Center;
            float dot = Vector3.Dot(dir, axisDir);
            if (dot < -tolerance || dot > height + tolerance)
                return false;

            if (height <= 1e-8f)
            {
                Vector3 radial0 = dir - axisDir * dot;
                return radial0.LengthSquared() <= (radius + tolerance) * (radius + tolerance);
            }

            float t = Math.Clamp(dot / height, 0.0f, 1.0f);
            float allowedRadius = MathF.Max(0.0f, radius * (1.0f - t)) + tolerance;
            Vector3 radial = dir - axisDir * dot;
            return radial.LengthSquared() <= allowedRadius * allowedRadius;
        }

        public override readonly string ToString()
            => $"Cone (Center: {Center}, Up: {Up}, Height: {Height}, Radius: {Radius})";

        public AABB GetAABB(bool transformed)
        {
            Vector3 axisDir = AxisDirection;
            float height = MathF.Max(0.0f, Height);
            float radius = MathF.Max(0.0f, Radius);

            Vector3 tip = Center + axisDir * height;

            static float RadialExtent(float axisComponent, float radius)
            {
                float orthogonalSquared = 1.0f - axisComponent * axisComponent;
                if (orthogonalSquared < 0.0f)
                    orthogonalSquared = 0.0f;
                return radius * MathF.Sqrt(orthogonalSquared);
            }

            float ex = RadialExtent(axisDir.X, radius);
            float ey = RadialExtent(axisDir.Y, radius);
            float ez = RadialExtent(axisDir.Z, radius);

            Vector3 min = new(
                MathF.Min(tip.X, Center.X - ex),
                MathF.Min(tip.Y, Center.Y - ey),
                MathF.Min(tip.Z, Center.Z - ez));

            Vector3 max = new(
                MathF.Max(tip.X, Center.X + ex),
                MathF.Max(tip.Y, Center.Y + ey),
                MathF.Max(tip.Z, Center.Z + ez));

            return new AABB(min, max);
        }

        public bool IntersectsSegment(Segment segment, out Vector3[] points)
        {
            bool startInside = ContainsPoint(segment.Start);
            bool endInside = ContainsPoint(segment.End);

            if (startInside && endInside)
            {
                points = [segment.Start, segment.End];
                return true;
            }

            const int steps = 32;
            List<Vector3> intersections = new(2);

            static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

            bool prevInside = startInside;
            float prevT = 0.0f;
            Vector3 prevPoint = segment.Start;

            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector3 sample = Lerp(segment.Start, segment.End, t);
                bool inside = ContainsPoint(sample);

                if (inside != prevInside)
                {
                    float a = prevT;
                    float b = t;
                    Vector3 mid = sample;

                    for (int it = 0; it < 10; it++)
                    {
                        float m = (a + b) * 0.5f;
                        mid = Lerp(segment.Start, segment.End, m);
                        bool midInside = ContainsPoint(mid);
                        if (midInside == prevInside)
                            a = m;
                        else
                            b = m;
                    }

                    if (intersections.Count == 0 || Vector3.DistanceSquared(intersections[^1], mid) > 1e-8f)
                        intersections.Add(mid);
                }

                prevInside = inside;
                prevT = t;
                prevPoint = sample;
            }

            if (startInside && (intersections.Count == 0 || Vector3.DistanceSquared(intersections[0], segment.Start) > 1e-8f))
                intersections.Insert(0, segment.Start);
            if (endInside && (intersections.Count == 0 || Vector3.DistanceSquared(intersections[^1], segment.End) > 1e-8f))
                intersections.Add(segment.End);

            points = intersections.ToArray();
            return points.Length > 0;
        }

        public bool IntersectsSegment(Segment segment)
        {
            if (ContainsPoint(segment.Start) || ContainsPoint(segment.End))
                return true;

            if (GetAABB(true).Intersects(new AABB(Vector3.Min(segment.Start, segment.End), Vector3.Max(segment.Start, segment.End))))
            {
                const int steps = 24;
                for (int i = 1; i < steps; i++)
                {
                    float t = i / (float)steps;
                    Vector3 sample = segment.Start + (segment.End - segment.Start) * t;
                    if (ContainsPoint(sample))
                        return true;
                }
            }

            return false;
        }

        public EContainment ContainsBox(Box box)
        {
            Vector3[] corners = box.WorldCorners.ToArray();
            bool allInside = true;
            bool anyInside = false;

            for (int i = 0; i < corners.Length; i++)
            {
                bool inside = ContainsPoint(corners[i]);
                allInside &= inside;
                anyInside |= inside;
            }

            if (allInside)
                return EContainment.Contains;
            if (anyInside)
                return EContainment.Intersects;

            EContainment aabbContainment = GetAABB(true).ContainsAABB(box.GetAABB(true));
            return aabbContainment == EContainment.Disjoint
                ? EContainment.Disjoint
                : EContainment.Intersects;
        }
    }
}
