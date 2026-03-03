using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Data.Geometry
{
    public static partial class GeoUtil
    {
        /// <summary>
        /// Containment classification queries returning <see cref="EContainment"/>.
        /// Usage reads as "containment of X within Y".
        /// </summary>
        public static class ContainmentOf
        {
            public static EContainment AABBWithinSphere(Vector3 center, float radius, Vector3 minimum, Vector3 maximum)
            {
                float r2 = radius * radius;
                if ((center - minimum).LengthSquared() < r2 &&
                    (center - maximum).LengthSquared() < r2)
                    return EContainment.Contains;

                Sphere sphere = new(center, radius);
                EPlaneIntersection[] intersections =
                [
                    Intersect.PlaneWithSphere(new Plane(Vector3.UnitX, XRMath.GetPlaneDistance(maximum, Vector3.UnitX)), sphere),
                    Intersect.PlaneWithSphere(new Plane(-Vector3.UnitX, XRMath.GetPlaneDistance(minimum, -Vector3.UnitX)), sphere),
                    Intersect.PlaneWithSphere(new Plane(Vector3.UnitY, XRMath.GetPlaneDistance(maximum, Vector3.UnitY)), sphere),
                    Intersect.PlaneWithSphere(new Plane(-Vector3.UnitY, XRMath.GetPlaneDistance(minimum, -Vector3.UnitY)), sphere),
                    Intersect.PlaneWithSphere(new Plane(Vector3.UnitZ, XRMath.GetPlaneDistance(maximum, Vector3.UnitZ)), sphere),
                    Intersect.PlaneWithSphere(new Plane(-Vector3.UnitZ, XRMath.GetPlaneDistance(minimum, -Vector3.UnitZ)), sphere),
                ];

                return intersections.Any(x => x == EPlaneIntersection.Front)
                    ? EContainment.Disjoint
                    : EContainment.Intersects;
            }

            public static EContainment BoxWithinAABB(Vector3 box1Min, Vector3 box1Max, Vector3 box2HalfExtents, Matrix4x4 box2Transform)
            {
                Vector3[] corners = AABB.GetCorners(box2HalfExtents, x => Vector3.Transform(x, box2Transform));
                int numIn = 0, numOut = 0;
                for (int i = 0; i < 8; ++i)
                {
                    if (Contains.PointWithinAABB(box1Min, box1Max, corners[i]))
                        ++numIn;
                    else
                        ++numOut;
                }
                if (numOut == 0)
                    return EContainment.Contains;
                if (numIn == 0)
                    return EContainment.Disjoint;
                return EContainment.Intersects;
            }

            public static EContainment AABBWithinAABB(Vector3 box1Min, Vector3 box1Max, Vector3 box2Min, Vector3 box2Max)
            {
                if (box1Max.X < box2Min.X || box1Min.X > box2Max.X)
                    return EContainment.Disjoint;

                if (box1Max.Y < box2Min.Y || box1Min.Y > box2Max.Y)
                    return EContainment.Disjoint;

                if (box1Max.Z < box2Min.Z || box1Min.Z > box2Max.Z)
                    return EContainment.Disjoint;

                if (box1Min.X <= box2Min.X && box2Max.X <= box1Max.X &&
                    box1Min.Y <= box2Min.Y && box2Max.Y <= box1Max.Y &&
                    box1Min.Z <= box2Min.Z && box2Max.Z <= box1Max.Z)
                    return EContainment.Contains;

                return EContainment.Intersects;
            }

            public static EContainment SphereWithinAABB(Vector3 boxMin, Vector3 boxMax, Vector3 sphereCenter, float sphereRadius)
            {
                Vector3 vector = Vector3.Clamp(sphereCenter, boxMin, boxMax);
                float distance = Vector3.DistanceSquared(sphereCenter, vector);

                if (distance > sphereRadius * sphereRadius)
                    return EContainment.Disjoint;

                return
                    (boxMin.X + sphereRadius <= sphereCenter.X) &&
                    (boxMin.Y + sphereRadius <= sphereCenter.Y) &&
                    (boxMin.Z + sphereRadius <= sphereCenter.Z) &&
                    (sphereCenter.X <= boxMax.X - sphereRadius) &&
                    (sphereCenter.Y <= boxMax.Y - sphereRadius) &&
                    (sphereCenter.Z <= boxMax.Z - sphereRadius) &&
                    (boxMax.X - boxMin.X > sphereRadius) &&
                    (boxMax.Y - boxMin.Y > sphereRadius) &&
                    (boxMax.Z - boxMin.Z > sphereRadius)
                    ? EContainment.Contains
                    : EContainment.Intersects;
            }

            /// <summary>
            /// Determines whether a <see cref="Sphere"/> contains a triangle.
            /// </summary>
            public static EContainment TriangleWithinSphere(Sphere sphere, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3)
            {
                //Source: Jorgy343
                //Reference: None

                bool test1 = Contains.PointWithinSphere(sphere.Center, sphere.Radius, vertex1);
                bool test2 = Contains.PointWithinSphere(sphere.Center, sphere.Radius, vertex2);
                bool test3 = Contains.PointWithinSphere(sphere.Center, sphere.Radius, vertex3);

                if (test1 && test2 && test3)
                    return EContainment.Contains;

                return Intersect.SphereWithTriangle(sphere.Center, sphere.Radius, vertex1, vertex2, vertex3)
                    ? EContainment.Intersects
                    : EContainment.Disjoint;
            }

            public static EContainment BoxWithinSphere(
                Vector3 sphereCenter,
                float sphereRadius,
                Vector3 boxHalfExtents,
                Matrix4x4 boxInverseTransform)
            {
                if (!Intersect.BoxWithSphere(boxHalfExtents, boxInverseTransform, sphereCenter, sphereRadius))
                    return EContainment.Disjoint;

                sphereCenter = Vector3.Transform(sphereCenter, boxInverseTransform);

                float r2 = sphereRadius * sphereRadius;
                Vector3[] points = AABB.GetCorners(boxHalfExtents);
                foreach (Vector3 point in points)
                    if (Vector3.DistanceSquared(sphereCenter, point) > r2)
                        return EContainment.Intersects;

                return EContainment.Contains;
            }

            /// <summary>
            /// Determines whether a <see cref="Sphere"/> contains a <see cref="Sphere"/>.
            /// </summary>
            public static EContainment SphereWithinSphere(Vector3 sphere1Center, float sphere1Radius, Vector3 sphere2Center, float sphere2Radius)
            {
                float distance = Vector3.DistanceSquared(sphere1Center, sphere2Center);

                float value = sphere1Radius + sphere2Radius;
                if (value * value < distance)
                    return EContainment.Disjoint;

                value = sphere1Radius - sphere2Radius;
                return value * value < distance
                    ? EContainment.Intersects
                    : EContainment.Contains;
            }

            public static EContainment SphereWithinFrustum(Frustum frustum, Vector3 center, float radius)
            {
                float distance;
                EContainment type = EContainment.Contains;
                foreach (Plane p in frustum)
                {
                    distance = DistanceFrom.PlaneToPoint(p, center);
                    if (distance < -radius)
                        return EContainment.Disjoint;
                    else if (distance < radius)
                        type = EContainment.Intersects;
                }
                return type;
            }

            public static EContainment BoxWithinFrustum(Frustum frustum, Vector3 boxHalfExtents, Matrix4x4 boxTransform)
            {
                EContainment result = EContainment.Contains;
                int numOut, numIn;
                Vector3[] corners = AABB.GetCorners(boxHalfExtents, x => Vector3.Transform(x, boxTransform));
                foreach (Plane p in frustum)
                {
                    numOut = 0;
                    numIn = 0;
                    for (int i = 0; i < 8 && (numIn == 0 || numOut == 0); i++)
                        if (DistanceFrom.PlaneToPoint(p, corners[i]) < 0)
                            numOut++;
                        else
                            numIn++;
                    if (numIn == 0)
                        return EContainment.Disjoint;
                    else if (numOut != 0)
                        result = EContainment.Intersects;
                }
                return result;
            }

            public static EContainment FrustumWithinAABB(Vector3 boxMin, Vector3 boxMax, Frustum frustum)
            {
                int numIn = 0, numOut = 0;
                foreach (Vector3 v in frustum.Corners)
                    if (Contains.PointWithinAABB(boxMin, boxMax, v))
                        ++numIn;
                    else
                        ++numOut;
                return numOut == 0 ? EContainment.Contains : numIn == 0 ? EContainment.Disjoint : EContainment.Intersects;
            }

            public static EContainment AABBWithinFrustum(Frustum frustum, Vector3 boxMin, Vector3 boxMax)
            {
                EContainment c = FrustumWithinAABB(boxMin, boxMax, frustum);
                if (c != EContainment.Disjoint)
                    return EContainment.Intersects;

                EContainment result = EContainment.Contains;
                int numOut, numIn;
                Vector3[] corners = AABB.GetCorners(boxMin, boxMax);
                foreach (Plane p in frustum)
                {
                    numOut = 0;
                    numIn = 0;
                    for (int i = 0; i < 8 && (numIn == 0 || numOut == 0); i++)
                        if (DistanceFrom.PlaneToPoint(p, corners[i]) < 0)
                            numOut++;
                        else
                            numIn++;
                    if (numIn == 0)
                        return EContainment.Disjoint;
                    else if (numOut != 0)
                        result = EContainment.Intersects;
                }
                return result;
            }

            public static EContainment ConeWithinFrustum(Frustum frustum, Vector3 center, Vector3 up, float height, float radius)
            {
                float clampedHeight = MathF.Max(0.0f, height);
                float clampedRadius = MathF.Max(0.0f, radius);

                float upLengthSquared = up.LengthSquared();
                Vector3 axisDir = upLengthSquared > 1e-12f
                    ? up / MathF.Sqrt(upLengthSquared)
                    : Vector3.UnitY;

                Vector3 tip = center + axisDir * clampedHeight;

                EContainment containment = EContainment.Contains;
                foreach (Plane p in frustum)
                {
                    Vector3 planeNormal = p.Normal;
                    float baseCenterDistance = DistanceFrom.PlaneToPoint(p, center);
                    float tipDistance = DistanceFrom.PlaneToPoint(p, tip);

                    float normalLengthSquared = planeNormal.LengthSquared();
                    float normalAlongAxis = Vector3.Dot(planeNormal, axisDir);
                    float radialNormalLengthSquared = normalLengthSquared - normalAlongAxis * normalAlongAxis;
                    if (radialNormalLengthSquared < 0.0f)
                        radialNormalLengthSquared = 0.0f;

                    float radialSupport = clampedRadius * MathF.Sqrt(radialNormalLengthSquared);
                    float maxBaseDistance = baseCenterDistance + radialSupport;
                    float minBaseDistance = baseCenterDistance - radialSupport;

                    float maxDistance = MathF.Max(maxBaseDistance, tipDistance);
                    if (maxDistance < 0.0f)
                        return EContainment.Disjoint;

                    float minDistance = MathF.Min(minBaseDistance, tipDistance);
                    if (minDistance < 0.0f)
                        containment = EContainment.Intersects;
                }

                return containment;
            }
        }
    }
}
