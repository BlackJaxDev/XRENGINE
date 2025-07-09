using System.Numerics;

namespace Extensions
{
    public static class Vector3Extensions
    {
        //public static Vector3 ToNumerics(this Assimp.Vector3D vector)
        //    => new(vector.X, vector.Y, vector.Z);
        //public static Assimp.Vector3D ToAssimp(this Vector3 vector)
        //    => new(vector.X, vector.Y, vector.Z);

        /// <summary>
        /// Modifies each component of the vector using the provided function.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        public static Vector3 Each(this Vector3 value, Func<float, float> func) =>
            new(func(value.X), func(value.Y), func(value.Z));

        public static Vector3 Clamp(this Vector3 value, Vector3 min, Vector3 max) =>
            new(value.X.Clamp(min.X, max.X), value.Y.Clamp(min.Y, max.Y), value.Z.Clamp(min.Z, max.Z));

        public static Vector3 Abs(this Vector3 value) =>
            new(Math.Abs(value.X), Math.Abs(value.Y), Math.Abs(value.Z));

        public static Vector3 Cross(this Vector3 value, Vector3 other) =>
            Vector3.Cross(value, other);

        public static float Dot(this Vector3 value, Vector3 other) =>
            Vector3.Dot(value, other);

        public static float Distance(this Vector3 value, Vector3 other) =>
            Vector3.Distance(value, other);

        public static float DistanceSquared(this Vector3 value, Vector3 other) =>
            Vector3.DistanceSquared(value, other);

        public static Vector3 Lerp(this Vector3 value, Vector3 other, float amount) =>
            Vector3.Lerp(value, other, amount);

        public static Vector3 Max(this Vector3 value, Vector3 other) =>
            Vector3.Max(value, other);

        public static Vector3 Min(this Vector3 value, Vector3 other) =>
            Vector3.Min(value, other);

        public static Vector3 Normalized(this Vector3 value, bool safe = true, float safeLengthTolerance = 0.0001f)
        {
            if (!safe)
                return Vector3.Normalize(value);
           
            var len = value.Length();
            if (len < safeLengthTolerance)
                return Vector3.Zero;
            else
                return value / len;
        }

        public static bool TryNormalizeSafe(this Vector3 value, out Vector3 result, float safeLengthTolerance = 0.0001f)
        {
            var len = value.Length();
            if (len < safeLengthTolerance)
            {
                result = Vector3.Zero;
                return false;
            }
            else
            {
                result = value / len;
                return true;
            }
        }

        public static Vector3 Reflect(this Vector3 value, Vector3 normal) =>
            Vector3.Reflect(value, normal);

        public static Vector3 Transform(this Vector3 value, Matrix4x4 matrix) =>
            Vector3.Transform(value, matrix);

        public static Vector3 TransformNormal(this Vector3 value, Matrix4x4 matrix) =>
            Vector3.TransformNormal(value, matrix);

        public static Vector3 Transform(this Vector3 value, Quaternion rotation) =>
            Vector3.Transform(value, rotation);

        public static Vector2 XY(this Vector3 value) =>
            new(value.X, value.Y);

        public static Vector2 XZ(this Vector3 value) =>
            new(value.X, value.Z);

        public static Vector2 YZ(this Vector3 value) =>
            new(value.Y, value.Z);

        public static Vector2 YX(this Vector3 value) =>
            new(value.Y, value.X);

        public static Vector2 ZX(this Vector3 value) =>
            new(value.Z, value.X);

        public static Vector2 ZY(this Vector3 value) =>
            new(value.Z, value.Y);

        public static Vector4 XYZW(this Vector3 value, float w) =>
            new(value, w);

        public static Vector3 YXZ(this Vector3 value) =>
            new(value.Y, value.X, value.Z);

        public static Vector3 YZX(this Vector3 value) =>
            new(value.Y, value.Z, value.X);

        public static Vector3 ZXY(this Vector3 value) =>
            new(value.Z, value.X, value.Y);

        public static Vector3 ZYX(this Vector3 value) =>
            new(value.Z, value.Y, value.X);

        public static Vector3 XZY(this Vector3 value) =>
            new(value.X, value.Z, value.Y);

        public static Vector3 ClampMagnitude(this Vector3 value, float maxLength) =>
            value.LengthSquared() > maxLength * maxLength ? value.Normalized() * maxLength : value;
    }
}
