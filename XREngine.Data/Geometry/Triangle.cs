using System;
using Extensions;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace XREngine.Data.Geometry
{
    public struct Triangle(Vector3 a, Vector3 b, Vector3 c) : IEquatable<Triangle>
    {
        public Vector3 A = a;
        public Vector3 B = b;
        public Vector3 C = c;

        public override readonly int GetHashCode()
            => HashCode.Combine(A, B, C);

        public override readonly bool Equals([NotNullWhen(true)] object? obj)
            => obj is Triangle t && A == t.A && B == t.B && C == t.C;

        public readonly Plane GetPlane()
            => Plane.CreateFromVertices(A, B, C);

        public readonly Vector3 GetNormal()
            => Vector3.Cross(B - A, C - A).Normalized();

        public void Flip()
            => (C, A) = (A, C);

        public readonly Vector3 ClosestPoint(Vector3 point)
        {
            Vector3 ab = B - A;
            Vector3 ac = C - A;
            Vector3 ap = point - A;

            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);

            if (d1 <= 0.0f && d2 <= 0.0f)
                return A;

            Vector3 bp = point - B;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);

            if (d3 >= 0.0f && d4 <= d3)
                return B;

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f)
            {
                float x = d1 / (d1 - d3);
                return A + x * ab;
            }

            Vector3 cp = point - C;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);

            if (d6 >= 0.0f && d5 <= d6)
                return C;

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f)
            {
                float x = d2 / (d2 - d6);
                return A + x * ac;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0.0f && (d4 - d3) >= 0.0f && (d5 - d6) >= 0.0f)
            {
                float x = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return B + x * (C - B);
            }

            float denom = 1.0f / (va + vb + vc);
            float v = vb * denom;
            float w = vc * denom;
            return A + ab * v + ac * w;
        }

        public readonly bool Equals(Triangle other)
            => A == other.A && B == other.B && C == other.C;

        public static bool operator ==(Triangle left, Triangle right)
            => left.Equals(right);
        public static bool operator !=(Triangle left, Triangle right)
            => !(left == right);

        /// <summary>
        /// Attempts to compute barycentric coordinates for the provided point relative to this triangle.
        /// </summary>
        public readonly bool TryGetBarycentricCoordinates(Vector3 point, out Vector3 barycentric)
        {
            Vector3 v0 = B - A;
            Vector3 v1 = C - A;
            Vector3 v2 = point - A;

            float d00 = Vector3.Dot(v0, v0);
            float d01 = Vector3.Dot(v0, v1);
            float d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0);
            float d21 = Vector3.Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (MathF.Abs(denom) <= 1e-8f)
            {
                barycentric = Vector3.Zero;
                return false;
            }

            float v = (d11 * d20 - d01 * d21) / denom;
            float w = (d00 * d21 - d01 * d20) / denom;
            float u = 1.0f - v - w;

            barycentric = new Vector3(u, v, w);
            return true;
        }
    }
}