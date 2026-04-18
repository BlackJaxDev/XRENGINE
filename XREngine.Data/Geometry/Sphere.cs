using XREngine.Extensions;
using System.Numerics;

namespace XREngine.Data.Geometry
{
    public struct Sphere(Vector3 center, float radius) : IShape
    {
        public Vector3 Center = center;
        public float Radius = radius;

        public float Diameter
        {
            readonly get => Radius * 2.0f;
            set => Radius = value / 2.0f;
        }

        public readonly Vector3 ClosestPoint(Vector3 point, bool clampToEdge)
        {
            Vector3 vec = point - Center;
            float length = vec.Length();
            return Center + vec / length * Radius;
        }

        public readonly bool ContainedWithin(AABB boundingBox)
        {
            Vector3 min = boundingBox.Min;
            Vector3 max = boundingBox.Max;
            Vector3 closestPoint = ClosestPoint(min, false);
            if (closestPoint.X < min.X || closestPoint.X > max.X)
                return false;
            if (closestPoint.Y < min.Y || closestPoint.Y > max.Y)
                return false;
            if (closestPoint.Z < min.Z || closestPoint.Z > max.Z)
                return false;
            return true;
        }

        public readonly EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
        {
            float allowedRadius = Radius + tolerance;
            float allowedRadiusSquared = allowedRadius * allowedRadius;

            Vector3 closestPoint = Vector3.Clamp(Center, box.Min, box.Max);
            Vector3 closestDelta = closestPoint - Center;
            if (Vector3.Dot(closestDelta, closestDelta) > allowedRadiusSquared)
                return EContainment.Disjoint;

            Vector3 minDelta = Vector3.Abs(box.Min - Center);
            Vector3 maxDelta = Vector3.Abs(box.Max - Center);
            float farthestX = MathF.Max(minDelta.X, maxDelta.X);
            float farthestY = MathF.Max(minDelta.Y, maxDelta.Y);
            float farthestZ = MathF.Max(minDelta.Z, maxDelta.Z);
            float farthestDistanceSquared =
                farthestX * farthestX +
                farthestY * farthestY +
                farthestZ * farthestZ;

            return farthestDistanceSquared <= allowedRadiusSquared
                ? EContainment.Contains
                : EContainment.Intersects;
        }

        public readonly EContainment ContainsSphere(Sphere sphere)
        {
            Vector3 delta = sphere.Center - Center;
            float distanceSquared = Vector3.Dot(delta, delta);

            float outsideDistance = Radius + sphere.Radius;
            if (distanceSquared > outsideDistance * outsideDistance)
                return EContainment.Disjoint;

            float insideDistance = Radius - sphere.Radius;
            if (insideDistance > 0.0f && distanceSquared < insideDistance * insideDistance)
                return EContainment.Contains;

            return EContainment.Intersects;
        }

        public EContainment ContainsCone(Cone cone)
        {
            float sphereRadius = MathF.Max(0.0f, Radius);
            float coneHeight = MathF.Max(0.0f, cone.Height);
            float coneRadius = MathF.Max(0.0f, cone.Radius);

            Vector3 up = cone.Up;
            float upLengthSquared = up.LengthSquared();
            Vector3 axisDir = upLengthSquared > 1e-12f
                ? up / MathF.Sqrt(upLengthSquared)
                : Vector3.UnitY;

            Vector3 baseCenter = cone.Center;
            Vector3 tip = baseCenter + axisDir * coneHeight;

            float sphereRadiusSquared = sphereRadius * sphereRadius;

            Vector3 tipDelta = tip - Center;
            float tipDistanceSquared = tipDelta.LengthSquared();

            Vector3 baseDelta = baseCenter - Center;
            float baseParallel = Vector3.Dot(baseDelta, axisDir);
            Vector3 basePerp = baseDelta - axisDir * baseParallel;
            float basePerpLength = basePerp.Length();
            float maxBaseDistanceSquared = baseParallel * baseParallel + (basePerpLength + coneRadius) * (basePerpLength + coneRadius);

            if (tipDistanceSquared <= sphereRadiusSquared && maxBaseDistanceSquared <= sphereRadiusSquared)
                return EContainment.Contains;

            if (ContainsPoint(baseCenter) || ContainsPoint(tip))
                return EContainment.Intersects;

            Vector3 tangentA = MathF.Abs(axisDir.Y) < 0.99f
                ? Vector3.Normalize(Vector3.Cross(axisDir, Vector3.UnitY))
                : Vector3.Normalize(Vector3.Cross(axisDir, Vector3.UnitX));
            Vector3 tangentB = Vector3.Normalize(Vector3.Cross(axisDir, tangentA));

            if (coneRadius > 0.0f)
            {
                if (ContainsPoint(baseCenter + tangentA * coneRadius) ||
                    ContainsPoint(baseCenter - tangentA * coneRadius) ||
                    ContainsPoint(baseCenter + tangentB * coneRadius) ||
                    ContainsPoint(baseCenter - tangentB * coneRadius))
                    return EContainment.Intersects;
            }

            Vector3 closest = cone.ClosestPoint(Center, true);
            return Vector3.DistanceSquared(closest, Center) <= sphereRadiusSquared
                ? EContainment.Intersects
                : EContainment.Disjoint;
        }

