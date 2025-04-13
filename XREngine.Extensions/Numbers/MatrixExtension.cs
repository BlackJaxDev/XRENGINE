using System.Numerics;

namespace Extensions
{
    public static class MatrixExtension
    {
        //public static Matrix4x4 ToNumerics(this Assimp.Matrix4x4 matrix)
        //    => new(
        //        matrix.A1, matrix.A2, matrix.A3, matrix.A4,
        //        matrix.B1, matrix.B2, matrix.B3, matrix.B4,
        //        matrix.C1, matrix.C2, matrix.C3, matrix.C4,
        //        matrix.D1, matrix.D2, matrix.D3, matrix.D4);

        //public static Assimp.Matrix4x4 ToAssimp(this Matrix4x4 matrix)
        //    => new(
        //        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        //        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        //        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        //        matrix.M41, matrix.M42, matrix.M43, matrix.M44);

        public static Matrix4x4 Inverted(this Matrix4x4 matrix)
            => Matrix4x4.Invert(matrix, out Matrix4x4 result) ? result : Matrix4x4.Identity;

        public static Matrix4x4 Transposed(this Matrix4x4 matrix)
            => Matrix4x4.Transpose(matrix);

        public static Matrix4x4 Translate(this Matrix4x4 matrix, Vector3 translation)
        {
            matrix.M41 += translation.X;
            matrix.M42 += translation.Y;
            matrix.M43 += translation.Z;
            return matrix;
        }
        
        public static Vector3 Translation(this Matrix4x4 matrix)
            => new(matrix.M41, matrix.M42, matrix.M43);
        public static Vector3 Right(this Matrix4x4 matrix)
            => new(matrix.M11, matrix.M12, matrix.M13);
        public static Vector3 Up(this Matrix4x4 matrix)
            => new(matrix.M21, matrix.M22, matrix.M23);
        public static Vector3 Forward(this Matrix4x4 matrix)
            => new(matrix.M31, matrix.M32, matrix.M33);

        public static Quaternion ToQuaternion(this Matrix4x4 matrix)
        {
            matrix.Translation = Vector3.Zero; // Remove translation component
            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));

            //float trace = matrix.M11 + matrix.M22 + matrix.M33;
            //float w, x, y, z;
            //if (trace > 0)
            //{
            //    float s = 0.5f / (float)Math.Sqrt(trace + 1.0f);
            //    w = 0.25f / s;
            //    x = (matrix.M32 - matrix.M23) * s;
            //    y = (matrix.M13 - matrix.M31) * s;
            //    z = (matrix.M21 - matrix.M12) * s;
            //}
            //else if (matrix.M11 > matrix.M22 && matrix.M11 > matrix.M33)
            //{
            //    float s = 2.0f * (float)Math.Sqrt(1.0f + matrix.M11 - matrix.M22 - matrix.M33);
            //    w = (matrix.M32 - matrix.M23) / s;
            //    x = 0.25f * s;
            //    y = (matrix.M12 + matrix.M21) / s;
            //    z = (matrix.M13 + matrix.M31) / s;
            //}
            //else if (matrix.M22 > matrix.M33)
            //{
            //    float s = 2.0f * (float)Math.Sqrt(1.0f + matrix.M22 - matrix.M11 - matrix.M33);
            //    w = (matrix.M13 - matrix.M31) / s;
            //    x = (matrix.M12 + matrix.M21) / s;
            //    y = 0.25f * s;
            //    z = (matrix.M23 + matrix.M32) / s;
            //}
            //else
            //{
            //    float s = 2.0f * (float)Math.Sqrt(1.0f + matrix.M33 - matrix.M11 - matrix.M22);
            //    w = (matrix.M21 - matrix.M12) / s;
            //    x = (matrix.M13 + matrix.M31) / s;
            //    y = (matrix.M23 + matrix.M32) / s;
            //    z = 0.25f * s;
            //}
            //return new Quaternion(x, y, z, w);
        }
    }
}
