using System.Numerics;

namespace XREngine.Data.Geometry
{
    public static partial class GeoUtil
    {
        /// <summary>
        /// Closest-point queries. Usage reads as "nearest point on X".
        /// </summary>
        public static class Nearest
        {
            /// <summary>
            /// Determines the closest point between a point and a triangle.
            /// </summary>
            /// <param name="point">The point to test.</param>
            /// <param name="vertex1">The first vertex to test.</param>
            /// <param name="vertex2">The second vertex to test.</param>
            /// <param name="vertex3">The third vertex to test.</param>
            public static Vector3 PointOnTriangle(Vector3 point, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3)
            {
                //Source: Real-Time Collision Detection by Christer Ericson
                //Reference: Page 136

                //Check if P in vertex region outside A
                Vector3 ab = vertex2 - vertex1;
                Vector3 ac = vertex3 - vertex1;
                Vector3 ap = point - vertex1;

                float d1 = Vector3.Dot(ab, ap);
                float d2 = Vector3.Dot(ac, ap);
                if (d1 <= 0.0f && d2 <= 0.0f)
                    return vertex1; //Barycentric coordinates (1,0,0)

                //Check if P in vertex region outside B
                Vector3 bp = point - vertex2;
                float d3 = Vector3.Dot(ab, bp);
                float d4 = Vector3.Dot(ac, bp);
                if (d3 >= 0.0f && d4 <= d3)
                    return vertex2; // Barycentric coordinates (0,1,0)

                //Check if P in edge region of AB, if so return projection of P onto AB
                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f)
                {
                    float v = d1 / (d1 - d3);
                    return vertex1 + v * ab; //Barycentric coordinates (1-v,v,0)
                }

                //Check if P in vertex region outside C
                Vector3 cp = point - vertex3;
                float d5 = Vector3.Dot(ab, cp);
                float d6 = Vector3.Dot(ac, cp);
                if (d6 >= 0.0f && d5 <= d6)
                    return vertex3; //Barycentric coordinates (0,0,1)

                //Check if P in edge region of AC, if so return projection of P onto AC
                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f)
                {
                    float w = d2 / (d2 - d6);
                    return vertex1 + w * ac; //Barycentric coordinates (1-w,0,w)
                }

                //Check if P in edge region of BC, if so return projection of P onto BC
                float va = d3 * d6 - d5 * d4;
                if (va <= 0.0f && (d4 - d3) >= 0.0f && (d5 - d6) >= 0.0f)
                {
                    float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                    return vertex2 + w * (vertex3 - vertex2); //Barycentric coordinates (0,1-w,w)
                }

                //P inside face region. Compute Q through its Barycentric coordinates (u,v,w)
                float denom = 1.0f / (va + vb + vc);
                float v2 = vb * denom;
                float w2 = vc * denom;
                return vertex1 + ab * v2 + ac * w2; //= u*vertex1 + v*vertex2 + w*vertex3, u = va * denom = 1.0f - v - w
            }

            public static Vector3 PointOnPlane(System.Numerics.Plane plane, Vector3 point)
                => point - ((Vector3.Dot(plane.Normal, point) - plane.D) * plane.Normal);

            public static Vector3 PointOnPlane(Vector3 planeNormal, float planeOriginDistance, Vector3 point)
                => point - (planeNormal * DistanceFrom.PlaneToPoint(planeNormal, planeOriginDistance, point));

            public static Vector3 PointOnAABB(Vector3 min, Vector3 max, Vector3 point)
                => Vector3.Min(Vector3.Max(point, min), max);

            public static Vector3 PointOnSphere(Vector3 center, float radius, Vector3 point)
            {
                Vector3 dir = point - center;
                float lengthSquared = Vector3.Dot(dir, dir);
                if (lengthSquared <= 1e-12f)
                    return center + Vector3.UnitX * radius;

                float inverseLength = 1.0f / MathF.Sqrt(lengthSquared);
                return center + dir * (radius * inverseLength);
            }

            /// <summary>
            /// Determines the closest point between a <see cref="Sphere"/> and a <see cref="Sphere"/>.
            /// </summary>
            /// <remarks>
            /// If the two spheres are overlapping, but not directly on top of each other, the closest point
            /// is the 'closest' point of intersection. This can also be considered is the deepest point of
            /// intersection.
            /// </remarks>
            public static Vector3 SphereOnSphere(Vector3 sphere1Center, float sphere1Radius, Vector3 sphere2Center)
                => PointOnSphere(sphere1Center, sphere1Radius, sphere2Center);

            public static Vector3 ColinearPointOnSegment(Vector3 start, Vector3 end, Vector3 point)
            {
                Vector3 v = end - start;
                float lengthSquared = Vector3.Dot(v, v);
                if (lengthSquared <= 1e-12f)
                    return start;

                float t = Vector3.Dot(point - start, v) / lengthSquared;
                return start + v * t;
            }

            public static Vector3 ColinearPointOnRay(Vector3 start, Vector3 dir, Vector3 point)
            {
                float t = Vector3.Dot(point - start, dir);
                return start + dir * t;
            }
        }
    }
}
