using System.Numerics;

namespace XREngine.Data.Geometry
{
    public static partial class GeoUtil
    {
        /// <summary>
        /// Boolean containment tests. Usage reads as "contains X within Y".
        /// </summary>
        public static class Contains
        {
            public static bool PointWithinBox(Vector3 boxHalfExtents, Matrix4x4 boxInverseTransform, Vector3 point)
            {
                //Transform point into untransformed box space
                point = Vector3.Transform(point, boxInverseTransform);
                return PointWithinAABB(-boxHalfExtents, boxHalfExtents, point);
            }

            public static bool PointWithinAABB(Vector3 boxMin, Vector3 boxMax, Vector3 point)
            {
                if (boxMin.X <= point.X && boxMax.X >= point.X &&
                    boxMin.Y <= point.Y && boxMax.Y >= point.Y &&
                    boxMin.Z <= point.Z && boxMax.Z >= point.Z)
                    return true;

                return false;
            }

            /// <summary>
            /// Determines whether a <see cref="Sphere"/> contains a point.
            /// </summary>
            public static bool PointWithinSphere(Vector3 center, float radius, Vector3 point)
                => Vector3.DistanceSquared(point, center) <= radius * radius;

            public static bool PointWithinFrustum(Frustum frustum, Vector3 point)
            {
                foreach (Plane p in frustum)
                    if (DistanceFrom.PlaneToPoint(p, point) < 0)
                        return false;
                return true;
            }
        }
    }
}
