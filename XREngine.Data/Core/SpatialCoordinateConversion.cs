using System;
using System.Numerics;
using XREngine.Data.Transforms;

namespace XREngine.Data.Core
{
    public enum SpatialAxis
    {
        PositiveX,
        NegativeX,
        PositiveY,
        NegativeY,
        PositiveZ,
        NegativeZ,
    }

    /// <summary>
    /// Describes how semantic right, up, and forward axes map to signed cartesian axes.
    /// </summary>
    public readonly struct SpatialCoordinateSystem : IEquatable<SpatialCoordinateSystem>
    {
        public static SpatialCoordinateSystem Engine { get; } = new(SpatialAxis.PositiveX, SpatialAxis.PositiveY, SpatialAxis.NegativeZ);
        public static SpatialCoordinateSystem OpenGL { get; } = Engine;
        public static SpatialCoordinateSystem OpenXR { get; } = Engine;
        public static SpatialCoordinateSystem OpenVR { get; } = Engine;
        public static SpatialCoordinateSystem XRightYUpZForward { get; } = new(SpatialAxis.PositiveX, SpatialAxis.PositiveY, SpatialAxis.PositiveZ);
        public static SpatialCoordinateSystem Unity { get; } = XRightYUpZForward;
        public static SpatialCoordinateSystem Mmd { get; } = XRightYUpZForward;
        public static SpatialCoordinateSystem Blender { get; } = new(SpatialAxis.PositiveX, SpatialAxis.PositiveZ, SpatialAxis.NegativeY);
        public static SpatialCoordinateSystem Unreal { get; } = new(SpatialAxis.PositiveY, SpatialAxis.PositiveZ, SpatialAxis.PositiveX);

        public SpatialCoordinateSystem(SpatialAxis right, SpatialAxis up, SpatialAxis forward)
        {
            Validate(right, up, forward);

            Right = right;
            Up = up;
            Forward = forward;
        }

        public SpatialAxis Right { get; }
        public SpatialAxis Up { get; }
        public SpatialAxis Forward { get; }

        public Vector3 RightVector => SpatialCoordinateConversion.AxisToVector(Right);
        public Vector3 UpVector => SpatialCoordinateConversion.AxisToVector(Up);
        public Vector3 ForwardVector => SpatialCoordinateConversion.AxisToVector(Forward);

        public Matrix4x4 BasisMatrix => SpatialCoordinateConversion.CreateBasisMatrix(this);

        public float Handedness
            => Vector3.Dot(Vector3.Cross(RightVector, UpVector), ForwardVector);

        public bool IsRightHanded => Handedness > 0.0f;
        public bool IsLeftHanded => Handedness < 0.0f;

        public bool Equals(SpatialCoordinateSystem other)
            => Right == other.Right
            && Up == other.Up
            && Forward == other.Forward;

        public override bool Equals(object? obj)
            => obj is SpatialCoordinateSystem other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine((int)Right, (int)Up, (int)Forward);

        public override string ToString()
            => $"Right={Right}, Up={Up}, Forward={Forward}";

        public static bool operator ==(SpatialCoordinateSystem left, SpatialCoordinateSystem right)
            => left.Equals(right);

        public static bool operator !=(SpatialCoordinateSystem left, SpatialCoordinateSystem right)
            => !left.Equals(right);

        private static void Validate(SpatialAxis right, SpatialAxis up, SpatialAxis forward)
        {
            int rightAxis = GetAxisIndex(right);
            int upAxis = GetAxisIndex(up);
            int forwardAxis = GetAxisIndex(forward);

            if (rightAxis == upAxis || rightAxis == forwardAxis || upAxis == forwardAxis)
                throw new ArgumentException("Coordinate system axes must use three distinct cartesian components.");
        }

        private static int GetAxisIndex(SpatialAxis axis)
            => axis switch
            {
                SpatialAxis.PositiveX or SpatialAxis.NegativeX => 0,
                SpatialAxis.PositiveY or SpatialAxis.NegativeY => 1,
                _ => 2,
            };
    }

    /// <summary>
    /// Converts spatial values between arbitrary orthonormal coordinate systems using System.Numerics row-vector conventions.
    /// </summary>
    public static class SpatialCoordinateConversion
    {
        public static Vector3 AxisToVector(SpatialAxis axis)
            => axis switch
            {
                SpatialAxis.PositiveX => Vector3.UnitX,
                SpatialAxis.NegativeX => -Vector3.UnitX,
                SpatialAxis.PositiveY => Vector3.UnitY,
                SpatialAxis.NegativeY => -Vector3.UnitY,
                SpatialAxis.PositiveZ => Vector3.UnitZ,
                _ => -Vector3.UnitZ,
            };

        public static Matrix4x4 CreateBasisMatrix(SpatialCoordinateSystem coordinateSystem)
        {
            Vector3 right = coordinateSystem.RightVector;
            Vector3 up = coordinateSystem.UpVector;
            Vector3 forward = coordinateSystem.ForwardVector;

            return new Matrix4x4(
                right.X, right.Y, right.Z, 0.0f,
                up.X, up.Y, up.Z, 0.0f,
                forward.X, forward.Y, forward.Z, 0.0f,
                0.0f, 0.0f, 0.0f, 1.0f);
        }

        public static Matrix4x4 GetVectorConversionMatrix(SpatialCoordinateSystem from, SpatialCoordinateSystem to)
            => CreateBasisMatrix(from) * Matrix4x4.Transpose(CreateBasisMatrix(to));

        public static AffineMatrix4x3 GetAffineConversionMatrix(SpatialCoordinateSystem from, SpatialCoordinateSystem to)
            => AffineMatrix4x3.FromMatrix4x4(GetVectorConversionMatrix(from, to));

        public static Vector3 Convert(Vector3 value, SpatialCoordinateSystem from, SpatialCoordinateSystem to)
            => ConvertDirection(value, from, to);

        public static Vector3 ConvertPosition(Vector3 value, SpatialCoordinateSystem from, SpatialCoordinateSystem to)
            => ConvertDirection(value, from, to);

        public static Vector3 ConvertDirection(Vector3 value, SpatialCoordinateSystem from, SpatialCoordinateSystem to)
            => Vector3.TransformNormal(value, GetVectorConversionMatrix(from, to));

        public static Vector3 ConvertNormal(Vector3 value, SpatialCoordinateSystem from, SpatialCoordinateSystem to)
            => ConvertDirection(value, from, to);

        public static Quaternion Convert(Quaternion value, SpatialCoordinateSystem from, SpatialCoordinateSystem to)
        {
            if (value.LengthSquared() <= float.Epsilon)
                return Quaternion.Identity;

            Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(value));
            Matrix4x4 converted = Convert(rotation, from, to);
            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(converted));
        }

        public static Matrix4x4 Convert(Matrix4x4 value, SpatialCoordinateSystem from, SpatialCoordinateSystem to)
        {
            Matrix4x4 fromTo = GetVectorConversionMatrix(from, to);
            Matrix4x4 toFrom = GetVectorConversionMatrix(to, from);
            return toFrom * value * fromTo;
        }

        public static AffineMatrix4x3 Convert(AffineMatrix4x3 value, SpatialCoordinateSystem from, SpatialCoordinateSystem to)
        {
            AffineMatrix4x3 fromTo = GetAffineConversionMatrix(from, to);
            AffineMatrix4x3 toFrom = GetAffineConversionMatrix(to, from);
            return toFrom * value * fromTo;
        }
    }
}