using System.Numerics;

namespace Extensions
{
    public static class Vector4Extensions
    {
        //public static Vector4 ToNumerics(this Assimp.Color4D vector)
        //    => new(vector.R, vector.G, vector.B, vector.A);
        //public static Assimp.Color4D ToAssimp(this Vector4 vector)
        //    => new(vector.X, vector.Y, vector.Z, vector.W);

        /// <summary>
        /// Modifies each component of the vector using the provided function.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="func"></param>
        /// <returns></returns>
        public static Vector4 Each(this Vector4 value, Func<float, float> func) =>
            new(func(value.X), func(value.Y), func(value.Z), func(value.W));

        public static Vector4 Clamp(this Vector4 value, Vector4 min, Vector4 max) =>
            new(value.X.Clamp(min.X, max.X), value.Y.Clamp(min.Y, max.Y), value.Z.Clamp(min.Z, max.Z), value.W.Clamp(min.W, max.W));

        public static Vector4 Abs(this Vector4 value) =>
            new(Math.Abs(value.X), Math.Abs(value.Y), Math.Abs(value.Z), Math.Abs(value.W));

        public static float Dot(this Vector4 value, Vector4 other) =>
            Vector4.Dot(value, other);

        public static Vector4 Lerp(this Vector4 value, Vector4 other, float amount) =>
            Vector4.Lerp(value, other, amount);

        public static Vector4 Max(this Vector4 value, Vector4 other) =>
            Vector4.Max(value, other);

        public static Vector4 Min(this Vector4 value, Vector4 other) =>
            Vector4.Min(value, other);

        public static Vector4 Normalized(this Vector4 value) =>
            Vector4.Normalize(value);

        public static Vector4 Transform(this Vector4 value, Matrix4x4 matrix) =>
            Vector4.Transform(value, matrix);

        public static Vector4 Transform(this Vector4 value, Quaternion rotation) =>
            Vector4.Transform(value, rotation);

        public static Vector3 XYZ(this Vector4 value) =>
            new(value.X, value.Y, value.Z);

        public static Vector3 XZY(this Vector4 value) =>
            new(value.X, value.Z, value.Y);

        public static Vector3 YXZ(this Vector4 value) =>
            new(value.Y, value.X, value.Z);

        public static Vector3 YZX(this Vector4 value) =>
            new(value.Y, value.Z, value.X);

        public static Vector3 ZXY(this Vector4 value) =>
            new(value.Z, value.X, value.Y);

        public static Vector3 ZYX(this Vector4 value) =>
            new(value.Z, value.Y, value.X);

        public static Vector2 XY(this Vector4 value) =>
            new(value.X, value.Y);

        public static Vector2 XZ(this Vector4 value) =>
            new(value.X, value.Z);

        public static Vector2 XW(this Vector4 value) =>
            new(value.X, value.W);

        public static Vector2 YX(this Vector4 value) =>
            new(value.Y, value.X);

        public static Vector2 YZ(this Vector4 value) =>
            new(value.Y, value.Z);

        public static Vector2 YW(this Vector4 value) =>
            new(value.Y, value.W);

        public static Vector2 ZX(this Vector4 value) =>
            new(value.Z, value.X);

        public static Vector2 ZY(this Vector4 value) =>
            new(value.Z, value.Y);  

        public static Vector2 ZW(this Vector4 value) =>
            new(value.Z, value.W);

        public static Vector2 WX(this Vector4 value) =>
            new(value.W, value.X);

        public static Vector2 WY(this Vector4 value) =>
            new(value.W, value.Y);

        public static Vector2 WZ(this Vector4 value) =>
            new(value.W, value.Z);

        public static Vector3 XYZ(this Vector4 value, float w) =>
            new(value.X, value.Y, value.Z);

        public static Vector3 XZY(this Vector4 value, float w) =>
            new(value.X, value.Z, value.Y);

        public static Vector3 YXZ(this Vector4 value, float w) =>
            new(value.Y, value.X, value.Z);

        public static Vector3 YZX(this Vector4 value, float w) =>
            new(value.Y, value.Z, value.X);

        public static Vector3 ZXY(this Vector4 value, float w) =>
            new(value.Z, value.X, value.Y);

        public static Vector3 ZYX(this Vector4 value, float w) =>
            new(value.Z, value.Y, value.X);

        public static Vector4 ClampMagnitude(this Vector4 value, float maxLength) =>
            value.LengthSquared() > maxLength * maxLength ? value.Normalized() * maxLength : value;
    }
}
