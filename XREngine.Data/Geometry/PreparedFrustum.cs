using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Plane = System.Numerics.Plane;

namespace XREngine.Data.Geometry
{
    /// <summary>
    /// Pre-baked frustum representation optimized for repeated intersection tests.
    /// Build once per frustum update and reuse for queries.
    /// </summary>
    public sealed class PreparedFrustum
    {
        public readonly Plane[] Planes;   // 6 planes
        public readonly Vector3[] Corners; // 8 corners

        public readonly int PlaneCount;

        // Structure of arrays layout for intrinsics
        public readonly float[] Nx;
        public readonly float[] Ny;
        public readonly float[] Nz;
        public readonly float[] D;

        // Bounding sphere from corners
        public readonly Vector3 SphereCenter;
        public readonly float SphereRadius;

        public PreparedFrustum(Plane[] planes, Vector3[] corners)
        {
            if (planes == null || planes.Length != 6)
                throw new ArgumentException("Frustum must have 6 planes.", nameof(planes));
            if (corners == null || corners.Length != 8)
                throw new ArgumentException("Frustum must have 8 corners.", nameof(corners));

            Planes = planes;
            Corners = corners;
            PlaneCount = planes.Length;

            Nx = new float[PlaneCount];
            Ny = new float[PlaneCount];
            Nz = new float[PlaneCount];
            D = new float[PlaneCount];

            for (int i = 0; i < PlaneCount; i++)
            {
                Nx[i] = planes[i].Normal.X;
                Ny[i] = planes[i].Normal.Y;
                Nz[i] = planes[i].Normal.Z;
                D[i] = planes[i].D;
            }

            Vector3 center = Vector3.Zero;
            for (int i = 0; i < corners.Length; i++)
                center += corners[i];
            center /= corners.Length;

            float maxR2 = 0.0f;
            for (int i = 0; i < corners.Length; i++)
            {
                float d2 = Vector3.DistanceSquared(center, corners[i]);
                if (d2 > maxR2)
                    maxR2 = d2;
            }

            SphereCenter = center;
            SphereRadius = MathF.Sqrt(maxR2);
        }

        public static PreparedFrustum FromFrustum(Frustum frustum)
        {
            Plane[] planes = new Plane[6];
            Vector3[] corners = new Vector3[8];

            for (int i = 0; i < planes.Length; i++)
                planes[i] = frustum.Planes[i];

            for (int i = 0; i < corners.Length; i++)
                corners[i] = frustum.Corners[i];

            return new PreparedFrustum(planes, corners);
        }

        public ReadOnlySpan<float> NxSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Nx;
        }

