using Extensions;
using System.Numerics;

namespace XREngine.Data.Geometry
{
    public static partial class GeoUtil
    {
        /// <summary>
        /// Scalar distance queries. Usage reads as "distance from X to Y".
        /// </summary>
        public static class DistanceFrom
        {
            public static float PlaneToPoint(Plane plane, Vector3 point)
                => PlaneToPoint(plane.Normal, plane.D, point);

            public static float PlaneToPoint(Vector3 planeNormal, float planeOriginDistance, Vector3 point)
                => Vector3.Dot(planeNormal, point) + planeOriginDistance;

            public static float PlaneToPoint(Vector3 normal, Vector3 planePoint, Vector3 point)
                => Vector3.Dot(normal, point) + Vector3.Dot(planePoint, normal);

            public static float AABBToPoint(Vector3 min, Vector3 max, Vector3 point)
            {
                Vector3 clamped = Vector3.Clamp(point, min, max);
                Vector3 delta = point - clamped;
                return MathF.Sqrt(Vector3.Dot(delta, delta));
            }

            /// <summary>
            /// Determines the distance between a <see cref="AABB"/> and a <see cref="AABB"/>.
            /// </summary>
            public static float AABBToAABB(Vector3 box1Min, Vector3 box1Max, Vector3 box2Min, Vector3 box2Max)
            {
                Vector3 sepA = Vector3.Max(box1Min - box2Max, Vector3.Zero);
                Vector3 sepB = Vector3.Max(box2Min - box1Max, Vector3.Zero);
                Vector3 separation = sepA + sepB;
                return MathF.Sqrt(Vector3.Dot(separation, separation));
            }

            public static float SphereToPoint(Vector3 sphereCenter, float sphereRadius, Vector3 point)
            {
                Vector3 delta = point - sphereCenter;
                float distance = MathF.Sqrt(Vector3.Dot(delta, delta));
                return (distance - sphereRadius).ClampMin(0.0f);
            }

            public static float SphereToSphere(float sphere1Radius, Vector3 sphere1Pos, float sphere2Radius, Vector3 sphere2Pos)
            {
                Vector3 delta = sphere2Pos - sphere1Pos;
                float distance = MathF.Sqrt(Vector3.Dot(delta, delta));
                return Math.Max(distance - sphere1Radius - sphere2Radius, 0f);
            }

            public static float SegmentToPoint(Vector3 start, Vector3 end, Vector3 point)
            {
                Vector3 v = end - start;
                float lengthSquared = Vector3.Dot(v, v);
                if (lengthSquared <= 1e-12f)
                {
                    Vector3 degenerateDelta = point - start;
                    return MathF.Sqrt(Vector3.Dot(degenerateDelta, degenerateDelta));
                }

                float t = Math.Clamp(Vector3.Dot(point - start, v) / lengthSquared, 0.0f, 1.0f);
                Vector3 closest = start + v * t;
                Vector3 delta = point - closest;
                return MathF.Sqrt(Vector3.Dot(delta, delta));
            }

            public static float SegmentToSegment(Vector3 s1Start, Vector3 s1End, Vector3 s2Start, Vector3 s2End)
            {
                Vector3 u = s1End - s1Start;
                Vector3 v = s2End - s2Start;
                Vector3 w = s1Start - s2Start;
                float a = Vector3.Dot(u, u); // always >= 0
                float b = Vector3.Dot(u, v);
                float c = Vector3.Dot(v, v); // always >= 0
                float d = Vector3.Dot(u, w);
                float e = Vector3.Dot(v, w);
                float D = a * c - b * b; // always >= 0
                // compute the line parameters of the two closest points
                float sc, tc;
                if (D < 1e-8f) // the lines are almost parallel
                {
                    sc = 0.0f;
                    tc = (b > c ? d / b : e / c); // use the largest denominator
                }
                else
                {
                    sc = (b * e - c * d) / D;
                    tc = (a * e - b * d) / D;
                }
                // get the difference of the two closest points
                Vector3 dP = w + (sc * u) - (tc * v); // = S1(sc) - S2(tc)
                return dP.LengthSquared();
            }

            public static Capsule.ESegmentPart PointToSegmentPart(Vector3 startPoint, Vector3 endPoint, Vector3 point, out float closestPartDist)
            {
                Vector3 ab = endPoint - startPoint;
                Vector3 ac = point - startPoint;
                float e = Vector3.Dot(ac, ab);
                if (e <= 0.0f)
                {
                    closestPartDist = 0.0f;
                    return Capsule.ESegmentPart.Start;
                }

                float f = Vector3.Dot(ab, ab);
                if (e >= f)
                {
                    closestPartDist = f;
                    return Capsule.ESegmentPart.End;
                }

                closestPartDist = e;
                return Capsule.ESegmentPart.Middle;
            }
        }
    }
}
