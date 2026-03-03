using Extensions;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Core;

namespace XREngine.Data.Geometry
{
    public struct Capsule : IShape
    {
        private Vector3 _center;
        private Vector3 _upAxis;
        private float _radius;
        private float _halfHeight;

        public Vector3 Center
        {
            readonly get => _center;
            set => _center = value;
        }
        public Vector3 UpAxis
        {
            readonly get => _upAxis;
            set => _upAxis = value.Normalized();
        }
        public float Radius
        {
            readonly get => _radius;
            set => _radius = value;
        }
        public float HalfHeight
        {
            readonly get => _halfHeight;
            set => _halfHeight = value;
        }

        public readonly Matrix4x4 CreateTransform()
        {
            Vector3 arb = Vector3.UnitX;
            if (Vector3.Dot(UpAxis, Vector3.UnitX) > 0.99f || Vector3.Dot(UpAxis, Vector3.UnitX) < -0.99f)
                arb = Vector3.UnitZ;
            Vector3 perp = Vector3.Cross(UpAxis, arb).Normalized();
            return Matrix4x4.CreateWorld(Center, UpAxis, Vector3.Cross(UpAxis, perp));
        }

        public readonly Vector3 WorldToLocal(Vector3 worldPoint)
            => Vector3.Transform(worldPoint, CreateTransform().Inverted());

        public readonly Vector3 LocalToWorld(Vector3 localPoint)
            => Vector3.Transform(localPoint, CreateTransform());

        public Capsule(Vector3 upAxis, float radius, float halfHeight)
        {
            UpAxis = upAxis;
            Radius = radius;
            HalfHeight = halfHeight;
        }
        public Capsule(Vector3 center, Vector3 upAxis, float radius, float halfHeight)
        {
            Center = center;
            UpAxis = upAxis;
            Radius = radius;
            HalfHeight = halfHeight;
        }

        public readonly Sphere GetTopSphere()
            => new(GetTopCenterPoint(), Radius);
        public readonly Sphere GetBottomSphere()
            => new(GetBottomCenterPoint(), Radius);

