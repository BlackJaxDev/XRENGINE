using Extensions;
using System.Numerics;
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
            Vector3 colinearPoint = GeoUtil.SegmentClosestColinearPointToPoint(GetBottomCenterPoint(), GetTopCenterPoint(), point);
            if (!clampIfInside && Vector3.Distance(colinearPoint, point) < _radius)
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
            => GeoUtil.SegmentShortestDistanceToPoint(GetBottomCenterPoint(), GetTopCenterPoint(), point) <= _radius;

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
            ESegmentPart part = GeoUtil.GetDistancePointToSegmentPart(startPoint, endPoint, point, out _);
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
            float pointToSegment = GeoUtil.SegmentShortestDistanceToPoint(startPoint, endPoint, sphere.Center);
            float maxDist = sphere.Radius + Radius;
            if (pointToSegment > maxDist)
                return EContainment.Disjoint;
            else if (pointToSegment + sphere.Radius < Radius)
                return EContainment.Contains;
            else
                return EContainment.Intersects;
        }

        public readonly EContainment ContainsCapsule(Capsule capsule)
        {
            //TODO
            return EContainment.Contains;
        }

        public readonly EContainment ContainsCone(Cone cone)
        {
            //TODO
            return EContainment.Contains;
        }

        public readonly EContainment Contains(Box box)
        {
            //TODO
            return EContainment.Contains;
        }

        public readonly EContainment ContainsAABB(AABB box, float tolerance = float.Epsilon)
        {
            //TODO
            return EContainment.Contains;
        }

        public bool ContainedWithin(AABB boundingBox)
        {
            throw new NotImplementedException();
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
            float distance = GeoUtil.SegmentDistanceToSegment(bot, top, segment.Start, segment.End);

            if (distance > _radius)
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
            float distance = GeoUtil.SegmentDistanceToSegment(bot, top, segment.Start, segment.End);

            // If the minimum distance is less than or equal to the radius, they intersect
            return distance <= _radius;
        }

        public EContainment ContainsBox(Box box)
        {
            throw new NotImplementedException();
        }
        #endregion
    }
}
