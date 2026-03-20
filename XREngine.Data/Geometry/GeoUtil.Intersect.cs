using Extensions;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Data.Geometry
{
    public static partial class GeoUtil
    {
        /// <summary>
        /// Intersection tests. Usage reads as "intersect X with Y".
        /// </summary>
        public static class Intersect
        {
            public static bool RayWithPoint(Ray ray, Vector3 point)
            {
                Vector3 m = ray.StartPoint - point;

                //Same thing as RayWithSphere except that the radius of the sphere (point)
                //is the epsilon for zero.
                float b = Vector3.Dot(m, ray.Direction);
                float c = Vector3.Dot(m, m) - SingleExtensions.ZeroTolerance;

                if (c > 0.0f && b > 0.0f)
                    return false;

                float discriminant = b * b - c;

                if (discriminant < 0.0f)
                    return false;

                return true;
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="Ray"/> and a <see cref="Ray"/>.
            /// </summary>
            /// <remarks>
            /// This method performs a ray vs ray intersection test based on the following formula
            /// from Goldman.
            /// <code>s = det([o_2 - o_1, d_2, d_1 x d_2]) / ||d_1 x d_2||^2</code>
            /// <code>t = det([o_2 - o_1, d_1, d_1 x d_2]) / ||d_1 x d_2||^2</code>
            /// </remarks>
            public static bool RayWithRay(Ray ray1, Ray ray2, out Vector3 point)
            {
                //Source: Real-Time Rendering, Third Edition
                //Reference: Page 780

                Vector3 cross = Vector3.Cross(ray1.Direction, ray2.Direction);
                float denominator = cross.Length();

                //Lines are parallel.
                if (denominator.IsZero())
                {
                    //Lines are parallel and on top of each other.
                    if (ray2.StartPoint.X.EqualTo(ray1.StartPoint.X) &&
                        ray2.StartPoint.Y.EqualTo(ray1.StartPoint.Y) &&
                        ray2.StartPoint.Z.EqualTo(ray1.StartPoint.Z))
                    {
                        point = Vector3.Zero;
                        return true;
                    }
                }

                denominator *= denominator;

                //3x3 Matrix4x4 for the first ray.
                float m11 = ray2.StartPoint.X - ray1.StartPoint.X;
                float m12 = ray2.StartPoint.Y - ray1.StartPoint.Y;
                float m13 = ray2.StartPoint.Z - ray1.StartPoint.Z;
                float m21 = ray2.Direction.X;
                float m22 = ray2.Direction.Y;
                float m23 = ray2.Direction.Z;
                float m31 = cross.X;
                float m32 = cross.Y;
                float m33 = cross.Z;

                //Determinant of first Matrix4x4.
                float dets =
                    m11 * m22 * m33 +
                    m12 * m23 * m31 +
                    m13 * m21 * m32 -
                    m11 * m23 * m32 -
                    m12 * m21 * m33 -
                    m13 * m22 * m31;

                //3x3 Matrix4x4 for the second ray.
                m21 = ray1.Direction.X;
                m22 = ray1.Direction.Y;
                m23 = ray1.Direction.Z;

                //Determinant of the second Matrix4x4.
                float dett =
                    m11 * m22 * m33 +
                    m12 * m23 * m31 +
                    m13 * m21 * m32 -
                    m11 * m23 * m32 -
                    m12 * m21 * m33 -
                    m13 * m22 * m31;

                //t values of the point of intersection.
                float s = dets / denominator;
                float t = dett / denominator;

                //The points of intersection.
                Vector3 point1 = ray1.StartPoint + (s * ray1.Direction);
                Vector3 point2 = ray2.StartPoint + (t * ray2.Direction);

                //If the points are not equal, no intersection has occurred.
                if (!point2.X.EqualTo(point1.X) ||
                    !point2.Y.EqualTo(point1.Y) ||
                    !point2.Z.EqualTo(point1.Z))
                {
                    point = Vector3.Zero;
                    return false;
                }

                point = point1;
                return true;
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="Ray"/> and a <see cref="System.Numerics.Plane"/>.
            /// Returns the distance to the intersection point.
            /// </summary>
            public static bool RayWithPlane(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 planePoint, Vector3 planeNormal, out float distance)
            {
                Vector3 normalizedDirection = Vector3.Normalize(rayDirection);
                Vector3 normalizedPlaneNormal = Vector3.Normalize(planeNormal);
                return RayWithPlaneCore(rayStartPoint, normalizedDirection, planePoint, normalizedPlaneNormal, out distance);
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="Ray"/> and a <see cref="System.Numerics.Plane"/>.
            /// Returns the intersection point.
            /// </summary>
            public static bool RayWithPlane(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 planePoint, Vector3 planeNormal, out Vector3 point)
            {
                Vector3 normalizedDirection = Vector3.Normalize(rayDirection);
                Vector3 normalizedPlaneNormal = Vector3.Normalize(planeNormal);
                if (!RayWithPlaneCore(rayStartPoint, normalizedDirection, planePoint, normalizedPlaneNormal, out float distance))
                {
                    point = Vector3.Zero;
                    return false;
                }

                point = rayStartPoint + normalizedDirection * distance;
                return true;
            }

            private static bool RayWithPlaneCore(Vector3 rayStartPoint, Vector3 normalizedRayDirection, Vector3 planePoint, Vector3 normalizedPlaneNormal, out float distance)
            {
                //Source: Real-Time Collision Detection by Christer Ericson
                //Reference: Page 175

                float direction = Vector3.Dot(normalizedPlaneNormal, normalizedRayDirection);

                if (direction.IsZero())
                {
                    distance = 0.0f;
                    return false;
                }

                float position = Vector3.Dot(normalizedPlaneNormal, rayStartPoint);
                distance = (-XRMath.GetPlaneDistance(planePoint, normalizedPlaneNormal) - position) / direction;

                if (distance < 0.0f)
                {
                    distance = 0.0f;
                    return false;
                }

                return true;
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="Ray"/> and a triangle.
            /// Returns the distance to the intersection point.
            /// </summary>
            /// <remarks>
            /// This method tests if the ray intersects either the front or back of the triangle.
            /// If the ray is parallel to the triangle's plane, no intersection is assumed to have
            /// happened. If the intersection of the ray and the triangle is behind the origin of
            /// the ray, no intersection is assumed to have happened.
            /// </remarks>
            public static bool RayWithTriangle(Vector3 rayStart, Vector3 rayDir, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3, out float distance)
            {
                //Source: Fast Minimum Storage Ray / Triangle Intersection
                //Reference: http://www.cs.virginia.edu/~gfx/Courses/2003/ImageSynthesis/papers/Acceleration/Fast%20MinimumStorage%20RayTriangle%20Intersection.pdf

                Vector3 edge1 = vertex2 - vertex1;
                Vector3 edge2 = vertex3 - vertex1;
                Vector3 p = Vector3.Cross(rayDir, edge2);
                float determinant = Vector3.Dot(edge1, p);

                if (determinant.IsZero())
                {
                    distance = 0.0f;
                    return false;
                }

                float inverseDeterminant = 1.0f / determinant;
                Vector3 t = rayStart - vertex1;
                float triangleU = Vector3.Dot(t, p) * inverseDeterminant;
                if (triangleU < 0.0f || triangleU > 1.0f)
                {
                    distance = 0.0f;
                    return false;
                }

                Vector3 q = Vector3.Cross(t, edge1);
                float triangleV = Vector3.Dot(rayDir, q) * inverseDeterminant;
                if (triangleV < 0.0f || triangleU + triangleV > 1.0f)
                {
                    distance = 0.0f;
                    return false;
                }

                float rayDistance = Vector3.Dot(edge2, q) * inverseDeterminant;
                if (rayDistance < 0.0f)
                {
                    distance = 0.0f;
                    return false;
                }

                distance = rayDistance;
                return true;
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="Ray"/> and a triangle.
            /// Returns the intersection point.
            /// </summary>
            public static bool RayWithTriangle(Vector3 rayStart, Vector3 rayDir, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3, out Vector3 point)
            {
                if (!RayWithTriangle(rayStart, rayDir, vertex1, vertex2, vertex3, out float distance))
                {
                    point = Vector3.Zero;
                    return false;
                }

                point = rayStart + (rayDir * distance);
                return true;
            }

            public static bool RayWithBoxDistance(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 boxHalfExtents, Matrix4x4 boxInverseTransform, out float distance)
            {
                //Transform ray to untransformed box space
                Vector3 rayEndPoint = rayStartPoint + rayDirection;
                rayStartPoint = Vector3.Transform(rayStartPoint, boxInverseTransform);
                rayEndPoint = Vector3.Transform(rayEndPoint, boxInverseTransform);
                rayDirection = rayEndPoint - rayStartPoint;
                return RayWithAABBDistance(rayStartPoint, rayDirection, -boxHalfExtents, boxHalfExtents, out distance);
            }

            #region RayWithAABBDistance
            public static bool RayWithAABBDistance(Ray ray, AABB box, out float distance)
                => RayWithAABBDistance(ray.StartPoint, ray.Direction, box.Min, box.Max, out distance);
            public static bool RayWithAABBDistance(Ray ray, Vector3 boxMin, Vector3 boxMax, out float distance)
                => RayWithAABBDistance(ray.StartPoint, ray.Direction, boxMin, boxMax, out distance);
            public static bool RayWithAABBDistance(Vector3 rayStartPoint, Vector3 rayDirection, AABB box, out float distance)
                 => RayWithAABBDistance(rayStartPoint, rayDirection, box.Min, box.Max, out distance);
            public static bool RayWithAABBDistance(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 boxMin, Vector3 boxMax, out float distance)
            {
                Vector3 normalizedDirection = Vector3.Normalize(rayDirection);
                bool hit = RayWithAABBDistanceCore(rayStartPoint, normalizedDirection, boxMin, boxMax, out float nearDistance, out _);
                distance = hit ? nearDistance : 0.0f;
                return hit;
            }
            public static bool RayWithAABBDistance(Ray ray, AABB aabb, out float nearDistance, out float farDistance)
                => RayWithAABBDistance(ray.StartPoint, ray.Direction, aabb.Min, aabb.Max, out nearDistance, out farDistance);
            public static bool RayWithAABBDistance(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 boxMin, Vector3 boxMax, out float nearDistance, out float farDistance)
            {
                Vector3 normalizedDirection = Vector3.Normalize(rayDirection);
                return RayWithAABBDistanceCore(rayStartPoint, normalizedDirection, boxMin, boxMax, out nearDistance, out farDistance);
            }

            private static bool RayWithAABBDistanceCore(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 boxMin, Vector3 boxMax, out float nearDistance, out float farDistance)
            {
                nearDistance = 0.0f;
                farDistance = float.MaxValue;

                if (!AccumulateRaySlabInterval(rayStartPoint.X, rayDirection.X, boxMin.X, boxMax.X, ref nearDistance, ref farDistance) ||
                    !AccumulateRaySlabInterval(rayStartPoint.Y, rayDirection.Y, boxMin.Y, boxMax.Y, ref nearDistance, ref farDistance) ||
                    !AccumulateRaySlabInterval(rayStartPoint.Z, rayDirection.Z, boxMin.Z, boxMax.Z, ref nearDistance, ref farDistance))
                {
                    nearDistance = 0.0f;
                    farDistance = 0.0f;
                    return false;
                }

                return true;
            }

            private static bool AccumulateRaySlabInterval(float rayStart, float rayDirection, float slabMin, float slabMax, ref float nearDistance, ref float farDistance)
            {
                if (rayDirection.IsZero())
                    return rayStart >= slabMin && rayStart <= slabMax;

                float inverse = 1.0f / rayDirection;
                float t1 = (slabMin - rayStart) * inverse;
                float t2 = (slabMax - rayStart) * inverse;
                if (t1 > t2)
                    (t1, t2) = (t2, t1);

                nearDistance = MathF.Max(nearDistance, t1);
                farDistance = MathF.Min(farDistance, t2);
                return nearDistance <= farDistance;
            }
            #endregion

            private static bool RaySlabIntersect(float slabmin, float slabmax, float raystart, float rayend, ref float tbenter, ref float tbexit)
            {
                float raydir = rayend - raystart;

                // ray parallel to the slab
                if (Math.Abs(raydir) < 1.0E-9f)
                {
                    // ray parallel to the slab, but ray not inside the slab planes
                    if (raystart < slabmin || raystart > slabmax)
                        return false;
                    // ray parallel to the slab, but ray inside the slab planes
                    else
                        return true;
                }

                // slab's enter and exit parameters
                float tsenter = (slabmin - raystart) / raydir;
                float tsexit = (slabmax - raystart) / raydir;

                // order the enter / exit values.
                if (tsenter > tsexit)
                    (tsenter, tsexit) = (tsexit, tsenter);

                // make sure the slab interval and the current box intersection interval overlap
                if (tbenter > tsexit || tsenter > tbexit)
                {
                    // nope. Ray missed the box.
                    return false;
                }
                else // yep, the slab and current intersection interval overlap
                {
                    // update the intersection interval
                    tbenter = Math.Max(tbenter, tsenter);
                    tbexit = Math.Min(tbexit, tsexit);
                    return true;
                }
            }

            public static bool SegmentWithAABB(Vector3 segmentStart, Vector3 segmentEnd, Vector3 boxMin, Vector3 boxMax, out Vector3 enterPoint, out Vector3 exitPoint)
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
                        {
                            enterPoint = Vector3.Zero;
                            exitPoint = Vector3.Zero;
                            return false;
                        }

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
                    {
                        enterPoint = Vector3.Zero;
                        exitPoint = Vector3.Zero;
                        return false;
                    }
                }

                enterPoint = segmentStart + direction * tMin;
                exitPoint = segmentStart + direction * tMax;
                return true;
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="Ray"/> and an AABB.
            /// Returns the intersection point.
            /// </summary>
            public static bool RayWithAABB(Ray ray, Vector3 boxMin, Vector3 boxMax, out Vector3 point)
            {
                Vector3 normalizedDirection = Vector3.Normalize(ray.Direction);
                if (!RayWithAABBDistanceCore(ray.StartPoint, normalizedDirection, boxMin, boxMax, out float distance, out _))
                {
                    point = Vector3.Zero;
                    return false;
                }

                point = ray.StartPoint + (normalizedDirection * distance);
                return true;
            }

            public static bool RayWithBox(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 boxHalfExtents, Matrix4x4 boxInverseTransform, out Vector3 point)
            {
                if (!RayWithBoxDistance(rayStartPoint, rayDirection, boxHalfExtents, boxInverseTransform, out float distance))
                {
                    point = Vector3.Zero;
                    return false;
                }

                point = rayStartPoint + (rayDirection * distance);
                return true;
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="Ray"/> and a <see cref="Sphere"/>.
            /// Returns the distance to the intersection point.
            /// </summary>
            public static bool RayWithSphere(Vector3 rayStart, Vector3 rayDir, Vector3 sphereCenter, float sphereRadius, out float distance)
            {
                //Source: Real-Time Collision Detection by Christer Ericson
                //Reference: Page 177

                Vector3 m = rayStart - sphereCenter;
                float a = Vector3.Dot(rayDir, rayDir);
                if (a <= 1e-12f)
                {
                    distance = 0.0f;
                    return false;
                }

                float b = Vector3.Dot(m, rayDir);
                float c = Vector3.Dot(m, m) - (sphereRadius * sphereRadius);

                if (c > 0.0f && b > 0.0f)
                {
                    distance = 0.0f;
                    return false;
                }

                float discriminant = b * b - a * c;

                if (discriminant < 0.0f)
                {
                    distance = 0.0f;
                    return false;
                }

                float sqrtDiscriminant = MathF.Sqrt(discriminant);
                distance = (-b - sqrtDiscriminant) / a;

                if (distance < 0.0f)
                    distance = 0.0f;

                return true;
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="Ray"/> and a <see cref="Sphere"/>.
            /// Returns the intersection point.
            /// </summary>
            public static bool RayWithSphere(Vector3 rayStart, Vector3 rayDir, Vector3 sphereCenter, float sphereRadius, out Vector3 point)
            {
                if (!RayWithSphere(rayStart, rayDir, sphereCenter, sphereRadius, out float distance))
                {
                    point = Vector3.Zero;
                    return false;
                }

                point = rayStart + (rayDir * distance);
                return true;
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="System.Numerics.Plane"/> and a point.
            /// </summary>
            public static EPlaneIntersection PlaneWithPoint(Plane plane, Vector3 point)
            {
                float distance = Vector3.Dot(plane.Normal, point);
                distance += plane.D;

                if (distance > 0.0f)
                    return EPlaneIntersection.Front;

                if (distance < 0.0f)
                    return EPlaneIntersection.Back;

                return EPlaneIntersection.Intersecting;
            }

            /// <summary>
            /// Determines whether there is an intersection between two planes.
            /// </summary>
            public static bool PlaneWithPlane(Plane plane1, Plane plane2)
            {
                Vector3 direction = Vector3.Cross(plane1.Normal, plane2.Normal);
                return !Vector3.Dot(direction, direction).IsZero();
            }

            /// <summary>
            /// Determines whether there is an intersection between two planes.
            /// Returns the line of intersection as a <see cref="Ray"/>.
            /// </summary>
            /// <remarks>
            /// Although a ray is set to have an origin, the ray returned by this method is really
            /// a line in three dimensions which has no real origin.
            /// </remarks>
            public static bool PlaneWithPlane(Plane plane1, Plane plane2, out Ray line)
            {
                //Source: Real-Time Collision Detection by Christer Ericson
                //Reference: Page 207

                Vector3 direction = Vector3.Cross(plane1.Normal, plane2.Normal);

                float denominator = Vector3.Dot(direction, direction);

                if (denominator.IsZero())
                {
                    line = new Ray();
                    return false;
                }

                Vector3 temp = plane1.D * plane2.Normal - plane2.D * plane1.Normal;
                Vector3 point = Vector3.Cross(temp, direction);

                line = new Ray(point, point + Vector3.Normalize(direction));

                return true;
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="System.Numerics.Plane"/> and a triangle.
            /// </summary>
            public static EPlaneIntersection PlaneWithTriangle(Plane plane, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3)
            {
                //Source: Real-Time Collision Detection by Christer Ericson
                //Reference: Page 207

                EPlaneIntersection test1 = PlaneWithPoint(plane, vertex1);
                EPlaneIntersection test2 = PlaneWithPoint(plane, vertex2);
                EPlaneIntersection test3 = PlaneWithPoint(plane, vertex3);

                if (test1 == EPlaneIntersection.Front && test2 == EPlaneIntersection.Front && test3 == EPlaneIntersection.Front)
                    return EPlaneIntersection.Front;

                if (test1 == EPlaneIntersection.Back && test2 == EPlaneIntersection.Back && test3 == EPlaneIntersection.Back)
                    return EPlaneIntersection.Back;

                return EPlaneIntersection.Intersecting;
            }

            public static EPlaneIntersection PlaneWithBox(Plane plane, Vector3 boxMin, Vector3 boxMax, Matrix4x4 boxInverseMatrix)
            {
                //Source: Real-Time Collision Detection by Christer Ericson
                //Reference: Page 161

                //Transform plane into untransformed box space
                plane = Plane.Transform(plane, boxInverseMatrix);

                Vector3 min = Vector3.Zero;
                Vector3 max = Vector3.Zero;

                max.X = (plane.Normal.X >= 0.0f) ? boxMin.X : boxMax.X;
                max.Y = (plane.Normal.Y >= 0.0f) ? boxMin.Y : boxMax.Y;
                max.Z = (plane.Normal.Z >= 0.0f) ? boxMin.Z : boxMax.Z;
                min.X = (plane.Normal.X >= 0.0f) ? boxMax.X : boxMin.X;
                min.Y = (plane.Normal.Y >= 0.0f) ? boxMax.Y : boxMin.Y;
                min.Z = (plane.Normal.Z >= 0.0f) ? boxMax.Z : boxMin.Z;

                if (Vector3.Dot(plane.Normal, max) + plane.D > 0.0f)
                    return EPlaneIntersection.Front;

                if (Vector3.Dot(plane.Normal, min) + plane.D < 0.0f)
                    return EPlaneIntersection.Back;

                return EPlaneIntersection.Intersecting;
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="System.Numerics.Plane"/> and a <see cref="Sphere"/>.
            /// </summary>
            public static EPlaneIntersection PlaneWithSphere(Plane plane, Sphere sphere)
            {
                //Source: Real-Time Collision Detection by Christer Ericson
                //Reference: Page 160

                float distance = Vector3.Dot(plane.Normal, sphere.Center) + plane.D;

                if (distance > sphere.Radius)
                    return EPlaneIntersection.Front;

                if (distance < -sphere.Radius)
                    return EPlaneIntersection.Back;

                return EPlaneIntersection.Intersecting;
            }

            /// <summary>
            /// Determines whether there is an intersection between two AABBs.
            /// </summary>
            public static bool AABBWithAABB(AABB box1, AABB box2)
            {
                if (box1.Min.X > box2.Max.X || box2.Min.X > box1.Max.X)
                    return false;

                if (box1.Min.Y > box2.Max.Y || box2.Min.Y > box1.Max.Y)
                    return false;

                if (box1.Min.Z > box2.Max.Z || box2.Min.Z > box1.Max.Z)
                    return false;

                return true;
            }

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="AABB"/> and a <see cref="Sphere"/>.
            /// </summary>
            public static bool BoxWithSphere(Vector3 boxHalfExtents, Matrix4x4 boxInverseTransform, Vector3 sphereCenter, float sphereRadius)
            {
                sphereCenter = Vector3.Transform(sphereCenter, boxInverseTransform);
                return AABBWithSphere(-boxHalfExtents, boxHalfExtents, sphereCenter, sphereRadius);
            }

            public static bool AABBWithSphere(Vector3 boxMin, Vector3 boxMax, Vector3 sphereCenter, float sphereRadius)
                => Vector3.DistanceSquared(sphereCenter, Vector3.Clamp(sphereCenter, boxMin, boxMax)) <= sphereRadius * sphereRadius;

            /// <summary>
            /// Determines whether there is an intersection between a <see cref="Sphere"/> and a triangle.
            /// </summary>
            public static bool SphereWithTriangle(Vector3 sphereCenter, float sphereRadius, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3)
            {
                //Source: Real-Time Collision Detection by Christer Ericson
                //Reference: Page 167

                Vector3 point = Nearest.PointOnTriangle(sphereCenter, vertex1, vertex2, vertex3);
                Vector3 v = point - sphereCenter;

                return v.LengthSquared() <= sphereRadius * sphereRadius;
            }

            public static bool SphereWithSphere(Vector3 sphere1Center, float sphere1Radius, Vector3 sphere2Center, float sphere2Radius)
            {
                float radiisum = sphere1Radius + sphere2Radius;
                return Vector3.DistanceSquared(sphere1Center, sphere2Center) <= radiisum * radiisum;
            }

            public static bool SegmentWithPlane(Vector3 start, Vector3 end, float d, Vector3 normal, out Vector3 intersectionPoint)
            {
                Vector3 ab = end - start;
                float denominator = Vector3.Dot(ab, normal);
                if (MathF.Abs(denominator) <= 1e-12f)
                {
                    intersectionPoint = Vector3.Zero;
                    return false;
                }

                float t = -(Vector3.Dot(start, normal) + d) / denominator;
                if (t >= 0.0f && t <= 1.0f)
                {
                    intersectionPoint = start + ab * t;
                    return true;
                }
                intersectionPoint = Vector3.Zero;
                return false;
            }

            public static bool FrustaAsAABB(PreparedFrustum frustumA, PreparedFrustum frustumB, out Vector3 aabbMin, out Vector3 aabbMax)
                => PreparedFrustum.FrustumIntersection.TryIntersectFrustaAabb(frustumA, frustumB, out aabbMin, out aabbMax);

            public static bool FrustaAsAABB(Frustum frustumA, Frustum frustumB, out Vector3 aabbMin, out Vector3 aabbMax)
                => PreparedFrustum.FrustumIntersection.TryIntersectFrustaAabb(
                    PreparedFrustum.FromFrustum(frustumA),
                    PreparedFrustum.FromFrustum(frustumB),
                    out aabbMin,
                    out aabbMax);

            public static Vector3 ThreePlanes(Plane plane1, Plane plane2, Plane plane3)
            {
                Vector3 normal1 = new(plane1.Normal.X, plane1.Normal.Y, plane1.Normal.Z);
                Vector3 normal2 = new(plane2.Normal.X, plane2.Normal.Y, plane2.Normal.Z);
                Vector3 normal3 = new(plane3.Normal.X, plane3.Normal.Y, plane3.Normal.Z);

                Vector3 cross1 = Vector3.Cross(normal2, normal3);
                Vector3 cross2 = Vector3.Cross(normal3, normal1);
                Vector3 cross3 = Vector3.Cross(normal1, normal2);

                float denominator = Vector3.Dot(normal1, cross1);
                return (cross1 * plane1.D + cross2 * plane2.D + cross3 * plane3.D) / denominator;
            }

            public static bool PointBetweenPlanes(Vector3 point, Plane far, Plane left, EBetweenPlanes comp)
            {
                float farDist = DistanceFrom.PlaneToPoint(far, point);
                float leftDist = DistanceFrom.PlaneToPoint(left, point);
                return comp switch
                {
                    EBetweenPlanes.NormalsFacing => farDist > 0.0f && leftDist > 0.0f,
                    EBetweenPlanes.NormalsAway => farDist < 0.0f && leftDist < 0.0f,
                    _ => farDist * leftDist < 0.0f,
                };
            }
        }
    }
}