        public ReadOnlySpan<float> NySpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Ny;
        }

        public ReadOnlySpan<float> NzSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Nz;
        }

        public ReadOnlySpan<float> DSpan
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => D;
        }

    public static class FrustumIntersection
    {
        /// <summary>
        /// Compute the AABB of the intersection of two frusta.
        /// Returns true if there is an intersection volume, false if disjoint.
        /// Plane normals must point inside their frustum (n·x + d >= 0 for inside).
        /// </summary>
        public static bool TryIntersectFrustaAabb(
            PreparedFrustum a,
            PreparedFrustum b,
            out Vector3 aabbMin,
            out Vector3 aabbMax)
        {
            const float planeEps = 1e-4f;
            const float pointEps = 1e-4f;
            const float denomEps = 1e-6f;

            aabbMin = default;
            aabbMax = default;

            if (AreFrustaClearlyDisjoint(a, b))
                return false;

            if (IsFrustumCompletelyOutside(a.Planes, b.Corners, planeEps) ||
                IsFrustumCompletelyOutside(b.Planes, a.Corners, planeEps))
            {
                return false;
            }

            const int totalPlanes = 12;
            Span<Plane> allPlanes = stackalloc Plane[totalPlanes];
            for (int i = 0; i < 6; i++)
            {
                allPlanes[i] = a.Planes[i];
                allPlanes[i + 6] = b.Planes[i];
            }

            Span<float> nx = stackalloc float[totalPlanes];
            Span<float> ny = stackalloc float[totalPlanes];
            Span<float> nz = stackalloc float[totalPlanes];
            Span<float> dd = stackalloc float[totalPlanes];

            for (int i = 0; i < totalPlanes; i++)
            {
                nx[i] = allPlanes[i].Normal.X;
                ny[i] = allPlanes[i].Normal.Y;
                nz[i] = allPlanes[i].Normal.Z;
                dd[i] = allPlanes[i].D;
            }

            List<Vector3> candidates = new(64);

            for (int i = 0; i < a.Corners.Length; i++)
            {
                Vector3 p = a.Corners[i];
                if (ContainsPointIntrinsics(p, a.NxSpan, a.NySpan, a.NzSpan, a.DSpan, a.PlaneCount, planeEps) &&
                    ContainsPointIntrinsics(p, b.NxSpan, b.NySpan, b.NzSpan, b.DSpan, b.PlaneCount, planeEps))
                {
                    AddUniquePoint(candidates, p, pointEps);
                }
            }

            for (int i = 0; i < b.Corners.Length; i++)
            {
                Vector3 p = b.Corners[i];
                if (ContainsPointIntrinsics(p, a.NxSpan, a.NySpan, a.NzSpan, a.DSpan, a.PlaneCount, planeEps) &&
                    ContainsPointIntrinsics(p, b.NxSpan, b.NySpan, b.NzSpan, b.DSpan, b.PlaneCount, planeEps))
                {
                    AddUniquePoint(candidates, p, pointEps);
                }
            }

            for (int i = 0; i < totalPlanes; i++)
            {
                for (int j = i + 1; j < totalPlanes; j++)
                {
                    for (int k = j + 1; k < totalPlanes; k++)
                    {
                        int inA = ((i < 6) ? 1 : 0) + ((j < 6) ? 1 : 0) + ((k < 6) ? 1 : 0);
                        if (inA == 0 || inA == 3)
                            continue;

                        if (TryIntersectThreePlanes(allPlanes[i], allPlanes[j], allPlanes[k], denomEps, out Vector3 p))
                        {
                            if (ContainsPointIntrinsics(p, nx, ny, nz, dd, totalPlanes, planeEps))
                                AddUniquePoint(candidates, p, pointEps);
                        }
                    }
                }
            }

            if (candidates.Count == 0)
                return false;

            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);

            for (int i = 0; i < candidates.Count; i++)
            {
                Vector3 p = candidates[i];
                if (p.X < min.X) min.X = p.X;
                if (p.Y < min.Y) min.Y = p.Y;
                if (p.Z < min.Z) min.Z = p.Z;
                if (p.X > max.X) max.X = p.X;
                if (p.Y > max.Y) max.Y = p.Y;
                if (p.Z > max.Z) max.Z = p.Z;
            }

            aabbMin = min;
            aabbMax = max;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AreFrustaClearlyDisjoint(PreparedFrustum a, PreparedFrustum b)
        {
            Vector3 delta = a.SphereCenter - b.SphereCenter;
            float dist2 = delta.LengthSquared();
            float radiusSum = a.SphereRadius + b.SphereRadius;
            float radiusSum2 = radiusSum * radiusSum;
            return dist2 > radiusSum2;
        }

        private static bool IsFrustumCompletelyOutside(Plane[] planes, Vector3[] corners, float eps)
        {
            float negEps = -eps;
            for (int i = 0; i < planes.Length; i++)
            {
                bool allOutside = true;
                Plane plane = planes[i];

                for (int j = 0; j < corners.Length; j++)
                {
                    float dist = SignedDistance(plane, corners[j]);
                    if (dist >= negEps)
                    {
                        allOutside = false;
                        break;
                    }
                }

                if (allOutside)
                    return true;
            }

            return false;
        }

        private static unsafe bool ContainsPointIntrinsics(
            Vector3 p,
            ReadOnlySpan<float> nx,
            ReadOnlySpan<float> ny,
            ReadOnlySpan<float> nz,
            ReadOnlySpan<float> d,
            int planeCount,
            float eps)
        {
            float negEps = -eps;

            if (planeCount == 0)
                return true;

            if (Avx.IsSupported && planeCount >= 8)
            {
                Vector256<float> pxVec = Vector256.Create(p.X);
                Vector256<float> pyVec = Vector256.Create(p.Y);
                Vector256<float> pzVec = Vector256.Create(p.Z);
                Vector256<float> negEpsVec = Vector256.Create(negEps);

                fixed (float* pNx = nx)
                fixed (float* pNy = ny)
                fixed (float* pNz = nz)
                fixed (float* pD = d)
                {
                    int i = 0;
                    int simdCount = planeCount - (planeCount % 8);

                    for (; i < simdCount; i += 8)
                    {
                        Vector256<float> nxv = Avx.LoadVector256(pNx + i);
                        Vector256<float> nyv = Avx.LoadVector256(pNy + i);
                        Vector256<float> nzv = Avx.LoadVector256(pNz + i);
                        Vector256<float> dv = Avx.LoadVector256(pD + i);

                        Vector256<float> dist = Avx.Add(
                            Avx.Add(Avx.Multiply(nxv, pxVec), Avx.Multiply(nyv, pyVec)),
                            Avx.Add(Avx.Multiply(nzv, pzVec), dv));

                        Vector256<float> cmp = Avx.Compare(dist, negEpsVec, FloatComparisonMode.OrderedLessThanNonSignaling);
                        int mask = Avx.MoveMask(cmp);
                        if (mask != 0)
                            return false;
                    }

                    for (; i < planeCount; i++)
                    {
                        float dist =
                            pNx[i] * p.X +
                            pNy[i] * p.Y +
                            pNz[i] * p.Z +
                            pD[i];

                        if (dist < negEps)
                            return false;
                    }
                }

                return true;
            }

            if (Sse.IsSupported && planeCount >= 4)
            {
                Vector128<float> pxVec = Vector128.Create(p.X);
                Vector128<float> pyVec = Vector128.Create(p.Y);
                Vector128<float> pzVec = Vector128.Create(p.Z);
                Vector128<float> negEpsVec = Vector128.Create(negEps);

                fixed (float* pNx = nx)
                fixed (float* pNy = ny)
                fixed (float* pNz = nz)
                fixed (float* pD = d)
                {
                    int i = 0;
                    int simdCount = planeCount - (planeCount % 4);

                    for (; i < simdCount; i += 4)
                    {
                        Vector128<float> nxv = Sse.LoadVector128(pNx + i);
                        Vector128<float> nyv = Sse.LoadVector128(pNy + i);
                        Vector128<float> nzv = Sse.LoadVector128(pNz + i);
                        Vector128<float> dv = Sse.LoadVector128(pD + i);

                        Vector128<float> dist = Sse.Add(
                            Sse.Add(Sse.Multiply(nxv, pxVec), Sse.Multiply(nyv, pyVec)),
                            Sse.Add(Sse.Multiply(nzv, pzVec), dv));

                        Vector128<float> cmp = Sse.CompareLessThan(dist, negEpsVec);
                        int mask = Sse.MoveMask(cmp);
                        if (mask != 0)
                            return false;
                    }

                    for (; i < planeCount; i++)
                    {
                        float dist =
                            pNx[i] * p.X +
                            pNy[i] * p.Y +
                            pNz[i] * p.Z +
                            pD[i];

                        if (dist < negEps)
                            return false;
                    }
                }

                return true;
            }

            for (int i = 0; i < planeCount; i++)
            {
                float dist =
                    nx[i] * p.X +
                    ny[i] * p.Y +
                    nz[i] * p.Z +
                    d[i];

                if (dist < negEps)
                    return false;
            }

            return true;
        }

        private static bool TryIntersectThreePlanes(
            Plane p1, Plane p2, Plane p3,
            float denomEps,
            out Vector3 point)
        {
            Vector3 n1 = p1.Normal;
            Vector3 n2 = p2.Normal;
            Vector3 n3 = p3.Normal;

            Vector3 c23 = Vector3.Cross(n2, n3);
            float denom = Vector3.Dot(n1, c23);

            if (MathF.Abs(denom) < denomEps)
            {
                point = default;
                return false;
            }

            Vector3 c31 = Vector3.Cross(n3, n1);
            Vector3 c12 = Vector3.Cross(n1, n2);

            point = (-p1.D * c23 - p2.D * c31 - p3.D * c12) / denom;
            return true;
        }

        private static void AddUniquePoint(List<Vector3> points, Vector3 p, float eps)
        {
            float eps2 = eps * eps;
            for (int i = 0; i < points.Count; i++)
            {
                if (Vector3.DistanceSquared(points[i], p) <= eps2)
                    return;
            }
            points.Add(p);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float SignedDistance(Plane plane, Vector3 point)
            => Vector3.Dot(plane.Normal, point) + plane.D;
    }
    }
}
