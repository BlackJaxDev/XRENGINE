using Extensions;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Data.Geometry
{
    /// <summary>
    /// Helper class to handle all intersections, containment, distances, and closest point situations between various types of goemetry.
    /// </summary>
    public static class GeoUtil
    {
        /// <summary>
        /// Determines the closest point between a point and a triangle.
        /// </summary>
        /// <param name="point">The point to test.</param>
        /// <param name="vertex1">The first vertex to test.</param>
        /// <param name="vertex2">The second vertex to test.</param>
        /// <param name="vertex3">The third vertex to test.</param>
        /// <param name="result">When the method completes, contains the closest point between the two objects.</param>
        public static Vector3 ClosestPointPointTriangle(Vector3 point, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3)
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
        public static Vector3 ClosestPointPlanePoint(System.Numerics.Plane plane, Vector3 point)
            => point - ((Vector3.Dot(plane.Normal, point) - plane.D) * plane.Normal);

        public static Vector3 ClosestPointAABBPoint(Vector3 min, Vector3 max, Vector3 point)
            => Vector3.Min(Vector3.Max(point, min), max);

        public static Vector3 ClosestPointSpherePoint(Vector3 center, float radius, Vector3 point)
        {
            Vector3 dir = point - center;
            dir = Vector3.Normalize(dir);
            return dir * radius + center;
        }

        /// <summary>
        /// Determines the closest point between a <see cref="Sphere"/> and a <see cref="Sphere"/>.
        /// </summary>
        /// <param name="sphere1">The first sphere to test.</param>
        /// <param name="sphere2">The second sphere to test.</param>
        /// <param name="result">When the method completes, contains the closest point between the two objects;
        /// or, if the point is directly in the center of the sphere, contains <see cref="Vector3.Zero"/>.</param>
        /// <remarks>
        /// If the two spheres are overlapping, but not directly on top of each other, the closest point
        /// is the 'closest' point of intersection. This can also be considered is the deepest point of
        /// intersection.
        /// </remarks>
        public static Vector3 ClosestPointSphereSphere(Vector3 sphere1Center, float sphere1Radius, Vector3 sphere2Center)
            => ClosestPointSpherePoint(sphere1Center, sphere1Radius, sphere2Center);
        
        public static float DistancePlanePoint(Plane plane, Vector3 point)
            => DistancePlanePoint(plane.Normal, plane.D, point);
        public static float DistancePlanePoint(Vector3 planeNormal, float planeOriginDistance, Vector3 point)
            => Vector3.Dot(planeNormal, point) + planeOriginDistance;
        public static Vector3 ClosestPlanePointToPoint(Vector3 planeNormal, float planeOriginDistance, Vector3 point)
            => point - (planeNormal * DistancePlanePoint(planeNormal, planeOriginDistance, point));

        public static EContainment SphereContainsAABB(Vector3 center, float radius, Vector3 minimum, Vector3 maximum)
        {
            float r2 = radius * radius;
            if ((center - minimum).LengthSquared() < r2 &&
                (center - maximum).LengthSquared() < r2)
                return EContainment.Contains;

            Sphere sphere = new(center, radius);
            EPlaneIntersection[] intersections =
            [
                PlaneIntersectsSphere(new Plane(Vector3.UnitX, XRMath.GetPlaneDistance(maximum, Vector3.UnitX)), sphere),
                PlaneIntersectsSphere(new Plane(-Vector3.UnitX, XRMath.GetPlaneDistance(minimum, -Vector3.UnitX)), sphere),
                PlaneIntersectsSphere(new Plane(Vector3.UnitY, XRMath.GetPlaneDistance(maximum, Vector3.UnitY)), sphere),
                PlaneIntersectsSphere(new Plane(-Vector3.UnitY, XRMath.GetPlaneDistance(minimum, -Vector3.UnitY)), sphere),
                PlaneIntersectsSphere(new Plane(Vector3.UnitZ, XRMath.GetPlaneDistance(maximum, Vector3.UnitZ)), sphere),
                PlaneIntersectsSphere(new Plane(-Vector3.UnitZ, XRMath.GetPlaneDistance(minimum, -Vector3.UnitZ)), sphere),
            ];

            return intersections.Any(x => x == EPlaneIntersection.Front) 
                ? EContainment.Disjoint
                : EContainment.Intersects;
        }

        public static float DistancePlanePoint(Vector3 normal, Vector3 planePoint, Vector3 point)
            => Vector3.Dot(normal, point) + Vector3.Dot(planePoint, normal);

        public static float DistanceAABBPoint(Vector3 min, Vector3 max, Vector3 point)
        {
            float distance = 0.0f;

            if (point.X < min.X)
                distance += (min.X - point.X) * (min.X - point.X);
            if (point.X > max.X)
                distance += (point.X - max.X) * (point.X - max.X);

            if (point.Y < min.Y)
                distance += (min.Y - point.Y) * (min.Y - point.Y);
            if (point.Y > max.Y)
                distance += (point.Y - max.Y) * (point.Y - max.Y);

            if (point.Z < min.Z)
                distance += (min.Z - point.Z) * (min.Z - point.Z);
            if (point.Z > max.Z)
                distance += (point.Z - max.Z) * (point.Z - max.Z);

            return (float)Math.Sqrt(distance);
        }

        /// <summary>
        /// Determines the distance between a <see cref="AABB"/> and a <see cref="AABB"/>.
        /// </summary>
        /// <param name="box1">The first box to test.</param>
        /// <param name="box2">The second box to test.</param>
        /// <returns>The distance between the two objects.</returns>
        public static float DistanceAABBAABB(Vector3 box1Min, Vector3 box1Max, Vector3 box2Min, Vector3 box2Max)
        {
            float distance = 0.0f;

            for (int i = 0; i < 3; ++i)
                if (box1Min[i] > box2Max[i])
                {
                    float delta = box2Max[i] - box1Min[i];
                    distance += delta * delta;
                }
                else if (box2Min[i] > box1Max[i])
                {
                    float delta = box1Max[i] - box2Min[i];
                    distance += delta * delta;
                }

            return (float)Math.Sqrt(distance);
        }

        public static float DistanceSpherePoint(Vector3 sphereCenter, float sphereRadius, Vector3 point)
            => (Vector3.Distance(sphereCenter, point) - sphereRadius).ClampMin(0.0f);

        public static float DistanceSphereSphere(float sphere1Radius, Vector3 sphere1Pos, float sphere2Radius, Vector3 sphere2Pos)
            => Math.Max(Vector3.Distance(sphere1Pos, sphere2Pos) - sphere1Radius - sphere2Radius, 0f);

        public static bool RayIntersectsPoint(Ray ray, Vector3 point)
        {
            Vector3 m = ray.StartPoint - point;

            //Same thing as RayIntersectsSphere except that the radius of the sphere (point)
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
        /// <param name="ray1">The first ray to test.</param>
        /// <param name="ray2">The second ray to test.</param>
        /// <param name="point">When the method completes, contains the point of intersection,
        /// or <see cref="Vector3.Zero"/> if there was no intersection.</param>
        /// <returns>Whether the two objects intersect.</returns>
        /// <remarks>
        /// This method performs a ray vs ray intersection test based on the following formula
        /// from Goldman.
        /// <code>s = det([o_2 - o_1, d_2, d_1 x d_2]) / ||d_1 x d_2||^2</code>
        /// <code>t = det([o_2 - o_1, d_1, d_1 x d_2]) / ||d_1 x d_2||^2</code>
        /// Where o_1 is the position of the first ray, o_2 is the position of the second ray,
        /// d_1 is the normalized direction of the first ray, d_2 is the normalized direction
        /// of the second ray, det denotes the determinant of a Matrix4x4, x denotes the cross
        /// product, [ ] denotes a Matrix4x4, and || || denotes the length or magnitude of a vector.
        /// </remarks>
        public static bool RayIntersectsRay(Ray ray1, Ray ray2, out Vector3 point)
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

        public static EContainment AABBContainsBox(Vector3 box1Min, Vector3 box1Max, Vector3 box2HalfExtents, Matrix4x4 box2Transform)
        {
            Vector3[] corners = AABB.GetCorners(box2HalfExtents, x => Vector3.Transform(x, box2Transform));
            int numIn = 0, numOut = 0;
            for (int i = 0; i < 8; ++i)
            {
                if (AABBContainsPoint(box1Min, box1Max, corners[i]))
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

        /// <summary>
        /// Determines whether there is an intersection between a <see cref="Ray"/> and a <see cref="System.Numerics.Plane"/>.
        /// </summary>
        /// <param name="ray">The ray to test.</param>
        /// <param name="plane">The plane to test.</param>
        /// <param name="distance">When the method completes, contains the distance of the intersection,
        /// or 0 if there was no intersection.</param>
        /// <returns>Whether the two objects intersect.</returns>
        public static bool RayIntersectsPlane(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 planePoint, Vector3 planeNormal, out float distance)
        {
            rayDirection = Vector3.Normalize(rayDirection);
            planeNormal = Vector3.Normalize(planeNormal);

            //Source: Real-Time Collision Detection by Christer Ericson
            //Reference: Page 175

            float direction = Vector3.Dot(planeNormal, rayDirection);

            if (direction.IsZero())
            {
                distance = 0.0f;
                return false;
            }

            float position = Vector3.Dot(planeNormal, rayStartPoint);
            distance = (-XRMath.GetPlaneDistance(planePoint, planeNormal) - position) / direction;

            if (distance < 0.0f)
            {
                distance = 0.0f;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether there is an intersection between a <see cref="Ray"/> and a <see cref="System.Numerics.Plane"/>.
        /// </summary>
        /// <param name="ray">The ray to test.</param>
        /// <param name="plane">The plane to test</param>
        /// <param name="point">When the method completes, contains the point of intersection,
        /// or <see cref="Vector3.Zero"/> if there was no intersection.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static bool RayIntersectsPlane(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 planePoint, Vector3 planeNormal, out Vector3 point)
        {
            //Source: Real-Time Collision Detection by Christer Ericson
            //Reference: Page 175

            if (!RayIntersectsPlane(rayStartPoint, rayDirection, planePoint, planeNormal, out float distance))
            {
                point = Vector3.Zero;
                return false;
            }

            point = rayStartPoint + (Vector3.Normalize(rayDirection) * distance);
            return true;
        }

        /// <summary>
        /// Determines whether there is an intersection between a <see cref="Ray"/> and a triangle.
        /// </summary>
        /// <param name="ray">The ray to test.</param>
        /// <param name="vertex1">The first vertex of the triangle to test.</param>
        /// <param name="vertex2">The second vertex of the triangle to test.</param>
        /// <param name="vertex3">The third vertex of the triangle to test.</param>
        /// <param name="distance">When the method completes, contains the distance of the intersection,
        /// or 0 if there was no intersection.</param>
        /// <returns>Whether the two objects intersected.</returns>
        /// <remarks>
        /// This method tests if the ray intersects either the front or back of the triangle.
        /// If the ray is parallel to the triangle's plane, no intersection is assumed to have
        /// happened. If the intersection of the ray and the triangle is behind the origin of
        /// the ray, no intersection is assumed to have happened. In both cases of assumptions,
        /// this method returns false.
        /// </remarks>
        public static bool RayIntersectsTriangle(Vector3 rayStart, Vector3 rayDir, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3, out float distance)
        {
            //Source: Fast Minimum Storage Ray / Triangle Intersection
            //Reference: http://www.cs.virginia.edu/~gfx/Courses/2003/ImageSynthesis/papers/Acceleration/Fast%20MinimumStorage%20RayTriangle%20Intersection.pdf

            //Compute vectors along two edges of the triangle.
            Vector3 edge1 = Vector3.Zero, edge2 = Vector3.Zero;

            //Edge 1
            edge1.X = vertex2.X - vertex1.X;
            edge1.Y = vertex2.Y - vertex1.Y;
            edge1.Z = vertex2.Z - vertex1.Z;

            //Edge2
            edge2.X = vertex3.X - vertex1.X;
            edge2.Y = vertex3.Y - vertex1.Y;
            edge2.Z = vertex3.Z - vertex1.Z;

            //Cross product of ray direction and edge2 - first part of determinant.
            Vector3 directioncrossedge2 = Vector3.Zero;
            directioncrossedge2.X = (rayDir.Y * edge2.Z) - (rayDir.Z * edge2.Y);
            directioncrossedge2.Y = (rayDir.Z * edge2.X) - (rayDir.X * edge2.Z);
            directioncrossedge2.Z = (rayDir.X * edge2.Y) - (rayDir.Y * edge2.X);

            //Compute the determinant.
            float determinant;
            //Dot product of edge1 and the first part of determinant.
            determinant = (edge1.X * directioncrossedge2.X) + (edge1.Y * directioncrossedge2.Y) + (edge1.Z * directioncrossedge2.Z);

            //If the ray is parallel to the triangle plane, there is no collision.
            //This also means that we are not culling, the ray may hit both the
            //back and the front of the triangle.
            if (determinant.IsZero())
            {
                distance = 0f;
                return false;
            }

            float inversedeterminant = 1.0f / determinant;

            //Calculate the U parameter of the intersection point.
            Vector3 distanceVector = Vector3.Zero;
            distanceVector.X = rayStart.X - vertex1.X;
            distanceVector.Y = rayStart.Y - vertex1.Y;
            distanceVector.Z = rayStart.Z - vertex1.Z;

            float triangleU;
            triangleU = (distanceVector.X * directioncrossedge2.X) + (distanceVector.Y * directioncrossedge2.Y) + (distanceVector.Z * directioncrossedge2.Z);
            triangleU *= inversedeterminant;

            //Make sure it is inside the triangle.
            if (triangleU < 0f || triangleU > 1f)
            {
                distance = 0f;
                return false;
            }

            //Calculate the V parameter of the intersection point.
            Vector3 distancecrossedge1 = Vector3.Zero;
            distancecrossedge1.X = (distanceVector.Y * edge1.Z) - (distanceVector.Z * edge1.Y);
            distancecrossedge1.Y = (distanceVector.Z * edge1.X) - (distanceVector.X * edge1.Z);
            distancecrossedge1.Z = (distanceVector.X * edge1.Y) - (distanceVector.Y * edge1.X);

            float triangleV;
            triangleV = ((rayDir.X * distancecrossedge1.X) + (rayDir.Y * distancecrossedge1.Y)) + (rayDir.Z * distancecrossedge1.Z);
            triangleV *= inversedeterminant;

            //Make sure it is inside the triangle.
            if (triangleV < 0f || triangleU + triangleV > 1f)
            {
                distance = 0f;
                return false;
            }

            //Compute the distance along the ray to the triangle.
            float raydistance;
            raydistance = (edge2.X * distancecrossedge1.X) + (edge2.Y * distancecrossedge1.Y) + (edge2.Z * distancecrossedge1.Z);
            raydistance *= inversedeterminant;

            //Is the triangle behind the ray origin?
            if (raydistance < 0f)
            {
                distance = 0f;
                return false;
            }

            distance = raydistance;
            return true;
        }

        /// <summary>
        /// Determines whether there is an intersection between a <see cref="Ray"/> and a triangle.
        /// </summary>
        /// <param name="ray">The ray to test.</param>
        /// <param name="vertex1">The first vertex of the triangle to test.</param>
        /// <param name="vertex2">The second vertex of the triangle to test.</param>
        /// <param name="vertex3">The third vertex of the triangle to test.</param>
        /// <param name="point">When the method completes, contains the point of intersection,
        /// or <see cref="Vector3.Zero"/> if there was no intersection.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static bool RayIntersectsTriangle(Vector3 rayStart, Vector3 rayDir, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3, out Vector3 point)
        {
            if (!RayIntersectsTriangle(rayStart, rayDir, vertex1, vertex2, vertex3, out float distance))
            {
                point = Vector3.Zero;
                return false;
            }

            point = rayStart + (rayDir * distance);
            return true;
        }
        public static bool RayIntersectsBoxDistance(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 boxHalfExtents, Matrix4x4 boxInverseTransform, out float distance)
        {
            //Transform ray to untransformed box space
            Vector3 rayEndPoint = rayStartPoint + rayDirection;
            rayStartPoint = Vector3.Transform(rayStartPoint, boxInverseTransform);
            rayEndPoint = Vector3.Transform(rayEndPoint, boxInverseTransform);
            rayDirection = rayEndPoint - rayStartPoint;
            return RayIntersectsAABBDistance(rayStartPoint, rayDirection, -boxHalfExtents, boxHalfExtents, out distance);
        }

        #region RayIntersectsAABBDistance
        public static bool RayIntersectsAABBDistance(Ray ray, AABB box, out float distance)
            => RayIntersectsAABBDistance(ray.StartPoint, ray.Direction, box.Min, box.Max, out distance);
        public static bool RayIntersectsAABBDistance(Ray ray, Vector3 boxMin, Vector3 boxMax, out float distance)
            => RayIntersectsAABBDistance(ray.StartPoint, ray.Direction, boxMin, boxMax, out distance);
        public static bool RayIntersectsAABBDistance(Vector3 rayStartPoint, Vector3 rayDirection, AABB box, out float distance)
             => RayIntersectsAABBDistance(rayStartPoint, rayDirection, box.Min, box.Max, out distance);
        public static bool RayIntersectsAABBDistance(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 boxMin, Vector3 boxMax, out float distance)
        {
            rayDirection = Vector3.Normalize(rayDirection);

            distance = 0.0f;
            float tmax = float.MaxValue;

            for (int i = 0; i < 3; ++i)
                if (rayDirection[i].IsZero())
                {
                    if (rayStartPoint[i] < boxMin[i] || rayStartPoint[i] > boxMax[i])
                    {
                        distance = 0.0f;
                        return false;
                    }
                }
                else
                {
                    float inverse = 1.0f / rayDirection[i];
                    float t1 = (boxMin[i] - rayStartPoint[i]) * inverse;
                    float t2 = (boxMax[i] - rayStartPoint[i]) * inverse;

                    if (t1 > t2)
                        (t2, t1) = (t1, t2);
                    
                    distance = Math.Max(t1, distance);
                    tmax = Math.Min(t2, tmax);

                    if (distance > tmax)
                    {
                        distance = 0.0f;
                        return false;
                    }
                }
            return true;
        }
        public static bool RayIntersectsAABBDistance(Ray ray, AABB aabb, out float nearDistance, out float farDistance)
            => RayIntersectsAABBDistance(ray.StartPoint, ray.Direction, aabb.Min, aabb.Max, out nearDistance, out farDistance);
        public static bool RayIntersectsAABBDistance(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 boxMin, Vector3 boxMax, out float nearDistance, out float farDistance)
        {
            rayDirection = Vector3.Normalize(rayDirection);

            nearDistance = 0.0f;
            farDistance = float.MaxValue;

            for (int i = 0; i < 3; ++i)
                if (rayDirection[i].IsZero())
                {
                    if (rayStartPoint[i] < boxMin[i] || rayStartPoint[i] > boxMax[i])
                    {
                        nearDistance = 0.0f;
                        farDistance = 0.0f;
                        return false;
                    }
                }
                else
                {
                    float inverse = 1.0f / rayDirection[i];
                    float t1 = (boxMin[i] - rayStartPoint[i]) * inverse;
                    float t2 = (boxMax[i] - rayStartPoint[i]) * inverse;

                    if (t1 > t2)
                        (t2, t1) = (t1, t2);

                    nearDistance = Math.Max(t1, nearDistance);
                    farDistance = Math.Min(t2, farDistance);

                    if (nearDistance > farDistance)
                    {
                        nearDistance = 0.0f;
                        farDistance = 0.0f;
                        return false;
                    }
                }

            return true;
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

        public static bool SegmentIntersectsAABB(Vector3 segmentStart, Vector3 segmentEnd, Vector3 boxMin, Vector3 boxMax, out Vector3 enterPoint, out Vector3 exitPoint)
        {
            enterPoint = segmentStart;
            exitPoint = segmentEnd;

            float tenter = 0.0f;
            float texit = 1.0f;

            if (!RaySlabIntersect(boxMin.X, boxMax.X, segmentStart.X, segmentEnd.X, ref tenter, ref texit) || 
                !RaySlabIntersect(boxMin.Y, boxMax.Y, segmentStart.Y, segmentEnd.Y, ref tenter, ref texit) || 
                !RaySlabIntersect(boxMin.Z, boxMax.Z, segmentStart.Z, segmentEnd.Z, ref tenter, ref texit))
                return false;

            enterPoint = Interp.Lerp(segmentStart, segmentEnd, tenter);
            exitPoint = Interp.Lerp(segmentStart, segmentEnd, texit);

            return true;
        }

        /// <summary>
        /// Determines whether there is an intersection between a <see cref="Ray"/> and a <see cref="System.Numerics.Plane"/>.
        /// </summary>
        /// <param name="ray">The ray to test.</param>
        /// <param name="box">The box to test.</param>
        /// <param name="point">When the method completes, contains the point of intersection,
        /// or <see cref="Vector3.Zero"/> if there was no intersection.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static bool RayIntersectsAABB(Ray ray, Vector3 boxMin, Vector3 boxMax, out Vector3 point)
        {
            if (!RayIntersectsAABBDistance(ray.StartPoint, ray.Direction, boxMin, boxMax, out float distance))
            {
                point = Vector3.Zero;
                return false;
            }

            point = ray.StartPoint + (ray.Direction * distance);
            return true;
        }
        public static bool RayIntersectsBox(Vector3 rayStartPoint, Vector3 rayDirection, Vector3 boxHalfExtents, Matrix4x4 boxInverseTransform, out Vector3 point)
        {
            if (!RayIntersectsBoxDistance(rayStartPoint, rayDirection, boxHalfExtents, boxInverseTransform, out float distance))
            {
                point = Vector3.Zero;
                return false;
            }

            point = rayStartPoint + (rayDirection * distance);
            return true;
        }

        /// <summary>
        /// Determines whether there is an intersection between a <see cref="Ray"/> and a <see cref="Sphere"/>.
        /// </summary>
        /// <param name="ray">The ray to test.</param>
        /// <param name="sphere">The sphere to test.</param>
        /// <param name="distance">When the method completes, contains the distance of the intersection,
        /// or 0 if there was no intersection.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static bool RayIntersectsSphere(Vector3 rayStart, Vector3 rayDir, Vector3 sphereCenter, float sphereRadius, out float distance)
        {
            //Source: Real-Time Collision Detection by Christer Ericson
            //Reference: Page 177

            rayDir = Vector3.Normalize(rayDir);

            Vector3 m = rayStart - sphereCenter;

            float b = Vector3.Dot(m, rayDir);
            float c = Vector3.Dot(m, m) - (sphereRadius * sphereRadius);

            if (c > 0f && b > 0f)
            {
                distance = 0f;
                return false;
            }

            float discriminant = b * b - c;

            if (discriminant < 0f)
            {
                distance = 0f;
                return false;
            }

            distance = -b - (float)Math.Sqrt(discriminant);

            if (distance < 0f)
                distance = 0f;

            return true;
        }

        /// <summary>
        /// Determines whether there is an intersection between a <see cref="Ray"/> and a <see cref="Sphere"/>. 
        /// </summary>
        /// <param name="ray">The ray to test.</param>
        /// <param name="sphere">The sphere to test.</param>
        /// <param name="point">When the method completes, contains the point of intersection,
        /// or <see cref="Vector3.Zero"/> if there was no intersection.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static bool RayIntersectsSphere(Vector3 rayStart, Vector3 rayDir, Vector3 sphereCenter, float sphereRadius, out Vector3 point)
        {
            if (!RayIntersectsSphere(rayStart, rayDir, sphereCenter, sphereRadius, out float distance))
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
        /// <param name="plane">The plane to test.</param>
        /// <param name="point">The point to test.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static EPlaneIntersection PlaneIntersectsPoint(Plane plane, Vector3 point)
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
        /// Determines whether there is an intersection between a <see cref="System.Numerics.Plane"/> and a <see cref="System.Numerics.Plane"/>.
        /// </summary>
        /// <param name="plane1">The first plane to test.</param>
        /// <param name="plane2">The second plane to test.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static bool PlaneIntersectsPlane(Plane plane1, Plane plane2)
        {
            Vector3 direction = Vector3.Cross(plane1.Normal, plane2.Normal);
            //If direction is the zero vector, the planes are parallel and possibly
            //coincident. It is not an intersection. The dot product will tell us.
            return !Vector3.Dot(direction, direction).IsZero();
        }

        /// <summary>
        /// Determines whether there is an intersection between a <see cref="System.Numerics.Plane"/> and a <see cref="System.Numerics.Plane"/>.
        /// </summary>
        /// <param name="plane1">The first plane to test.</param>
        /// <param name="plane2">The second plane to test.</param>
        /// <param name="line">When the method completes, contains the line of intersection
        /// as a <see cref="Ray"/>, or a zero ray if there was no intersection.</param>
        /// <returns>Whether the two objects intersected.</returns>
        /// <remarks>
        /// Although a ray is set to have an origin, the ray returned by this method is really
        /// a line in three dimensions which has no real origin. The ray is considered valid when
        /// both the positive direction is used and when the negative direction is used.
        /// </remarks>
        public static bool PlaneIntersectsPlane(Plane plane1, Plane plane2, out Ray line)
        {
            //Source: Real-Time Collision Detection by Christer Ericson
            //Reference: Page 207

            Vector3 direction = Vector3.Cross(plane1.Normal, plane2.Normal);

            //If direction is the zero vector, the planes are parallel and possibly
            //coincident. It is not an intersection. The dot product will tell us.
            float denominator = Vector3.Dot(direction, direction);

            //We assume the planes are normalized, therefore the denominator
            //only serves as a parallel and coincident check. Otherwise we need
            //to divide the point by the denominator.
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
        /// <param name="plane">The plane to test.</param>
        /// <param name="vertex1">The first vertex of the triangle to test.</param>
        /// <param name="vertex2">The second vertex of the triangle to test.</param>
        /// <param name="vertex3">The third vertex of the triangle to test.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static EPlaneIntersection PlaneIntersectsTriangle(Plane plane, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3)
        {
            //Source: Real-Time Collision Detection by Christer Ericson
            //Reference: Page 207

            EPlaneIntersection test1 = PlaneIntersectsPoint(plane, vertex1);
            EPlaneIntersection test2 = PlaneIntersectsPoint(plane, vertex2);
            EPlaneIntersection test3 = PlaneIntersectsPoint(plane, vertex3);

            if (test1 == EPlaneIntersection.Front && test2 == EPlaneIntersection.Front && test3 == EPlaneIntersection.Front)
                return EPlaneIntersection.Front;

            if (test1 == EPlaneIntersection.Back && test2 == EPlaneIntersection.Back && test3 == EPlaneIntersection.Back)
                return EPlaneIntersection.Back;

            return EPlaneIntersection.Intersecting;
        }
        public static EPlaneIntersection PlaneIntersectsBox(Plane plane, Vector3 boxMin, Vector3 boxMax, Matrix4x4 boxInverseMatrix)
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
        /// <param name="plane">The plane to test.</param>
        /// <param name="sphere">The sphere to test.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static EPlaneIntersection PlaneIntersectsSphere(Plane plane, Sphere sphere)
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

        /* This implementation is wrong
        /// <summary>
        /// Determines whether there is an intersection between a <see cref="SharpDX.Box"/> and a triangle.
        /// </summary>
        /// <param name="box">The box to test.</param>
        /// <param name="vertex1">The first vertex of the triangle to test.</param>
        /// <param name="vertex2">The second vertex of the triangle to test.</param>
        /// <param name="vertex3">The third vertex of the triangle to test.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static bool BoxIntersectsTriangle(Box box, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3)
        {
            if (BoxContainsPoint(box, vertex1) == EContainment.Contains)
                return true;
            if (BoxContainsPoint(box, vertex2) == EContainment.Contains)
                return true;
            if (BoxContainsPoint(box, vertex3) == EContainment.Contains)
                return true;
            return false;
        }
        */

        /// <summary>
        /// Determines whether there is an intersection between a <see cref="Box"/> and a <see cref="Box"/>.
        /// </summary>
        /// <param name="box1">The first box to test.</param>
        /// <param name="box2">The second box to test.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static bool AABBIntersectsAABB(AABB box1, AABB box2)
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
        /// <param name="box">The box to test.</param>
        /// <param name="sphere">The sphere to test.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static bool BoxIntersectsSphere(Vector3 boxHalfExtents, Matrix4x4 boxInverseTransform, Vector3 sphereCenter, float sphereRadius)
        {
            sphereCenter = Vector3.Transform(sphereCenter, boxInverseTransform);
            return AABBIntersectsSphere(-boxHalfExtents, boxHalfExtents, sphereCenter, sphereRadius);
        }

        public static bool AABBIntersectsSphere(Vector3 boxMin, Vector3 boxMax, Vector3 sphereCenter, float sphereRadius)
            => Vector3.DistanceSquared(sphereCenter, Vector3.Clamp(sphereCenter, boxMin, boxMax)) <= sphereRadius * sphereRadius;

        /// <summary>
        /// Determines whether there is an intersection between a <see cref="Sphere"/> and a triangle.
        /// </summary>
        /// <param name="sphere">The sphere to test.</param>
        /// <param name="vertex1">The first vertex of the triangle to test.</param>
        /// <param name="vertex2">The second vertex of the triangle to test.</param>
        /// <param name="vertex3">The third vertex of the triangle to test.</param>
        /// <returns>Whether the two objects intersected.</returns>
        public static bool SphereIntersectsTriangle(Vector3 sphereCenter, float sphereRadius, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3)
        {
            //Source: Real-Time Collision Detection by Christer Ericson
            //Reference: Page 167

            Vector3 point = ClosestPointPointTriangle(sphereCenter, vertex1, vertex2, vertex3);
            Vector3 v = point - sphereCenter;

            return v.LengthSquared() <= sphereRadius * sphereRadius;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="sphere1Center"></param>
        /// <param name="sphere1Radius"></param>
        /// <param name="sphere2Center"></param>
        /// <param name="sphere2Radius"></param>
        /// <returns></returns>
        public static bool SphereIntersectsSphere(Vector3 sphere1Center, float sphere1Radius, Vector3 sphere2Center, float sphere2Radius)
        {
            float radiisum = sphere1Radius + sphere2Radius;
            return Vector3.DistanceSquared(sphere1Center, sphere2Center) <= radiisum * radiisum;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="boxHalfExtents"></param>
        /// <param name="boxInverseTransform"></param>
        /// <param name="point"></param>
        /// <returns></returns>
        public static bool BoxContainsPoint(Vector3 boxHalfExtents, Matrix4x4 boxInverseTransform, Vector3 point)
        {
            //Transform point into untransformed box space
            point = Vector3.Transform(point, boxInverseTransform);
            return AABBContainsPoint(-boxHalfExtents, boxHalfExtents, point);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="boxMin"></param>
        /// <param name="boxMax"></param>
        /// <param name="point"></param>
        /// <returns></returns>
        public static bool AABBContainsPoint(Vector3 boxMin, Vector3 boxMax, Vector3 point)
        {
            if (boxMin.X <= point.X && boxMax.X >= point.X &&
                boxMin.Y <= point.Y && boxMax.Y >= point.Y &&
                boxMin.Z <= point.Z && boxMax.Z >= point.Z)
                return true;

            return false;
        }
        /* This implementation is wrong
        /// <summary>
        /// Determines whether a <see cref="SharpDX.Box"/> contains a triangle.
        /// </summary>
        /// <param name="box">The box to test.</param>
        /// <param name="vertex1">The first vertex of the triangle to test.</param>
        /// <param name="vertex2">The second vertex of the triangle to test.</param>
        /// <param name="vertex3">The third vertex of the triangle to test.</param>
        /// <returns>The type of containment the two objects have.</returns>
        public static EContainment BoxContainsTriangle(Box box, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3)
        {
            EContainment test1 = BoxContainsPoint(box, vertex1);
            EContainment test2 = BoxContainsPoint(box, vertex2);
            EContainment test3 = BoxContainsPoint(box, vertex3);
            if (test1 == EContainment.Contains && test2 == EContainment.Contains && test3 == EContainment.Contains)
                return EContainment.Contains;
            if (test1 == EContainment.Contains || test2 == EContainment.Contains || test3 == EContainment.Contains)
                return EContainment.Intersects;
            return EContainment.Disjoint;
        }
        */
        /// <summary>
        /// 
        /// </summary>
        /// <param name="box1Min"></param>
        /// <param name="box1Max"></param>
        /// <param name="box2Min"></param>
        /// <param name="box2Max"></param>
        /// <returns></returns>
        public static EContainment AABBContainsAABB(Vector3 box1Min, Vector3 box1Max, Vector3 box2Min, Vector3 box2Max)
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
        /// <summary>
        /// 
        /// </summary>
        /// <param name="boxMin"></param>
        /// <param name="boxMax"></param>
        /// <param name="sphereCenter"></param>
        /// <param name="sphereRadius"></param>
        /// <returns></returns>
        public static EContainment AABBContainsSphere(Vector3 boxMin, Vector3 boxMax, Vector3 sphereCenter, float sphereRadius)
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
        /// Determines whether a <see cref="Sphere"/> contains a point.
        /// </summary>
        /// <param name="sphere">The sphere to test.</param>
        /// <param name="point">The point to test.</param>
        /// <returns>The type of containment the two objects have.</returns>
        public static bool SphereContainsPoint(Vector3 center, float radius, Vector3 point)
            => Vector3.DistanceSquared(point, center) <= radius * radius;

        /// <summary>
        /// Determines whether a <see cref="Sphere"/> contains a triangle.
        /// </summary>
        /// <param name="sphere">The sphere to test.</param>
        /// <param name="vertex1">The first vertex of the triangle to test.</param>
        /// <param name="vertex2">The second vertex of the triangle to test.</param>
        /// <param name="vertex3">The third vertex of the triangle to test.</param>
        /// <returns>The type of containment the two objects have.</returns>
        public static EContainment SphereContainsTriangle(Sphere sphere, Vector3 vertex1, Vector3 vertex2, Vector3 vertex3)
        {
            //Source: Jorgy343
            //Reference: None

            bool test1 = SphereContainsPoint(sphere.Center, sphere.Radius, vertex1);
            bool test2 = SphereContainsPoint(sphere.Center, sphere.Radius, vertex2);
            bool test3 = SphereContainsPoint(sphere.Center, sphere.Radius, vertex3);

            if (test1 && test2 && test3)
                return EContainment.Contains;

            return SphereIntersectsTriangle(sphere.Center, sphere.Radius, vertex1, vertex2, vertex3)
                ? EContainment.Intersects
                : EContainment.Disjoint;
        }

        public static EContainment SphereContainsBox(
            Vector3 sphereCenter,
            float sphereRadius,
            Vector3 boxHalfExtents,
            Matrix4x4 boxInverseTransform)
        {
            if (!BoxIntersectsSphere(boxHalfExtents, boxInverseTransform, sphereCenter, sphereRadius))
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
        /// <param name="sphere1">The first sphere to test.</param>
        /// <param name="sphere2">The second sphere to test.</param>
        /// <returns>The type of containment the two objects have.</returns>
        public static EContainment SphereContainsSphere(Vector3 sphere1Center, float sphere1Radius, Vector3 sphere2Center, float sphere2Radius)
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
        public static EContainment FrustumContainsSphere(Frustum frustum, Vector3 center, float radius)
        {
            //if (frustum.UseBoundingSphere)
            //{
            //    EContainment c = SphereContainsSphere(frustum.BoundingSphere.Center, frustum.BoundingSphere.Radius, center, radius);
            //    if (c == EContainment.Disjoint)
            //        return EContainment.Disjoint;
            //    //If the bounding sphere intersects, could intersect the frustum or be disjoint with the frustum, so more checks needed
            //}

            float distance;
            EContainment type = EContainment.Contains;
            foreach (Plane p in frustum)
            {
                distance = DistancePlanePoint(p, center);
                if (distance < -radius)
                    return EContainment.Disjoint;
                else if (distance < radius)
                    type = EContainment.Intersects;
            }
            return type;
        }
        public static bool FrustumContainsPoint(Frustum frustum, Vector3 point)
        {
            //if (frustum.UseBoundingSphere)
            //{
            //    EContainment c = SphereContainsSphere(frustum.BoundingSphere.Center, frustum.BoundingSphere.Radius, center, radius);
            //    if (c == EContainment.Disjoint)
            //        return EContainment.Disjoint;
            //    //If the bounding sphere intersects, could intersect the frustum or be disjoint with the frustum, so more checks needed
            //}

            foreach (Plane p in frustum)
                if (DistancePlanePoint(p, point) < 0)
                    return false;
            return true;
        }
        public static EContainment FrustumContainsBox1(Frustum frustum, Vector3 boxHalfExtents, Matrix4x4 boxTransform)
        {
            //if (frustum.UseBoundingSphere)
            //{
            //    EContainment c = SphereContainsBox(frustum.BoundingSphere.Center, frustum.BoundingSphere.Radius, boxHalfExtents, boxTransform);
            //    if (c == EContainment.Disjoint)
            //        return EContainment.Disjoint;
            //    //If the bounding sphere intersects, could intersect the frustum or be disjoint with the frustum, so more checks needed
            //}

            EContainment result = EContainment.Contains;
            int numOut, numIn;
            Vector3[] corners = AABB.GetCorners(boxHalfExtents, x => Vector3.Transform(x, boxTransform));
            foreach (Plane p in frustum)
            {
                numOut = 0;
                numIn = 0;
                for (int i = 0; i < 8 && (numIn == 0 || numOut == 0); i++)
                    if (DistancePlanePoint(p, corners[i]) < 0)
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
        public static EContainment AABBContainsFrustum(Vector3 boxMin, Vector3 boxMax, Frustum frustum)
        {
            //if (frustum.UseBoundingSphere)
            //{
            //    EContainment c = AABBContainsSphere(boxMin, boxMax, frustum.BoundingSphere.Center, frustum.BoundingSphere.Radius);
            //    if (c == EContainment.Disjoint)
            //        return EContainment.Disjoint;
            //    //If the bounding sphere intersects, could intersect the frustum or be disjoint with the frustum, so more checks needed
            //}

            int numIn = 0, numOut = 0;
            foreach (Vector3 v in frustum.Corners)
                if (AABBContainsPoint(boxMin, boxMax, v))
                    ++numIn;
                else
                    ++numOut;
            return numOut == 0 ? EContainment.Contains : numIn == 0 ? EContainment.Disjoint : EContainment.Intersects;
        }
        public static EContainment FrustumContainsAABB(Frustum frustum, Vector3 boxMin, Vector3 boxMax)
        {
            EContainment c = AABBContainsFrustum(boxMin, boxMax, frustum);
            if (c != EContainment.Disjoint)
                return EContainment.Intersects;
            //else if (c == EContainment.Contains)
            //    return EContainment.ContainedWithin;

            EContainment result = EContainment.Contains;
            int numOut, numIn;
            Vector3[] corners = AABB.GetCorners(boxMin, boxMax);
            foreach (Plane p in frustum)
            {
                numOut = 0;
                numIn = 0;
                for (int i = 0; i < 8 && (numIn == 0 || numOut == 0); i++)
                    if (DistancePlanePoint(p, corners[i]) < 0)
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

        public static bool TryIntersectFrustaAabb(PreparedFrustum frustumA, PreparedFrustum frustumB, out Vector3 aabbMin, out Vector3 aabbMax)
            => FrustumIntersection.TryIntersectFrustaAabb(frustumA, frustumB, out aabbMin, out aabbMax);

        public static bool TryIntersectFrustaAabb(Frustum frustumA, Frustum frustumB, out Vector3 aabbMin, out Vector3 aabbMax)
            => FrustumIntersection.TryIntersectFrustaAabb(
                PreparedFrustum.FromFrustum(frustumA),
                PreparedFrustum.FromFrustum(frustumB),
                out aabbMin,
                out aabbMax);

        public static Vector3 Intersection(Plane plane1, Plane plane2, Plane plane3)
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

        public static Capsule.ESegmentPart GetDistancePointToSegmentPart(Vector3 startPoint, Vector3 endPoint, Vector3 point, out float closestPartDist)
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

        public static Vector3 SegmentClosestColinearPointToPoint(Vector3 start, Vector3 end, Vector3 point)
        {
            Vector3 v = end - start;
            float t = Vector3.Dot(point - start, v) / v.LengthSquared();
            return start + v * t;
        }

        public static Vector3 RayClosestColinearPointToPoint(Vector3 start, Vector3 dir, Vector3 point)
        {
            float t = Vector3.Dot(point - start, dir);
            return start + dir * t;
        }

        public static float SegmentShortestDistanceToPoint(Vector3 start, Vector3 end, Vector3 point)
        {
            Vector3 v = end - start;
            float t = Vector3.Dot(point - start, v) / v.LengthSquared();
            if (t < 0.0f)
                return Vector3.Distance(point, start);
            if (t > 1.0f)
                return Vector3.Distance(point, end);
            return Vector3.Distance(point, start + v * t);
        }

        public static EContainment FrustumContainsCone(Frustum frustum, Vector3 center, Vector3 up, float height, float radius)
        {
            throw new NotImplementedException();
        }

        public static bool SegmentIntersectsPlane(Vector3 start, Vector3 end, float d, Vector3 normal, out Vector3 intersectionPoint)
        {
            Vector3 ab = end - start;
            float t = (d - Vector3.Dot(start, normal)) / Vector3.Dot(ab, normal);
            if (t >= 0.0f && t <= 1.0f)
            {
                intersectionPoint = start + ab * t;
                return true;
            }
            intersectionPoint = Vector3.Zero;
            return false;
        }

        public enum EBetweenPlanes
        {
            NormalsFacing,
            NormalsAway,
            DontCare
        }

        public static bool PointIsBetweenPlanes(Vector3 point, Plane far, Plane left, EBetweenPlanes comp)
        {
            float farDist = DistancePlanePoint(far, point);
            float leftDist = DistancePlanePoint(left, point);
            if (comp == EBetweenPlanes.NormalsFacing)
                return farDist > 0.0f && leftDist > 0.0f;
            if (comp == EBetweenPlanes.NormalsAway)
                return farDist < 0.0f && leftDist < 0.0f;
            return farDist * leftDist < 0.0f;
        }

        public static float SegmentDistanceToSegment(Vector3 s1Start, Vector3 s1End, Vector3 s2Start, Vector3 s2End)
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
    }
    public enum EContainment
    {
        Contains,
        Disjoint,
        Intersects
    }
}