        public readonly Vector3 GetTopCenterPoint()
            => Center + UpAxis * HalfHeight;
        public readonly Vector3 GetBottomCenterPoint()
            => Center - UpAxis * HalfHeight;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 ClosestPointOnSegment(Vector3 start, Vector3 end, Vector3 point)
        {
            Vector3 segment = end - start;
            float lengthSquared = Vector3.Dot(segment, segment);
            if (lengthSquared <= 1e-12f)
                return start;

            float t = Math.Clamp(Vector3.Dot(point - start, segment) / lengthSquared, 0.0f, 1.0f);
            return start + segment * t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SegmentDistanceSquaredToPoint(Vector3 start, Vector3 end, Vector3 point)
        {
            Vector3 closest = ClosestPointOnSegment(start, end, point);
            Vector3 delta = point - closest;
            return Vector3.Dot(delta, delta);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ESegmentPart GetSegmentPart(Vector3 start, Vector3 end, Vector3 point)
        {
            Vector3 segment = end - start;
            float projection = Vector3.Dot(point - start, segment);
            if (projection <= 0.0f)
                return ESegmentPart.Start;

            float lengthSquared = Vector3.Dot(segment, segment);
            if (projection >= lengthSquared)
                return ESegmentPart.End;

            return ESegmentPart.Middle;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float DistanceSquaredToAabb(Vector3 point, Vector3 min, Vector3 max)
        {
            Vector3 clamped = Vector3.Clamp(point, min, max);
            Vector3 delta = point - clamped;
            return Vector3.Dot(delta, delta);
        }

        private static bool SegmentIntersectsAabb(Vector3 segmentStart, Vector3 segmentEnd, Vector3 boxMin, Vector3 boxMax)
        {
            Vector3 direction = segmentEnd - segmentStart;
            float tMin = 0.0f;
            float tMax = 1.0f;

            for (int axis = 0; axis < 3; axis++)
            {
                float start = segmentStart[axis];
                float delta = direction[axis];
                float min = boxMin[axis];
                float max = boxMax[axis];

                if (MathF.Abs(delta) <= 1e-12f)
                {
                    if (start < min || start > max)
                        return false;
                    continue;
                }

                float invDelta = 1.0f / delta;
                float t1 = (min - start) * invDelta;
                float t2 = (max - start) * invDelta;
                if (t1 > t2)
                    (t1, t2) = (t2, t1);

                tMin = MathF.Max(tMin, t1);
                tMax = MathF.Min(tMax, t2);
                if (tMin > tMax)
                    return false;
            }

            return true;
        }

        private static float SegmentDistanceSquared(Vector3 s1Start, Vector3 s1End, Vector3 s2Start, Vector3 s2End)
        {
            const float epsilon = 1e-8f;
            Vector3 u = s1End - s1Start;
            Vector3 v = s2End - s2Start;
            Vector3 w = s1Start - s2Start;

            float a = Vector3.Dot(u, u);
            float b = Vector3.Dot(u, v);
            float c = Vector3.Dot(v, v);
            float d = Vector3.Dot(u, w);
            float e = Vector3.Dot(v, w);
            float denominator = a * c - b * b;

            float sN;
            float sD = denominator;
            float tN;
            float tD = denominator;

            if (denominator < epsilon)
            {
                sN = 0.0f;
                sD = 1.0f;
                tN = e;
                tD = c;
            }
            else
            {
                sN = b * e - c * d;
                tN = a * e - b * d;

                if (sN < 0.0f)
                {
                    sN = 0.0f;
                    tN = e;
                    tD = c;
                }
                else if (sN > sD)
                {
                    sN = sD;
                    tN = e + b;
                    tD = c;
                }
            }

            if (tN < 0.0f)
            {
                tN = 0.0f;
                if (-d < 0.0f)
                    sN = 0.0f;
                else if (-d > a)
                    sN = sD;
                else
                {
                    sN = -d;
                    sD = a;
                }
            }
            else if (tN > tD)
            {
                tN = tD;
                if (-d + b < 0.0f)
                    sN = 0.0f;
                else if (-d + b > a)
                    sN = sD;
                else
                {
                    sN = -d + b;
                    sD = a;
                }
            }

            float sc = MathF.Abs(sN) < epsilon ? 0.0f : sN / sD;
            float tc = MathF.Abs(tN) < epsilon ? 0.0f : tN / tD;
            Vector3 dP = w + sc * u - tc * v;
            return Vector3.Dot(dP, dP);
        }

        /// <summary>
        /// Returns the closest point on this shape to the given point.
        /// </summary>
        /// <param name="point">The point determine closeness with.</param>
        public readonly Vector3 ClosestPoint(Vector3 point)
            => ClosestPoint(point, false);

        /// <summary>
        /// Returns the closest point on this shape to the given point.
        /// </summary>
        /// <param name="point">The point determine closeness with.</param>
        /// <param name="clampIfInside">If true, finds closest edge point even if the given point is inside the capsule. Otherwise, just returns the given point if it is inside.</param>
        public readonly Vector3 ClosestPoint(Vector3 point, bool clampIfInside)
        {
            Vector3 bottom = GetBottomCenterPoint();
            Vector3 top = GetTopCenterPoint();
            Vector3 colinearPoint = ClosestPointOnSegment(bottom, top, point);
            float radiusSquared = _radius * _radius;
            if (!clampIfInside && Vector3.DistanceSquared(colinearPoint, point) < radiusSquared)
                return point;
            return Ray.PointAtLineDistance(colinearPoint, point, _radius);
        }

        public readonly AABB GetAABB(bool includeCenterTranslation)
            => GetAABB(includeCenterTranslation, false, out _);
        public readonly AABB GetAABB(bool includeCenterTranslation, bool alignToUp, out Quaternion dirToUp)
        {
            Vector3 top = GetTopCenterPoint();
            Vector3 bot = GetBottomCenterPoint();
            if (!includeCenterTranslation)
            {
                top -= Center;
                bot -= Center;
            }
            if (alignToUp)
            {
                Vector3 dir = (top - bot).Normalized();
                dirToUp = Quaternion.Normalize(XRMath.RotationBetweenVectors(dir, Globals.Up));
                top = Vector3.Transform(top, dirToUp);
                bot = Vector3.Transform(bot, dirToUp);
            }
            else
                dirToUp = Quaternion.Identity;
            Vector3 radius = new(MathF.Abs(Radius));
            return new(
                Vector3.Min(top, bot) - radius,
                Vector3.Max(top, bot) + radius);
        }

        #region Containment

        public readonly bool ContainsPoint(Vector3 point, float tolerance = float.Epsilon)
        {
            Vector3 bottom = GetBottomCenterPoint();
            Vector3 top = GetTopCenterPoint();
            float distanceSquared = SegmentDistanceSquaredToPoint(bottom, top, point);
            float radius = MathF.Max(0.0f, _radius) + MathF.Max(0.0f, tolerance);
            return distanceSquared <= radius * radius;
        }

        public enum ESegmentPart
        {
            Start,
            End,
            Middle,
        }

        public readonly Vector3 ClosestPointTo(Vector3 point)
        {
            Vector3 startPoint = GetBottomCenterPoint();
            Vector3 endPoint = GetTopCenterPoint();
            ESegmentPart part = GetSegmentPart(startPoint, endPoint, point);
            return part switch
            {
                ESegmentPart.Start => Ray.PointAtLineDistance(startPoint, point, _radius),
                ESegmentPart.End => Ray.PointAtLineDistance(endPoint, point, _radius),
                ESegmentPart.Middle => Ray.GetPerpendicularVectorFromPoint(startPoint, endPoint - startPoint, point),
                _ => throw new Exception(),
            };
        }

        public readonly Vector3 ClosestPointTo(Sphere sphere)
            => ClosestPointTo(sphere.Center);

        public readonly EContainment ContainsSphere(Sphere sphere)
        {
            Vector3 startPoint = GetBottomCenterPoint();
            Vector3 endPoint = GetTopCenterPoint();
            float pointToSegmentSquared = SegmentDistanceSquaredToPoint(startPoint, endPoint, sphere.Center);
            float maxDist = sphere.Radius + Radius;
            if (pointToSegmentSquared > maxDist * maxDist)
                return EContainment.Disjoint;

            float allowance = Radius - sphere.Radius;
            if (allowance > 0.0f && pointToSegmentSquared < allowance * allowance)
                return EContainment.Contains;

            return EContainment.Intersects;
        }

        public readonly EContainment ContainsCapsule(Capsule capsule)
        {
            Vector3 thisStart = GetBottomCenterPoint();
            Vector3 thisEnd = GetTopCenterPoint();
            Vector3 otherStart = capsule.GetBottomCenterPoint();
            Vector3 otherEnd = capsule.GetTopCenterPoint();

            float thisRadius = MathF.Max(0.0f, Radius);
            float otherRadius = MathF.Max(0.0f, capsule.Radius);

            float sumRadii = thisRadius + otherRadius;
            float segmentDistanceSquared = SegmentDistanceSquared(thisStart, thisEnd, otherStart, otherEnd);
            if (segmentDistanceSquared > sumRadii * sumRadii)
                return EContainment.Disjoint;

            if (thisRadius >= otherRadius)
            {
                float allowance = thisRadius - otherRadius;
                float allowanceSquared = allowance * allowance;
                float startDistSquared = SegmentDistanceSquaredToPoint(thisStart, thisEnd, otherStart);
                float endDistSquared = SegmentDistanceSquaredToPoint(thisStart, thisEnd, otherEnd);
                if (startDistSquared <= allowanceSquared && endDistSquared <= allowanceSquared)
                    return EContainment.Contains;
            }

            return EContainment.Intersects;
        }

        public readonly EContainment ContainsCone(Cone cone)
        {
            Vector3 capStart = GetBottomCenterPoint();
            Vector3 capEnd = GetTopCenterPoint();
            float capRadius = MathF.Max(0.0f, Radius);

            Vector3 coneBase = cone.Center;
            Vector3 coneUp = cone.Up;
            float coneHeight = MathF.Max(0.0f, cone.Height);
            float coneRadius = MathF.Max(0.0f, cone.Radius);

            float upLengthSquared = coneUp.LengthSquared();
            Vector3 coneAxis = upLengthSquared > 1e-12f
                ? coneUp / MathF.Sqrt(upLengthSquared)
                : Vector3.UnitY;
            Vector3 coneTip = coneBase + coneAxis * coneHeight;

            AABB capsuleAabb = GetAABB(true);
            if (!capsuleAabb.Intersects(cone.GetAABB(true)))
                return EContainment.Disjoint;

            static Vector3 AnyPerpendicular(Vector3 axis)
            {
                Vector3 basis = MathF.Abs(axis.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
                return Vector3.Normalize(Vector3.Cross(axis, basis));
            }

            Vector3 tangentA = AnyPerpendicular(coneAxis);
            Vector3 tangentB = Vector3.Normalize(Vector3.Cross(coneAxis, tangentA));

            Vector3[] coneContainmentSamples =
            [
                coneTip,
                coneBase,
                coneBase + tangentA * coneRadius,
                coneBase - tangentA * coneRadius,
                coneBase + tangentB * coneRadius,
                coneBase - tangentB * coneRadius,
            ];

            bool allInside = true;
            bool anyInside = false;
            for (int i = 0; i < coneContainmentSamples.Length; i++)
            {
                bool inside = ContainsPoint(coneContainmentSamples[i]);
                allInside &= inside;
                anyInside |= inside;
            }

            if (allInside)
                return EContainment.Contains;

            if (anyInside)
                return EContainment.Intersects;

            if (cone.ContainsPoint(capStart) || cone.ContainsPoint(capEnd))
                return EContainment.Intersects;

            const int axisSamples = 24;
            for (int i = 0; i <= axisSamples; i++)
            {
                float t = i / (float)axisSamples;
                Vector3 axisPoint = coneBase + (coneTip - coneBase) * t;
                float coneRadiusAtPoint = coneRadius * (1.0f - t);
                float distanceSquaredToCapsuleAxis = SegmentDistanceSquaredToPoint(capStart, capEnd, axisPoint);
                float maxDistance = capRadius + coneRadiusAtPoint;
                if (distanceSquaredToCapsuleAxis <= maxDistance * maxDistance)
                    return EContainment.Intersects;
            }

            return EContainment.Disjoint;
        }

        public readonly EContainment Contains(Box box)
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

            AABB capsuleAabb = GetAABB(true);
            AABB boxAabb = box.GetAABB(true);
            if (!capsuleAabb.Intersects(boxAabb))
                return EContainment.Disjoint;

            Vector3 axisStart = GetBottomCenterPoint();
            Vector3 axisEnd = GetTopCenterPoint();
            if (box.ContainsPoint(axisStart) || box.ContainsPoint(axisEnd) || box.Intersects(new Segment(axisStart, axisEnd)))
                return EContainment.Intersects;

            float radius = MathF.Max(0.0f, Radius);
            const int samples = 24;
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                Vector3 axisPoint = axisStart + (axisEnd - axisStart) * t;
                if (DistanceSquaredToAabb(axisPoint, boxAabb.Min, boxAabb.Max) <= radius * radius)
                    return EContainment.Intersects;
            }

            return EContainment.Disjoint;
        }

        public readonly EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
        {
            Vector3[] corners = box.GetCorners();
            bool allInside = true;
            bool anyInside = false;
            for (int i = 0; i < corners.Length; i++)
            {
                bool inside = ContainsPoint(corners[i], tolerance);
                allInside &= inside;
                anyInside |= inside;
            }

            if (allInside)
                return EContainment.Contains;
            if (anyInside)
                return EContainment.Intersects;

            AABB capsuleAabb = GetAABB(true);
            if (!capsuleAabb.Intersects(box))
                return EContainment.Disjoint;

            Vector3 axisStart = GetBottomCenterPoint();
            Vector3 axisEnd = GetTopCenterPoint();
            if (box.ContainsPoint(axisStart, tolerance) || box.ContainsPoint(axisEnd, tolerance) ||
                SegmentIntersectsAabb(axisStart, axisEnd, box.Min, box.Max))
                return EContainment.Intersects;

            float radius = MathF.Max(0.0f, Radius);
            float maxDistance = radius + tolerance;
            float maxDistanceSquared = maxDistance * maxDistance;
            const int samples = 24;
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                Vector3 axisPoint = axisStart + (axisEnd - axisStart) * t;
                if (DistanceSquaredToAabb(axisPoint, box.Min, box.Max) <= maxDistanceSquared)
                    return EContainment.Intersects;
            }

            return EContainment.Disjoint;
        }

        public bool ContainedWithin(AABB boundingBox)
        {
            float radius = MathF.Max(0.0f, Radius);
            Vector3 shrunkMin = boundingBox.Min + new Vector3(radius);
            Vector3 shrunkMax = boundingBox.Max - new Vector3(radius);

            if (shrunkMin.X > shrunkMax.X || shrunkMin.Y > shrunkMax.Y || shrunkMin.Z > shrunkMax.Z)
                return false;

            Vector3 top = GetTopCenterPoint();
            Vector3 bottom = GetBottomCenterPoint();

            static bool Inside(Vector3 point, Vector3 min, Vector3 max)
                => point.X >= min.X && point.X <= max.X &&
                   point.Y >= min.Y && point.Y <= max.Y &&
                   point.Z >= min.Z && point.Z <= max.Z;

            return Inside(top, shrunkMin, shrunkMax) && Inside(bottom, shrunkMin, shrunkMax);
        }

        public readonly bool IntersectsSegment(Segment segment, out Vector3[] intersectingPoints)
        {
            Vector3 top = GetTopCenterPoint();
            Vector3 bot = GetBottomCenterPoint();
            Vector3 capsuleDir = top - bot;
            Vector3 segmentDir = segment.End - segment.Start;

            // First check if either endpoint is inside the capsule
            bool startInside = ContainsPoint(segment.Start);
            bool endInside = ContainsPoint(segment.End);

            if (startInside && endInside)
            {
                // Both endpoints are inside - the whole segment is inside
                intersectingPoints = [segment.Start, segment.End];
                return true;
            }

            // Calculate the minimum distance between the segment and the capsule's axis
            float distanceSquared = SegmentDistanceSquared(bot, top, segment.Start, segment.End);
            float radiusSquared = _radius * _radius;

            if (distanceSquared > radiusSquared)
            {
                // No intersection if the closest distance exceeds the radius
                intersectingPoints = [];
                return false;
            }

            // At this point, we know the segment intersects the capsule
            // We need to find the actual intersection points
            List<Vector3> points = new(2);

            // Add any endpoints that are inside the capsule
            if (startInside)
                points.Add(segment.Start);
            if (endInside)
                points.Add(segment.End);

            // If we don't have 2 points yet, we need to find the actual intersections
            if (points.Count < 2)
            {
                // Check intersection with the top hemisphere
                Sphere topSphere = GetTopSphere();
                if (topSphere.IntersectsSegment(segment, out Vector3[] topIntersections))
                {
                    foreach (var point in topIntersections)
                    {
                        // Only add points that lie in the hemisphere facing away from the capsule axis
                        if (Vector3.Dot(point - top, capsuleDir) >= 0)
                        {
                            points.Add(point);
                        }
                    }
                }

                // Check intersection with the bottom hemisphere
                Sphere bottomSphere = GetBottomSphere();
                if (bottomSphere.IntersectsSegment(segment, out Vector3[] bottomIntersections))
                {
                    foreach (var point in bottomIntersections)
                    {
                        // Only add points that lie in the hemisphere facing away from the capsule axis
                        if (Vector3.Dot(point - bot, capsuleDir) <= 0)
                        {
                            points.Add(point);
                        }
                    }
                }

                // Check intersection with the capsule's cylindrical part
                // We need to find where the segment intersects an infinite cylinder along the capsule axis
                Vector3 capAxis = capsuleDir.Normalized();
                Vector3 w = segment.Start - bot;

                // Calculate the coefficients for the quadratic equation
                Vector3 a = segmentDir - (Vector3.Dot(segmentDir, capAxis) * capAxis);
                Vector3 b = w - (Vector3.Dot(w, capAxis) * capAxis);

                float A = Vector3.Dot(a, a);
                float B = 2 * Vector3.Dot(a, b);
                float C = Vector3.Dot(b, b) - (_radius * _radius);

                // Solve the quadratic equation
                if (A > float.Epsilon) // Non-parallel case
                {
                    float discriminant = B * B - 4 * A * C;

                    if (discriminant >= 0)
                    {
                        float sqrtD = MathF.Sqrt(discriminant);
                        float t1 = (-B - sqrtD) / (2 * A);
                        float t2 = (-B + sqrtD) / (2 * A);

                        // Check if the intersection points are within the segment bounds
                        if (t1 >= 0 && t1 <= 1)
                        {
                            Vector3 p1 = segment.Start + t1 * segmentDir;

                            // Check if the point is within the cylindrical part of the capsule
                            float axisProj = Vector3.Dot(p1 - bot, capAxis);
                            if (axisProj >= 0 && axisProj <= capsuleDir.Length())
                            {
                                points.Add(p1);
                            }
                        }

                        if (t2 >= 0 && t2 <= 1)
                        {
                            Vector3 p2 = segment.Start + t2 * segmentDir;

                            // Check if the point is within the cylindrical part of the capsule
                            float axisProj = Vector3.Dot(p2 - bot, capAxis);
                            if (axisProj >= 0 && axisProj <= capsuleDir.Length())
                            {
                                points.Add(p2);
                            }
                        }
                    }
                }
            }

            // Remove duplicate points (within a small tolerance)
            if (points.Count > 1)
            {
                for (int i = points.Count - 1; i > 0; i--)
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (Vector3.DistanceSquared(points[i], points[j]) < 1e-6f)
                        {
                            points.RemoveAt(i);
                            break;
                        }
                    }
                }
            }

