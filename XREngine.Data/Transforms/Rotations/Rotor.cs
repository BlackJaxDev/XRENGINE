using System.Numerics;

namespace XREngine.Data.Transforms.Rotations
{
    /// <summary>
    /// Rotor in 3D geometric algebra: a + Bivector.
    /// Use to rotate vectors: v' = R v R*.
    /// </summary>
    public struct Rotor
    {
        // ────────────────────────────────────────────────
        // Packed fields: X = A, Y = B01, Z = B02, W = B12
        // ────────────────────────────────────────────────
        private Vector4 _v;

        public float A { readonly get => _v.X; set => _v.X = value; }
        public float B01 { readonly get => _v.Y; set => _v.Y = value; }
        public float B02 { readonly get => _v.Z; set => _v.Z = value; }
        public float B12 { readonly get => _v.W; set => _v.W = value; }

        /// <summary>
        /// Full constructor.
        /// </summary>
        public Rotor(float a, float b01, float b02, float b12)
            => _v = new Vector4(a, b01, b02, b12);

        /// <summary>Dot product (SIMD!).</summary>
        public static float Dot(in Rotor r1, in Rotor r2)
            => Vector4.Dot(r1._v, r2._v);

        /// <summary>Squared length.</summary>
        public readonly float LengthSquared()
            => Vector4.Dot(_v, _v);

        /// <summary>Length.</summary>
        public readonly float Length()
            => _v.Length();

        /// <summary>Normalize in-place (SIMD).</summary>
        public void Normalize()
            => _v = Vector4.Normalize(_v);

        /// <summary>Return a normalized copy.</summary>
        public readonly Rotor Normalized()
        {
            var r = this;
            r.Normalize();
            return r;
        }

        /// <summary>Reverse (conjugate): a - bivector.</summary>
        public readonly Rotor Reverse() =>
            new(_v.X, -_v.Y, -_v.Z, -_v.W);

        /// <summary>
        /// Construct from scalar + bivector.
        /// </summary>
        public Rotor(float a, Bivector3 bv)
        {
            A = a;
            B01 = bv.B01;
            B02 = bv.B02;
            B12 = bv.B12;
        }

        /// <summary>
        /// Construct the rotor rotating unit vector from→to.
        /// </summary>
        public Rotor(Vector3 from, Vector3 to)
        {
            A = 1 + Vector3.Dot(to, from);
            var minusB = Bivector3.Wedge(to, from);
            B01 = minusB.B01;
            B02 = minusB.B02;
            B12 = minusB.B12;
            Normalize();
        }

        /// <summary>
        /// Construct from a normalized plane bivector and rotation angle (radians).
        /// </summary>
        public Rotor(Bivector3 plane, float angleRadians)
        {
            float half = angleRadians * 0.5f;
            float sina = MathF.Sin(half);
            A = MathF.Cos(half);
            B01 = -sina * plane.B01;
            B02 = -sina * plane.B02;
            B12 = -sina * plane.B12;
        }

        /// <summary>
        /// Rotate a vector: v' = R v R*.
        /// </summary>
        public readonly Vector3 Rotate(Vector3 v)
        {
            // q = R * v
            Vector3 q = new(
                A * v.X + v.Y * B01 + v.Z * B02,
                A * v.Y - v.X * B01 + v.Z * B12,
                A * v.Z - v.X * B02 - v.Y * B12
            );
            // trivector part
            float q012 = v.X * B12 - v.Y * B02 + v.Z * B01;
            // r = q * R*
            return new Vector3(
                A * q.X + q.Y * B01 + q.Z * B02 + q012 * B12,
                A * q.Y - q.X * B01 - q012 * B02 + q.Z * B12,
                A * q.Z + q012 * B01 - q.X * B02 - q.Y * B12
            );
        }

        /// <summary>
        /// Geometric (Clifford) product of two rotors.
        /// </summary>
        public static Rotor operator *(Rotor p, Rotor q)
            => new(
                p.A * q.A - p.B01 * q.B01 - p.B02 * q.B02 - p.B12 * q.B12,
                p.B01 * q.A + p.A * q.B01 + p.B12 * q.B02 - p.B02 * q.B12,
                p.B02 * q.A + p.A * q.B02 - p.B12 * q.B01 + p.B01 * q.B12,
                p.B12 * q.A + p.A * q.B12 + p.B02 * q.B01 - p.B01 * q.B02
            );

        /// <summary>
        /// In‐place multiply.
        /// </summary>
        public void MultiplyInPlace(Rotor r)
            => this *= r;

