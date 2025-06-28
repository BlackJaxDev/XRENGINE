using System.Numerics;

namespace Extensions
{
    public static class QuaternionExtension
    {
        public static Matrix4x4 ToMatrix(this Quaternion quaternion)
        {
            Matrix4x4 result = Matrix4x4.Identity;
            result.M11 = 1 - 2 * quaternion.Y * quaternion.Y - 2 * quaternion.Z * quaternion.Z;
            result.M12 = 2 * quaternion.X * quaternion.Y - 2 * quaternion.Z * quaternion.W;
            result.M13 = 2 * quaternion.X * quaternion.Z + 2 * quaternion.Y * quaternion.W;
            result.M21 = 2 * quaternion.X * quaternion.Y + 2 * quaternion.Z * quaternion.W;
            result.M22 = 1 - 2 * quaternion.X * quaternion.X - 2 * quaternion.Z * quaternion.Z;
            result.M23 = 2 * quaternion.Y * quaternion.Z - 2 * quaternion.X * quaternion.W;
            result.M31 = 2 * quaternion.X * quaternion.Z - 2 * quaternion.Y * quaternion.W;
            result.M32 = 2 * quaternion.Y * quaternion.Z + 2 * quaternion.X * quaternion.W;
            result.M33 = 1 - 2 * quaternion.X * quaternion.X - 2 * quaternion.Y * quaternion.Y;
            return result;
        }

        public static Quaternion Normalized(this Quaternion quaternion)
            => Quaternion.Normalize(quaternion);
        public static Vector3 Rotate(this Quaternion rotation, Vector3 vector)
            => Vector3.Transform(vector, rotation);
        public static Vector3 PosZ(this Quaternion rotation)
            => Vector3.Transform(Vector3.UnitZ, rotation);
        public static Vector3 PosY(this Quaternion rotation)
            => Vector3.Transform(Vector3.UnitY, rotation);
        public static Vector3 PosX(this Quaternion rotation)
            => Vector3.Transform(Vector3.UnitX, rotation);
        public static Vector3 NegZ(this Quaternion rotation)
            => Vector3.Transform(-Vector3.UnitZ, rotation);
        public static Vector3 NegY(this Quaternion rotation)
            => Vector3.Transform(-Vector3.UnitY, rotation);
        public static Vector3 NegX(this Quaternion rotation)
            => Vector3.Transform(-Vector3.UnitX, rotation);

        public static Quaternion WorldToLocal(this Quaternion worldRotation, Quaternion parentWorldRotation)
            => Quaternion.Inverse(parentWorldRotation) * worldRotation;
        public static Quaternion LocalToWorld(this Quaternion localRotation, Quaternion parentWorldRotation)
            => parentWorldRotation * localRotation;
    }
}