            // Sort points along the segment direction for consistency
            points.Sort((a, b) =>
                Vector3.Dot(a - segment.Start, segmentDir).CompareTo(
                Vector3.Dot(b - segment.Start, segmentDir)));

            intersectingPoints = points.ToArray();
            return intersectingPoints.Length > 0;
        }

        public readonly bool IntersectsSegment(Segment segment)
        {
            Vector3 top = GetTopCenterPoint();
            Vector3 bot = GetBottomCenterPoint();

            // Find minimum distance between capsule axis segment and query segment
            float distanceSquared = SegmentDistanceSquared(bot, top, segment.Start, segment.End);
            float radiusSquared = _radius * _radius;

            // If the minimum distance is less than or equal to the radius, they intersect
            return distanceSquared <= radiusSquared;
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

            AABB capsuleAabb = GetAABB(true);
            AABB boxAabb = box.GetAABB(true);
            if (!capsuleAabb.Intersects(boxAabb))
                return EContainment.Disjoint;

            Vector3 axisStart = GetBottomCenterPoint();
            Vector3 axisEnd = GetTopCenterPoint();
            if (box.ContainsPoint(axisStart) || box.ContainsPoint(axisEnd) || box.Intersects(new Segment(axisStart, axisEnd)))
                return EContainment.Intersects;

            float radius = MathF.Max(0.0f, Radius);
            const int samples = 24;
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                Vector3 axisPoint = axisStart + (axisEnd - axisStart) * t;
                if (DistanceSquaredToAabb(axisPoint, boxAabb.Min, boxAabb.Max) <= radius * radius)
                    return EContainment.Intersects;
            }

            return EContainment.Disjoint;
        }
        #endregion
    }
}