        /// <summary>
        /// Convert rotor to rotation matrix.
        /// </summary>
        public readonly Matrix4x4 ToMatrix()
        {
            Vector3 xy = Rotate(new Vector3(1, 0, 0));
            Vector3 xz = Rotate(new Vector3(0, 1, 0));
            Vector3 yz = Rotate(new Vector3(0, 0, 1));

            //TODO: double-check row or column major
            return new Matrix4x4(
                xy.X, xz.X, yz.X, 0,
                xy.Y, xz.Y, yz.Y, 0,
                xy.Z, xz.Z, yz.Z, 0,
                0, 0, 0, 1
            );
        }

        /// <summary>
        /// Quick geometric product of two vectors: generates a rotor.
        /// </summary>
        public static Rotor Geo(Vector3 a, Vector3 b)
            => new(Vector3.Dot(a, b), Bivector3.Wedge(a, b));

        /// <summary>Identity rotor (no rotation).</summary>
        public static readonly Rotor Identity = new(1, 0, 0, 0);

        /// <summary>Dot product of two rotors.</summary>
        public static float Dot(Rotor r1, Rotor r2)
            => r1.A * r2.A + r1.B01 * r2.B01 + r1.B02 * r2.B02 + r1.B12 * r2.B12;

        /// <summary>Inverse of this rotor (undo rotation).</summary>
        public readonly Rotor Inverse()
        {
            float norm2 = LengthSquared();
            return new Rotor(
                A / norm2,
               -B01 / norm2,
               -B02 / norm2,
               -B12 / norm2
            );
        }

        /// <summary>Convert this Rotor to a Quaternion (X,Y,Z,W).</summary>
        public readonly Quaternion ToQuaternion()
            => new(B12, B02, B01, A);

        /// <summary>Create a Rotor from a Quaternion.</summary>
        public static Rotor FromQuaternion(Quaternion q)
            => new(q.W, q.Z, q.Y, q.X);

        /// <summary>Create a Rotor from an axis & angle (radians).</summary>
        public static Rotor FromAxisAngle(Vector3 axis, float angle)
        {
            axis = Vector3.Normalize(axis);
            var half = angle * 0.5f;
            float s = MathF.Sin(half);
            // quaternion formula: (cos(θ/2), axis·sin(θ/2)) 
            return new Rotor(
                MathF.Cos(half),
                axis.Z * s,   // B01 ← q.Z
                axis.Y * s,   // B02 ← q.Y
                axis.X * s    // B12 ← q.X
            ).Normalized();
        }

        /// <summary>Extract axis & angle (radians) from this Rotor.</summary>
        public readonly void ToAxisAngle(out Vector3 axis, out float angle)
        {
            var q = Quaternion.Normalize(ToQuaternion());
            angle = 2 * MathF.Acos(q.W);
            float s = MathF.Sqrt(1 - q.W * q.W);
            if (s < 1e-4f)
                axis = new Vector3(1, 0, 0); // arbitrary
            else
                axis = new Vector3(q.X / s, q.Y / s, q.Z / s);
        }

        /// <summary>Normalized Linear Interpolation (NLERP).</summary>
        public static Rotor Nlerp(Rotor a, Rotor b, float t)
            => new Rotor(
                a.A * (1 - t) + b.A * t,
                a.B01 * (1 - t) + b.B01 * t,
                a.B02 * (1 - t) + b.B02 * t,
                a.B12 * (1 - t) + b.B12 * t
            ).Normalized();

        /// <summary>Spherical Linear Interpolation (SLERP).</summary>
        public static Rotor Slerp(Rotor a, Rotor b, float t)
        {
            float cosHalfTheta = Dot(a, b);
            if (cosHalfTheta < 0)
            {
                b = new Rotor(-b.A, -b.B01, -b.B02, -b.B12);
                cosHalfTheta = -cosHalfTheta;
            }
            if (MathF.Abs(cosHalfTheta) >= 1.0f)
                return a;

            float halfTheta = MathF.Acos(cosHalfTheta);
            float sinHalfTheta = MathF.Sqrt(1 - cosHalfTheta * cosHalfTheta);

            if (MathF.Abs(sinHalfTheta) < 1e-3f)
                return Nlerp(a, b, t);

            float ratioA = MathF.Sin((1 - t) * halfTheta) / sinHalfTheta;
            float ratioB = MathF.Sin(t * halfTheta) / sinHalfTheta;

            return new Rotor(
                a.A * ratioA + b.A * ratioB,
                a.B01 * ratioA + b.B01 * ratioB,
                a.B02 * ratioA + b.B02 * ratioB,
                a.B12 * ratioA + b.B12 * ratioB
            );
        }
    }
}