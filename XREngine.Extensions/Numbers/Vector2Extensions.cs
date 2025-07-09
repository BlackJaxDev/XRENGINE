using System.Numerics;

namespace Extensions
{
    public static class Vector2Extensions
    {
        //public static Vector2 ToNumerics(this Assimp.Vector2D vector)
        //    => new(vector.X, vector.Y);
        //public static Assimp.Vector2D ToAssimp(this Vector2 vector)
        //    => new(vector.X, vector.Y);

        /// <summary>
        /// Modifies each component of the vector using the provided function.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        public static Vector2 Each(this Vector2 value, Func<float, float> func) =>
            new(func(value.X), func(value.Y));

        public static Vector2 Clamp(this Vector2 value, Vector2 min, Vector2 max) =>
            new(value.X.Clamp(min.X, max.X), value.Y.Clamp(min.Y, max.Y));

        public static Vector2 Abs(this Vector2 value) =>
            new(Math.Abs(value.X), Math.Abs(value.Y));

        public static float Cross(this Vector2 value, Vector2 other) =>
            value.X * other.Y - value.Y * other.X;

        public static float Dot(this Vector2 value, Vector2 other) =>
            Vector2.Dot(value, other);

        public static float Distance(this Vector2 value, Vector2 other) =>
            Vector2.Distance(value, other);

        public static float DistanceSquared(this Vector2 value, Vector2 other) =>
            Vector2.DistanceSquared(value, other);

        public static Vector2 Lerp(this Vector2 value, Vector2 other, float amount) =>
            Vector2.Lerp(value, other, amount);

        public static Vector2 Max(this Vector2 value, Vector2 other) =>
            Vector2.Max(value, other);

        public static Vector2 Min(this Vector2 value, Vector2 other) =>
            Vector2.Min(value, other);

        public static Vector2 Normalize(this Vector2 value) =>
            Vector2.Normalize(value);

        public static Vector2 Reflect(this Vector2 value, Vector2 normal) =>
            Vector2.Reflect(value, normal);

        public static Vector2 Transform(this Vector2 value, Matrix3x2 matrix) =>
            Vector2.Transform(value, matrix);

        public static Vector2 Transform(this Vector2 value, Quaternion rotation) =>
            Vector2.Transform(value, rotation);

        public static Vector3 XYZ(this Vector2 value, float z) =>
            new(value, z);

        public static Vector2 ClampMagnitude(this Vector2 value, float maxLength) =>
            value.LengthSquared() > maxLength * maxLength ? value.Normalize() * maxLength : value;

        public static bool Contains(this Vector2 value, Vector2 point) =>
            value.X <= point.X && value.Y <= point.Y && value.X >= 0.0f && value.Y >= 0.0f;
    }
}
