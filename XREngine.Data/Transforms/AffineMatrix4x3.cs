using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace XREngine.Data.Transforms
{
    /// <summary>
    /// Compact affine matrix that follows System.Numerics row-vector conventions.
    /// The omitted fourth column is always [0, 0, 0, 1].
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct AffineMatrix4x3 : IEquatable<AffineMatrix4x3>
    {
        public readonly float M11;
        public readonly float M12;
        public readonly float M13;

        public readonly float M21;
        public readonly float M22;
        public readonly float M23;

        public readonly float M31;
        public readonly float M32;
        public readonly float M33;

        public readonly float M41;
        public readonly float M42;
        public readonly float M43;

        public static AffineMatrix4x3 Identity { get; } = new(
            1.0f, 0.0f, 0.0f,
            0.0f, 1.0f, 0.0f,
            0.0f, 0.0f, 1.0f,
            0.0f, 0.0f, 0.0f);

        public AffineMatrix4x3(
            float m11, float m12, float m13,
            float m21, float m22, float m23,
            float m31, float m32, float m33,
            float m41, float m42, float m43)
        {
            M11 = m11;
            M12 = m12;
            M13 = m13;
            M21 = m21;
            M22 = m22;
            M23 = m23;
            M31 = m31;
            M32 = m32;
            M33 = m33;
            M41 = m41;
            M42 = m42;
            M43 = m43;
        }

        public Vector3 Translation
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new(M41, M42, M43);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix4x3 CreateTranslation(Vector3 translation)
            => new(
                1.0f, 0.0f, 0.0f,
                0.0f, 1.0f, 0.0f,
                0.0f, 0.0f, 1.0f,
                translation.X, translation.Y, translation.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix4x3 CreateScale(Vector3 scale)
            => new(
                scale.X, 0.0f, 0.0f,
                0.0f, scale.Y, 0.0f,
                0.0f, 0.0f, scale.Z,
                0.0f, 0.0f, 0.0f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix4x3 CreateFromQuaternion(Quaternion quaternion)
        {
            quaternion = Quaternion.Normalize(quaternion);

            float xx = quaternion.X * quaternion.X;
            float yy = quaternion.Y * quaternion.Y;
            float zz = quaternion.Z * quaternion.Z;
            float xy = quaternion.X * quaternion.Y;
            float xz = quaternion.X * quaternion.Z;
            float yz = quaternion.Y * quaternion.Z;
            float wx = quaternion.W * quaternion.X;
            float wy = quaternion.W * quaternion.Y;
            float wz = quaternion.W * quaternion.Z;

            return new AffineMatrix4x3(
                1.0f - 2.0f * (yy + zz), 2.0f * (xy + wz), 2.0f * (xz - wy),
                2.0f * (xy - wz), 1.0f - 2.0f * (xx + zz), 2.0f * (yz + wx),
                2.0f * (xz + wy), 2.0f * (yz - wx), 1.0f - 2.0f * (xx + yy),
                0.0f, 0.0f, 0.0f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix4x3 CreateTRS(Vector3 scale, Quaternion rotation, Vector3 translation)
        {
            AffineMatrix4x3 rotationMatrix = CreateFromQuaternion(rotation);
            return new AffineMatrix4x3(
                rotationMatrix.M11 * scale.X, rotationMatrix.M12 * scale.X, rotationMatrix.M13 * scale.X,
                rotationMatrix.M21 * scale.Y, rotationMatrix.M22 * scale.Y, rotationMatrix.M23 * scale.Y,
                rotationMatrix.M31 * scale.Z, rotationMatrix.M32 * scale.Z, rotationMatrix.M33 * scale.Z,
                translation.X, translation.Y, translation.Z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAffine(in Matrix4x4 matrix, float epsilon = 1e-5f)
            => MathF.Abs(matrix.M14) <= epsilon
            && MathF.Abs(matrix.M24) <= epsilon
            && MathF.Abs(matrix.M34) <= epsilon
            && MathF.Abs(matrix.M44 - 1.0f) <= epsilon;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryFromMatrix4x4(in Matrix4x4 matrix, out AffineMatrix4x3 affine, float epsilon = 1e-5f)
        {
            if (!IsAffine(matrix, epsilon))
            {
                affine = default;
                return false;
            }

            affine = FromMatrix4x4(matrix);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix4x3 FromMatrix4x4(in Matrix4x4 matrix)
            => new(
                matrix.M11, matrix.M12, matrix.M13,
                matrix.M21, matrix.M22, matrix.M23,
                matrix.M31, matrix.M32, matrix.M33,
                matrix.M41, matrix.M42, matrix.M43);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Matrix4x4 ToMatrix4x4()
            => new(
                M11, M12, M13, 0.0f,
                M21, M22, M23, 0.0f,
                M31, M32, M33, 0.0f,
                M41, M42, M43, 1.0f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 TransformDirection(Vector3 value)
        {
            if (Sse.IsSupported)
                return TransformVectorSse(value, includeTranslation: false);

            return new Vector3(
                value.X * M11 + value.Y * M21 + value.Z * M31,
                value.X * M12 + value.Y * M22 + value.Z * M32,
                value.X * M13 + value.Y * M23 + value.Z * M33);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 TransformPosition(Vector3 value)
        {
            if (Sse.IsSupported)
                return TransformVectorSse(value, includeTranslation: true);

            return new Vector3(
                value.X * M11 + value.Y * M21 + value.Z * M31 + M41,
                value.X * M12 + value.Y * M22 + value.Z * M32 + M42,
                value.X * M13 + value.Y * M23 + value.Z * M33 + M43);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix4x3 Multiply(in AffineMatrix4x3 left, in AffineMatrix4x3 right)
        {
            if (Sse.IsSupported)
                return MultiplySse(left, right);

            Vector3 row1 = new(
                left.M11 * right.M11 + left.M12 * right.M21 + left.M13 * right.M31,
                left.M11 * right.M12 + left.M12 * right.M22 + left.M13 * right.M32,
                left.M11 * right.M13 + left.M12 * right.M23 + left.M13 * right.M33);
            Vector3 row2 = new(
                left.M21 * right.M11 + left.M22 * right.M21 + left.M23 * right.M31,
                left.M21 * right.M12 + left.M22 * right.M22 + left.M23 * right.M32,
                left.M21 * right.M13 + left.M22 * right.M23 + left.M23 * right.M33);
            Vector3 row3 = new(
                left.M31 * right.M11 + left.M32 * right.M21 + left.M33 * right.M31,
                left.M31 * right.M12 + left.M32 * right.M22 + left.M33 * right.M32,
                left.M31 * right.M13 + left.M32 * right.M23 + left.M33 * right.M33);
            Vector3 translation = new(
                left.M41 * right.M11 + left.M42 * right.M21 + left.M43 * right.M31 + right.M41,
                left.M41 * right.M12 + left.M42 * right.M22 + left.M43 * right.M32 + right.M42,
                left.M41 * right.M13 + left.M42 * right.M23 + left.M43 * right.M33 + right.M43);

            return new AffineMatrix4x3(
                row1.X, row1.Y, row1.Z,
                row2.X, row2.Y, row2.Z,
                row3.X, row3.Y, row3.Z,
                translation.X, translation.Y, translation.Z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Invert(in AffineMatrix4x3 value, out AffineMatrix4x3 inverted)
        {
            float c11 = value.M22 * value.M33 - value.M23 * value.M32;
            float c12 = value.M13 * value.M32 - value.M12 * value.M33;
            float c13 = value.M12 * value.M23 - value.M13 * value.M22;
            float c21 = value.M23 * value.M31 - value.M21 * value.M33;
            float c22 = value.M11 * value.M33 - value.M13 * value.M31;
            float c23 = value.M13 * value.M21 - value.M11 * value.M23;
            float c31 = value.M21 * value.M32 - value.M22 * value.M31;
            float c32 = value.M12 * value.M31 - value.M11 * value.M32;
            float c33 = value.M11 * value.M22 - value.M12 * value.M21;

            float determinant = value.M11 * c11 + value.M12 * c21 + value.M13 * c31;
            if (MathF.Abs(determinant) <= 1e-8f)
            {
                inverted = default;
                return false;
            }

            float invDet = 1.0f / determinant;
            float i11 = c11 * invDet;
            float i12 = c12 * invDet;
            float i13 = c13 * invDet;
            float i21 = c21 * invDet;
            float i22 = c22 * invDet;
            float i23 = c23 * invDet;
            float i31 = c31 * invDet;
            float i32 = c32 * invDet;
            float i33 = c33 * invDet;

            float t1 = -(value.M41 * i11 + value.M42 * i21 + value.M43 * i31);
            float t2 = -(value.M41 * i12 + value.M42 * i22 + value.M43 * i32);
            float t3 = -(value.M41 * i13 + value.M42 * i23 + value.M43 * i33);

            inverted = new AffineMatrix4x3(
                i11, i12, i13,
                i21, i22, i23,
                i31, i32, i33,
                t1, t2, t3);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix4x3 operator *(AffineMatrix4x3 left, AffineMatrix4x3 right)
            => Multiply(left, right);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator Matrix4x4(AffineMatrix4x3 value)
            => value.ToMatrix4x4();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator AffineMatrix4x3(Matrix4x4 value)
            => FromMatrix4x4(value);

        public override bool Equals(object? obj)
            => obj is AffineMatrix4x3 other && Equals(other);

        public bool Equals(AffineMatrix4x3 other)
            => M11.Equals(other.M11)
            && M12.Equals(other.M12)
            && M13.Equals(other.M13)
            && M21.Equals(other.M21)
            && M22.Equals(other.M22)
            && M23.Equals(other.M23)
            && M31.Equals(other.M31)
            && M32.Equals(other.M32)
            && M33.Equals(other.M33)
            && M41.Equals(other.M41)
            && M42.Equals(other.M42)
            && M43.Equals(other.M43);

        public override int GetHashCode()
            => HashCode.Combine(
                HashCode.Combine(M11, M12, M13, M21),
                HashCode.Combine(M22, M23, M31, M32),
                HashCode.Combine(M33, M41, M42, M43));

        public override string ToString()
            => $"{{ {{M11:{M11}, M12:{M12}, M13:{M13}}} {{M21:{M21}, M22:{M22}, M23:{M23}}} {{M31:{M31}, M32:{M32}, M33:{M33}}} {{M41:{M41}, M42:{M42}, M43:{M43}}} }}";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static AffineMatrix4x3 MultiplySse(in AffineMatrix4x3 left, in AffineMatrix4x3 right)
        {
            Vector128<float> column1 = Vector128.Create(right.M11, right.M12, right.M13, 0.0f);
            Vector128<float> column2 = Vector128.Create(right.M21, right.M22, right.M23, 0.0f);
            Vector128<float> column3 = Vector128.Create(right.M31, right.M32, right.M33, 0.0f);
            Vector128<float> translation = Vector128.Create(right.M41, right.M42, right.M43, 0.0f);

            Vector128<float> row1 = MultiplyRowSse(left.M11, left.M12, left.M13, column1, column2, column3, includeTranslation: false, translation);
            Vector128<float> row2 = MultiplyRowSse(left.M21, left.M22, left.M23, column1, column2, column3, includeTranslation: false, translation);
            Vector128<float> row3 = MultiplyRowSse(left.M31, left.M32, left.M33, column1, column2, column3, includeTranslation: false, translation);
            Vector128<float> row4 = MultiplyRowSse(left.M41, left.M42, left.M43, column1, column2, column3, includeTranslation: true, translation);

            return new AffineMatrix4x3(
                row1.GetElement(0), row1.GetElement(1), row1.GetElement(2),
                row2.GetElement(0), row2.GetElement(1), row2.GetElement(2),
                row3.GetElement(0), row3.GetElement(1), row3.GetElement(2),
                row4.GetElement(0), row4.GetElement(1), row4.GetElement(2));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Vector3 TransformVectorSse(Vector3 value, bool includeTranslation)
        {
            Vector128<float> result = includeTranslation
                ? Vector128.Create(M41, M42, M43, 0.0f)
                : Vector128<float>.Zero;

            Vector128<float> x = Vector128.Create(value.X);
            Vector128<float> y = Vector128.Create(value.Y);
            Vector128<float> z = Vector128.Create(value.Z);

            Vector128<float> column1 = Vector128.Create(M11, M12, M13, 0.0f);
            Vector128<float> column2 = Vector128.Create(M21, M22, M23, 0.0f);
            Vector128<float> column3 = Vector128.Create(M31, M32, M33, 0.0f);

            if (Fma.IsSupported)
            {
                result = Fma.MultiplyAdd(x, column1, result);
                result = Fma.MultiplyAdd(y, column2, result);
                result = Fma.MultiplyAdd(z, column3, result);
            }
            else
            {
                result = Sse.Add(result, Sse.Multiply(x, column1));
                result = Sse.Add(result, Sse.Multiply(y, column2));
                result = Sse.Add(result, Sse.Multiply(z, column3));
            }

            return new Vector3(result.GetElement(0), result.GetElement(1), result.GetElement(2));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<float> MultiplyRowSse(
            float x,
            float y,
            float z,
            Vector128<float> column1,
            Vector128<float> column2,
            Vector128<float> column3,
            bool includeTranslation,
            Vector128<float> translation)
        {
            Vector128<float> result = includeTranslation ? translation : Vector128<float>.Zero;
            Vector128<float> vx = Vector128.Create(x);
            Vector128<float> vy = Vector128.Create(y);
            Vector128<float> vz = Vector128.Create(z);

            if (Fma.IsSupported)
            {
                result = Fma.MultiplyAdd(vx, column1, result);
                result = Fma.MultiplyAdd(vy, column2, result);
                result = Fma.MultiplyAdd(vz, column3, result);
                return result;
            }

            result = Sse.Add(result, Sse.Multiply(vx, column1));
            result = Sse.Add(result, Sse.Multiply(vy, column2));
            result = Sse.Add(result, Sse.Multiply(vz, column3));
            return result;
        }
    }
}