        public EContainment ContainsCapsule(Capsule shape)
        {
            float sphereRadius = MathF.Max(0.0f, Radius);
            float capsuleRadius = MathF.Max(0.0f, shape.Radius);

            Vector3 top = shape.GetTopCenterPoint();
            Vector3 bottom = shape.GetBottomCenterPoint();

            static float SegmentDistanceSquaredToPoint(Vector3 start, Vector3 end, Vector3 point)
            {
                Vector3 segment = end - start;
                float segmentLengthSquared = Vector3.Dot(segment, segment);
                if (segmentLengthSquared <= 1e-12f)
                {
                    Vector3 degenerateDelta = point - start;
                    return Vector3.Dot(degenerateDelta, degenerateDelta);
                }

                float t = Math.Clamp(Vector3.Dot(point - start, segment) / segmentLengthSquared, 0.0f, 1.0f);
                Vector3 closest = start + segment * t;
                Vector3 delta = point - closest;
                return Vector3.Dot(delta, delta);
            }

            float minDistanceSquaredToAxis = SegmentDistanceSquaredToPoint(bottom, top, Center);
            float maxIntersectDistance = sphereRadius + capsuleRadius;
            if (minDistanceSquaredToAxis > maxIntersectDistance * maxIntersectDistance)
                return EContainment.Disjoint;

            Vector3 topDelta = top - Center;
            Vector3 bottomDelta = bottom - Center;
            float topDistanceSquared = Vector3.Dot(topDelta, topDelta);
            float bottomDistanceSquared = Vector3.Dot(bottomDelta, bottomDelta);
            float maxDistanceSquaredToAxis = MathF.Max(topDistanceSquared, bottomDistanceSquared);

            float containsDistance = sphereRadius - capsuleRadius;
            return containsDistance > 0.0f && maxDistanceSquaredToAxis <= containsDistance * containsDistance
                ? EContainment.Contains
                : EContainment.Intersects;
        }

        public readonly bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
        {
            Vector3 vec = point - Center;
            float distanceSquared = Vector3.Dot(vec, vec);
            float allowedRadius = Radius + tolerance;
            return distanceSquared <= allowedRadius * allowedRadius;
        }

        public readonly AABB GetAABB(bool transformed) 
            => new(Center - new Vector3(Radius), Center + new Vector3(Radius));

        public readonly bool IntersectsSegment(Segment segment, out Vector3[] points)
        {
            Vector3 direction = segment.End - segment.Start;
            Vector3 diff = segment.Start - Center;
            float a = Vector3.Dot(direction, direction);
            float b = 2.0f * Vector3.Dot(diff, direction);
            float c = Vector3.Dot(diff, diff) - Radius * Radius;
            float discriminant = b * b - 4.0f * a * c;
            if (discriminant < 0)
            {
                points = [];
                return false;
            }
            float t1 = (-b + MathF.Sqrt(discriminant)) / (2.0f * a);
            float t2 = (-b - MathF.Sqrt(discriminant)) / (2.0f * a);
            points = [segment.Start + t1 * direction, segment.Start + t2 * direction];
            return true;
        }

        public readonly bool IntersectsSegment(Segment segment)
        {
            Vector3 direction = segment.End - segment.Start;
            Vector3 diff = segment.Start - Center;
            float a = Vector3.Dot(direction, direction);
            float b = 2.0f * Vector3.Dot(diff, direction);
            float c = Vector3.Dot(diff, diff) - Radius * Radius;
            float discriminant = b * b - 4.0f * a * c;
            return discriminant >= 0;
        }

        public override readonly string ToString()
            => $"Sphere (Center: {Center}, Radius: {Radius})";

        public readonly EContainment ContainsBox(Box box)
        {
            Vector3 min = box.LocalMinimum;
            Vector3 max = box.LocalMaximum;
            var wtl = box.Transform.Inverted();
            Vector3 localCenter = Vector3.Transform(Center, wtl);
            if (localCenter.X - Radius > max.X || localCenter.X + Radius < min.X || 
                localCenter.Y - Radius > max.Y || localCenter.Y + Radius < min.Y || 
                localCenter.Z - Radius > max.Z || localCenter.Z + Radius < min.Z)
                return EContainment.Disjoint;
            if (localCenter.X - Radius < min.X && localCenter.X + Radius > max.X &&
                localCenter.Y - Radius < min.Y && localCenter.Y + Radius > max.Y &&
                localCenter.Z - Radius < min.Z && localCenter.Z + Radius > max.Z)
                return EContainment.Contains;
            return EContainment.Intersects;
        }
    }
}
