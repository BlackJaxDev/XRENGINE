using Extensions;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Transforms.Rotations;
using YamlDotNet.Core.Tokens;
using static System.Math;

namespace XREngine.Data.Core
{
    public unsafe static class XRMath
    {
        /// <summary>
        /// A small number.
        /// </summary>
        public const float Epsilon = 0.00001f;
        /// <summary>
        /// 2 * PI represented as a float value.
        /// </summary>
        public static readonly float TwoPIf = 2.0f * PIf;
        /// <summary>
        /// 2 * PI represented as a double value.
        /// </summary>
        public static readonly double TwoPI = 2.0 * PI;
        /// <summary>
        /// PI represented as a float value.
        /// </summary>
        public const float PIf = 3.1415926535897931f;
        /// <summary>
        /// e represented as a double value.
        /// </summary>
        //public const double E = 2.7182818284590451;
        /// <summary>
        /// e represented as a float value.
        /// </summary>
        public const float Ef = 2.7182818284590451f;
        /// <summary>
        /// Multiply this constant by a degree value to convert to radians.
        /// </summary>
        public static readonly double DegToRadMult = PI / 180.0;
        /// <summary>
        /// Multiply this constant by a degree value to convert to radians.
        /// </summary>
        public static readonly float DegToRadMultf = PIf / 180.0f;
        /// <summary>
        /// Multiply this constant by a radian value to convert to degrees.
        /// </summary>
        public static readonly double RadToDegMult = 180.0 / PI;
        /// <summary>
        /// Multiply this constant by a radian value to convert to degrees.
        /// </summary>
        public static readonly float RadToDegMultf = 180.0f / PIf;
        /// <summary>
        /// Converts the given value in degrees to radians.
        /// </summary>
        public static double DegToRad(double degrees) => degrees * DegToRadMult;
        /// <summary>
        /// Converts the given value in radians to degrees.
        /// </summary>
        public static double RadToDeg(double radians) => radians * RadToDegMult;
        /// <summary>
        /// Converts the given value in degrees to radians.
        /// </summary>
        public static float DegToRad(float degrees) => degrees * DegToRadMultf;
        /// <summary>
        /// Converts the given value in radians to degrees.
        /// </summary>
        public static float RadToDeg(float radians) => radians * RadToDegMultf;
        /// <summary>
        /// Converts the given value in degrees to radians.
        /// </summary>
        public static Vector2 DegToRad(Vector2 degrees) => degrees * DegToRadMultf;
        /// <summary>
        /// Converts the given value in radians to degrees.
        /// </summary>
        public static Vector2 RadToDeg(Vector2 radians) => radians * RadToDegMultf;
        /// <summary>
        /// Converts the given value in degrees to radians.
        /// </summary>
        public static Vector3 DegToRad(Vector3 degrees) => degrees * DegToRadMultf;
        /// <summary>
        /// Converts the given value in radians to degrees.
        /// </summary>
        public static Vector3 RadToDeg(Vector3 radians) => radians * RadToDegMultf;

        /// <summary>
        /// Returns the most significant decimal digit.
        /// <para>250 -> 100</para>
        /// <para>12 -> 10</para>
        /// <para>5 -> 1</para>
        /// <para>0.5 -> 0.1</para>
        /// </summary>
        public static float MostSignificantDigit(float value)
        {
            float n = 1;

            float abs = Abs(value);
            float sig = Sign(value);

            if (abs > 1.0f)
            {
                while (n < abs)
                    n *= 10.0f;

                return (int)Floor(sig * n * 0.1f);
            }
            else // n <= 1
            {
                while (n > abs)
                    n *= 0.1f;

                return sig * n;
            }
        }

        public static float SqrtFast(float a, int iterations = 3)
        {
            float x = a * 0.5f;
            for (int i = 0; i < iterations; i++)
                x = 0.5f * (x + a / x);
            return x;
        }
        /// <summary>
        /// https://en.wikipedia.org/wiki/Fast_inverse_square_root
        /// </summary>
        /// <param name="x"></param>
        /// <returns></returns>
        public static unsafe float InvSqrtFast(float x)
        {
            const int magic = 0x5F3759DF;//0x5F375A86;
            int i = *(int*)&x;
            i = magic - (i >> 1);
            float y = *(float*)&i;
            return y * (1.5f - 0.5f * x * y * y);
        }

        public static void CartesianToPolarDeg(Vector2 vector, out float angle, out float radius)
        {
            radius = vector.Length();
            angle = Atan2df(vector.Y, vector.X);
        }
        public static void CartesianToPolarRad(Vector2 vector, out float angle, out float radius)
        {
            radius = vector.Length();
            angle = Atan2f(vector.Y, vector.X);
        }
        public static Vector2 PolarToCartesianDeg(float degree, float radius)
        {
            SinCosdf(degree, out float sin, out float cos);
            return new Vector2(cos * radius, sin * radius);
        }
        public static Vector2 PolarToCartesianRad(float radians, float radius)
        {
            SinCosf(radians, out float sin, out float cos);
            return new Vector2(cos * radius, sin * radius);
        }

        /// <summary>
        /// Returns a translation value representing a rotation of the cameraPoint around the focusPoint.
        /// Assumes the Globals.Up axis is up. Yaw is performed before pitch.
        /// </summary>
        /// <param name="pitch">Rotation about the X axis, after yaw.</param>
        /// <param name="yaw">Rotation about the Y axis.</param>
        /// <param name="focusPoint">The point to rotate around.</param>
        /// <param name="cameraPoint">The point to move.</param>
        /// <param name="cameraRightDir">The direction representing the right side of a camera. This is the reference axis rotated around (at the focusPoint) using the pitch value.</param>
        /// <returns></returns>
        public static Vector3 ArcballTranslation(
            float pitch,
            float yaw,
            Vector3 focusPoint,
            Vector3 cameraPoint,
            Vector3 cameraRightDir)
            => ArcballTranslation(
                Quaternion.CreateFromAxisAngle(cameraRightDir, DegToRad(pitch)) * Quaternion.CreateFromAxisAngle(Globals.Up, DegToRad(yaw)),
                focusPoint,
                cameraPoint);

        /// <summary>
        /// Returns a translation value representing a rotation of the cameraPoint around the focusPoint.
        /// Assumes the Y axis is up. Yaw is performed before pitch.
        /// </summary>
        /// <param name="rotation">Rotation about the X axis, after yaw.</param>
        /// <param name="focusPoint">The point to rotate around.</param>
        /// <param name="cameraPoint">The point to move.</param>
        /// <returns></returns>
        public static Vector3 ArcballTranslation(
            Quaternion rotation,
            Vector3 focusPoint,
            Vector3 cameraPoint)
            => focusPoint + Vector3.Transform(cameraPoint - focusPoint, rotation);

        /// <summary>
        /// Returns the sine and cosine of a radian angle simultaneously as doubles.
        /// </summary>
        public static void SinCos(double rad, out double sin, out double cos)
        {
            sin = Sin(rad);
            cos = Cos(rad);
        }
        /// <summary>
        /// Returns the sine and cosine of a radian angle simultaneously as floats.
        /// </summary>
        public static void SinCosf(float rad, out float sin, out float cos)
        {
            sin = Sinf(rad);
            cos = Cosf(rad);
        }
        /// <summary>
        /// Returns the sine and cosine of a degree angle simultaneously as doubles.
        /// </summary>
        public static void SinCosd(double deg, out double sin, out double cos)
        {
            sin = Sind(deg);
            cos = Cosd(deg);
        }
        /// <summary>
        /// Returns the sine and cosine of a degree angle simultaneously as floats.
        /// </summary>
        public static void SinCosdf(float deg, out float sin, out float cos)
        {
            sin = Sindf(deg);
            cos = Cosdf(deg);
        }

        /// <summary>
        /// Cosine as float, from radians
        /// </summary>
        /// <param name="rad"></param>
        /// <returns></returns>
        public static float Cosf(float rad) => (float)Cos(rad);
        /// <summary>
        /// Sine as float, from radians
        /// </summary>
        /// <param name="rad"></param>
        /// <returns></returns>
        public static float Sinf(float rad) => (float)Sin(rad);
        /// <summary>
        /// Tangent as float, from radians
        /// </summary>
        /// <param name="rad"></param>
        /// <returns></returns>
        public static float Tanf(float rad) => (float)Tan(rad);

        /// <summary>
        /// Cosine from degrees, as float
        /// </summary>
        /// <param name="deg"></param>
        /// <returns></returns>
        public static float Cosdf(float deg) => Cosf(deg * DegToRadMultf);
        /// <summary>
        /// Sine from degrees, as float
        /// </summary>
        /// <param name="deg"></param>
        /// <returns></returns>
        public static float Sindf(float deg) => Sinf(deg * DegToRadMultf);
        /// <summary>
        /// Tangent from degrees, as float
        /// </summary>
        /// <param name="deg"></param>
        /// <returns></returns>
        public static float Tandf(float deg) => Tanf(deg * DegToRadMultf);

        /// <summary>
        /// Arc cosine, as float. Returns radians
        /// </summary>
        /// <param name="cos"></param>
        /// <returns></returns>
        public static float Acosf(float cos) => (float)Acos(cos);
        /// <summary>
        /// Arc sine, as float. Returns radians
        /// </summary>
        /// <param name="sin"></param>
        /// <returns></returns>
        public static float Asinf(float sin) => (float)Asin(sin);
        /// <summary>
        /// Arc tangent, as float. Returns radians
        /// </summary>
        /// <param name="tan"></param>
        /// <returns></returns>
        public static float Atanf(float tan) => (float)Atan(tan);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="tanY"></param>
        /// <param name="tanX"></param>
        /// <returns></returns>
        public static float Atan2f(float tanY, float tanX) => (float)Atan2(tanY, tanX);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cos"></param>
        /// <returns></returns>
        public static float Acosdf(float cos) => Acosf(cos) * RadToDegMultf;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="sin"></param>
        /// <returns></returns>
        public static float Asindf(float sin) => Asinf(sin) * RadToDegMultf;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="tan"></param>
        /// <returns></returns>
        public static float Atandf(float tan) => Atanf(tan) * RadToDegMultf;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="tanY"></param>
        /// <param name="tanX"></param>
        /// <returns></returns>
        public static float Atan2df(float tanY, float tanX) => Atan2f(tanY, tanX) * RadToDegMultf;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="deg"></param>
        /// <returns></returns>
        public static double Cosd(double deg) => Cos(deg * DegToRadMult);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="deg"></param>
        /// <returns></returns>
        public static double Sind(double deg) => Sin(deg * DegToRadMult);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="deg"></param>
        /// <returns></returns>
        public static double Tand(double deg) => Tan(deg * DegToRadMult);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="value"></param>
        /// <param name="exponent"></param>
        /// <returns></returns>
        public static float Powf(float value, float exponent) => (float)Pow(value, exponent);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static float Sigmoidf(float value) => 1.0f / (1.0f + Powf(Ef, -value));
        /// <summary>
        /// 
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static double Sigmoid(double value) => 1.0 / (1.0 + Pow(E, -value));
        /// <summary>
        /// Finds the two values of x where the equation ax^2 + bx + c evaluates to 0.
        /// Returns false if the solutions are not a real numbers.
        /// </summary>
        public static bool QuadraticRealRoots(float a, float b, float c, out float x1, out float x2)
        {
            if (a != 0.0f)
            {
                float mag = b * b - 4.0f * a * c;
                if (mag >= 0.0f)
                {
                    mag = (float)Sqrt(mag);
                    a *= 2.0f;

                    x1 = (-b + mag) / a;
                    x2 = (-b - mag) / a;
                    return true;
                }
            }
            else if (b != 0.0f)
            {
                x1 = x2 = -c / b;
                return true;
            }
            else if (c != 0.0f)
            {
                x1 = 0.0f;
                x2 = 0.0f;
                return true;
            }
            x1 = 0.0f;
            x2 = 0.0f;
            return false;
        }
        /// <summary>
        /// Finds the two values of x where the equation ax^2 + bx + c evaluates to 0.
        /// </summary>
        public static bool QuadraticRoots(float a, float b, float c, out Complex x1, out Complex x2)
        {
            if (a != 0.0f)
            {
                float mag = b * b - 4.0f * a * c;

                a *= 2.0f;
                b /= a;

                if (mag >= 0.0f)
                {
                    mag = (float)Sqrt(mag) / a;

                    x1 = new Complex(-b + mag, 0.0);
                    x2 = new Complex(-b - mag, 0.0);
                }
                else
                {
                    mag = (float)Sqrt(-mag) / a;

                    x1 = new Complex(-b, mag);
                    x2 = new Complex(-b, -mag);
                }

                return true;
            }
            else if (b != 0.0f)
            {
                x1 = x2 = -c / b;
                return true;
            }
            else
            {
                x1 = x2 = 0.0f;
                return false;
            }
        }
        public static Vector3 Morph(Vector3 baseCoord, (Vector3 Position, float Weight)[] targets, bool relative = false)
        {
            if (relative)
            {
                Vector3 morphed = baseCoord;
                foreach (var (Position, Weight) in targets)
                    morphed += Position * Weight;
                return morphed;
            }
            else
            {
                Vector3 morphed = Vector3.Zero;
                float weightSum = 0.0f;
                foreach (var (Position, Weight) in targets)
                {
                    morphed += Position * Weight;
                    weightSum += Weight;
                }
                float invWeight = 1.0f - weightSum;
                return morphed + baseCoord * invWeight;
            }
        }
        /// <summary>
        /// Returns the angle in radians between two vectors.
        /// </summary>
        public static float AngleBetween(Vector3 vector1, Vector3 vector2)
        {
            vector1 = vector1.Normalized();
            vector2 = vector2.Normalized();

            float dot = Vector3.Dot(vector1, vector2);

            //dot is the cosine adj/hyp ratio between the two vectors, so
            //dot == 1 is same direction
            //dot == -1 is opposite direction
            //dot == 0 is a 90 degree angle

            if (dot > 0.999f)
                return 0.0f;
            else if (dot < -0.999f)
                return DegToRad(180.0f);
            else
                return MathF.Acos(dot);
        }
        /// <summary>
        /// Returns the rotation axis direction vector that is perpendicular to the two vectors.
        /// </summary>
        public static Vector3 AxisBetween(Vector3 initialVector, Vector3 finalVector)
        {
            initialVector = initialVector.Normalized();
            finalVector = finalVector.Normalized();

            float dot = Vector3.Dot(initialVector, finalVector);

            //dot is the cosine adj/hyp ratio between the two vectors, so
            //dot == 1 is same direction
            //dot == -1 is opposite direction
            //dot == 0 is a 90 degree angle

            return dot > 0.999f || dot < -0.999f
                ? Globals.Forward
                : Vector3.Cross(initialVector, finalVector);
        }
        /// <summary>
        /// Returns a rotation axis and angle in radians between two vectors.
        /// </summary>
        public static void AxisAngleBetween(Vector3 initialVector, Vector3 finalVector, out Vector3 axis, out float rad)
        {
            initialVector = initialVector.Normalized();
            finalVector = finalVector.Normalized();

            float dot = Vector3.Dot(initialVector, finalVector);

            //dot is the cosine adj/hyp ratio between the two vectors, so
            //dot == 1 is same direction
            //dot == -1 is opposite direction
            //dot == 0 is a 90 degree angle

            if (dot > 0.999f)
            {
                axis = Globals.Backward;
                rad = 0.0f;
            }
            else if (dot < -0.999f)
            {
                axis = -Globals.Backward;
                rad = DegToRad(180.0f);
            }
            else
            {
                axis = Vector3.Cross(initialVector, finalVector);
                rad = MathF.Acos(dot);
            }
        }

        /// <summary>
        /// Converts nonlinear normalized depth between 0.0f and 1.0f
        /// to a linear distance value between nearZ and farZ.
        /// </summary>
        public static float DepthToDistance(float depth, float nearZ, float farZ)
        {
            float depthSample = 2.0f * depth - 1.0f;
            float zLinear = 2.0f * nearZ * farZ / (farZ + nearZ - depthSample * (farZ - nearZ));
            return zLinear;
        }
        /// <summary>
        /// Converts a linear distance value between nearZ and farZ
        /// to nonlinear normalized depth between 0.0f and 1.0f.
        /// </summary>
        public static float DistanceToDepth(float z, float nearZ, float farZ)
        {
            float nonLinearDepth = (farZ + nearZ - 2.0f * nearZ * farZ / z.ClampMin(0.001f)) / (farZ - nearZ);
            nonLinearDepth = (nonLinearDepth + 1.0f) / 2.0f;
            return nonLinearDepth;
        }

        public static Vector3 JacobiMethod(Matrix4x4 inputMatrix, Vector3 expectedOutcome, int iterations)
        {
            Vector3 solvedVector = Vector3.Zero;
            for (int step = 0; step < iterations; ++step)
            {
                for (int row = 0; row < 3; ++row)
                {
                    float sigma = 0.0f;
                    for (int col = 0; col < 3; ++col)
                    {
                        if (col != row)
                            sigma += inputMatrix[row, col] * solvedVector[col];
                    }
                    solvedVector[row] = (expectedOutcome[row] - sigma) / inputMatrix[row, row];
                }
                //Engine.PrintLine("Step #" + step + ": " + solvedVector.ToString());
            }
            return solvedVector;
        }
        public static Vector4 JacobiMethod(Matrix4x4 inputMatrix, Vector4 expectedOutcome, int iterations)
        {
            Vector4 solvedVector = Vector4.Zero;
            for (int step = 0; step < iterations; ++step)
            {
                for (int row = 0; row < 4; ++row)
                {
                    float sigma = 0.0f;
                    for (int col = 0; col < 4; ++col)
                    {
                        if (col != row)
                            sigma += inputMatrix[row, col] * solvedVector[col];
                    }
                    solvedVector[row] = (expectedOutcome[row] - sigma) / inputMatrix[row, row];
                }
                //Engine.PrintLine("Step #" + step + ": " + solvedVector.ToString());
            }
            return solvedVector;
        }

        private static Vector2 AsVector2(Vector3 v3)
            => new(v3.X, v3.Y);

        private static Vector3 AsVector3(Vector2 v2)
            => new(v2.X, v2.Y, 0.0f);

        /// <summary>
        /// Returns a YPR rotator looking from the origin to the end of this vector.
        /// </summary>
        public static Rotator LookatAngles(Vector3 vector) => new(
            RadToDeg(GetPitchAfterYaw(vector)),
            RadToDeg(GetYaw(vector)),
            0.0f);

        public static Rotator LookatAngles(Vector3 origin, Vector3 point)
            => LookatAngles(point - origin);

        public static float GetYaw(Vector3 vector)
            => MathF.Atan2(-vector.X, -vector.Z);

        public static float GetPitchAfterYaw(Vector3 vector)
            => MathF.Atan2(vector.Y, MathF.Sqrt(vector.X * vector.X + vector.Z * vector.Z));

        public static Vector3 GetSafeNormal(Vector3 value, float Tolerance = 1.0e-8f)
        {
            float sq = value.LengthSquared();
            if (sq == 1.0f)
                return value;
            else if (sq < Tolerance)
                return Vector3.Zero;
            else
                return value * InverseSqrtFast(sq);
        }

        public static bool IsInTriangle(Vector3 value, Triangle triangle)
            => IsInTriangle(value, triangle.A, triangle.B, triangle.C);
        public static bool IsInTriangle(Vector3 value, Vector3 triPt1, Vector3 triPt2, Vector3 triPt3)
        {
            Vector3 v0 = triPt2 - triPt1;
            Vector3 v1 = triPt3 - triPt1;
            Vector3 v2 = value - triPt1;

            float dot00 = v0.Dot(v0);
            float dot01 = v0.Dot(v1);
            float dot02 = v0.Dot(v2);
            float dot11 = v1.Dot(v1);
            float dot12 = v1.Dot(v2);

            float invDenom = 1.0f / (dot00 * dot11 - dot01 * dot01);
            float u = (dot11 * dot02 - dot01 * dot12) * invDenom;
            float v = (dot00 * dot12 - dot01 * dot02) * invDenom;

            return u >= 0.0f && v >= 0.0f && u + v < 1.0f;
        }

        public static bool BarycentricCoordsWithin(
            Vector3 value,
            Vector3 triPt1, Vector3 triPt2, Vector3 triPt3,
            out float u, out float v, out float w)
        {
            Vector3 v0 = triPt2 - triPt1;
            Vector3 v1 = triPt3 - triPt1;
            Vector3 v2 = value - triPt1;

            float d00 = v0.Dot(v0);
            float d01 = v0.Dot(v1);
            float d02 = v0.Dot(v2);
            float d11 = v1.Dot(v1);
            float d12 = v1.Dot(v2);

            float invDenom = 1.0f / (d00 * d11 - d01 * d01);
            v = (d11 * d02 - d01 * d12) * invDenom;
            w = (d00 * d12 - d01 * d02) * invDenom;
            u = 1.0f - v - w;

            return u >= 0.0f && v >= 0.0f && u + v < 1.0f;
        }

        /// <summary>
        /// Returns a vector pointing out of a plane, given the plane's normal and a vector to be reflected which is pointing at the plane.
        /// </summary>
        public static Vector3 ReflectionVector(Vector3 normal, Vector3 vector)
        {
            normal = normal.Normalized();
            return vector - 2.0f * Vector3.Dot(vector, normal) * normal;
        }

        /// <summary>
        /// Returns the portion of this Vector3 that is parallel to the given normal.
        /// </summary>
        public static Vector3 ParallelComponent(Vector3 value, Vector3 normal)
        {
            normal = normal.Normalized();
            return normal * Vector3.Dot(value, normal);
        }

        /// <summary>
        /// Returns the portion of this Vector3 that is perpendicular to the given normal.
        /// </summary>
        public static Vector3 PerpendicularComponent(Vector3 value, Vector3 normal)
            => value - ParallelComponent(value, normal);

        #region Transforms
        public static Vector3 RotateAboutPoint(Vector3 point, Vector3 center, Rotator angles)
            => TransformAboutPoint(point, center, angles.GetMatrix());
        public static Vector3 RotateAboutPoint(Vector3 point, Vector3 center, Quaternion rotation)
            => TransformAboutPoint(point, center, Matrix4x4.CreateFromQuaternion(rotation));
        public static Vector3 ScaleAboutPoint(Vector3 point, Vector3 center, Vector3 scale)
            => TransformAboutPoint(point, center, Matrix4x4.CreateScale(scale));
        public static Vector2 RotateAboutPoint2D(Vector2 point, Vector2 center, float angle)
            => AsVector2(TransformAboutPoint(AsVector3(point), AsVector3(center), Matrix4x4.CreateRotationZ(angle)));
        public static Vector2 ScaleAboutPoint2D(Vector2 point, Vector2 center, Vector2 scale)
        {
            Vector3 result = Vector3.Transform(new Vector3(point, 0.0f),
            Matrix4x4.CreateTranslation(new Vector3(center, 0.0f)) *
            Matrix4x4.CreateTranslation(new Vector3(-center, 0.0f)) *
            Matrix4x4.CreateScale(scale.X, scale.Y, 1.0f));
            return new Vector2(result.X, result.Y);
        }

        public static Vector3 TransformAboutPoint(Vector3 point, Vector3 center, Matrix4x4 transform) =>
            Vector3.Transform(point, MatrixAboutPivot(center, transform));

        /// <summary>
        /// Creates a transformation matrix that operates about the pivot point.
        /// </summary>
        /// <param name="pivot"></param>
        /// <param name="transform"></param>
        /// <returns></returns>
        private static Matrix4x4 MatrixAboutPivot(Vector3 pivot, Matrix4x4 transform)
            => FromOriginMatrix(pivot) * transform * ToOriginMatrix(pivot);

        /// <summary>
        /// Adds back the translation from the origin
        /// </summary>
        /// <param name="center"></param>
        /// <returns></returns>
        private static Matrix4x4 FromOriginMatrix(Vector3 center)
            => Matrix4x4.CreateTranslation(center);

        /// <summary>
        /// Removes the translation from the origin
        /// </summary>
        /// <param name="center"></param>
        /// <returns></returns>
        private static Matrix4x4 ToOriginMatrix(Vector3 center)
            => Matrix4x4.CreateTranslation(-center);

        #endregion

        #region Min/Max
        public static float Max(params float[] values)
        {
            float max = float.MinValue;
            for (int i = 0; i < values.Length; i++)
                max = Math.Max(max, values[i]);
            return max;
        }
        public static double Max(params double[] values)
        {
            double max = double.MinValue;
            for (int i = 0; i < values.Length; i++)
                max = Math.Max(max, values[i]);
            return max;
        }
        public static decimal Max(params decimal[] values)
        {
            decimal max = decimal.MinValue;
            for (int i = 0; i < values.Length; i++)
                max = Math.Max(max, values[i]);
            return max;
        }
        public static int Max(params int[] values)
        {
            int max = int.MinValue;
            for (int i = 0; i < values.Length; i++)
                max = Math.Max(max, values[i]);
            return max;
        }
        public static uint Max(params uint[] values)
        {
            uint max = uint.MinValue;
            for (int i = 0; i < values.Length; i++)
                max = Math.Max(max, values[i]);
            return max;
        }
        public static short Max(params short[] values)
        {
            short max = short.MinValue;
            for (int i = 0; i < values.Length; i++)
                max = Math.Max(max, values[i]);
            return max;
        }
        public static ushort Max(params ushort[] values)
        {
            ushort max = ushort.MinValue;
            for (int i = 0; i < values.Length; i++)
                max = Math.Max(max, values[i]);
            return max;
        }
        public static byte Max(params byte[] values)
        {
            byte max = byte.MinValue;
            for (int i = 0; i < values.Length; i++)
                max = Math.Max(max, values[i]);
            return max;
        }
        public static sbyte Max(params sbyte[] values)
        {
            sbyte max = sbyte.MinValue;
            for (int i = 0; i < values.Length; i++)
                max = Math.Max(max, values[i]);
            return max;
        }
        public static Vector2 ComponentMax(params Vector2[] values)
        {
            Vector2 max = new(float.MinValue);
            for (int i = 0; i < 2; ++i)
                for (int x = 0; x < values.Length; x++)
                    max[i] = Math.Max(max[i], values[x][i]);
            return max;
        }
        public static Vector3 ComponentMax(params Vector3[] values)
        {
            Vector3 max = new(float.MinValue);
            for (int i = 0; i < 3; ++i)
                for (int x = 0; x < values.Length; x++)
                    max[i] = Math.Max(max[i], values[x][i]);
            return max;
        }
        public static Vector4 ComponentMax(params Vector4[] values)
        {
            Vector4 max = new(float.MinValue);
            for (int i = 0; i < 4; ++i)
                for (int x = 0; x < values.Length; x++)
                    max[i] = Math.Max(max[i], values[x][i]);
            return max;
        }
        public static float Min(params float[] values)
        {
            float min = float.MaxValue;
            for (int i = 0; i < values.Length; i++)
                min = Math.Min(min, values[i]);
            return min;
        }
        public static double Min(params double[] values)
        {
            double min = double.MaxValue;
            for (int i = 0; i < values.Length; i++)
                min = Math.Min(min, values[i]);
            return min;
        }
        public static decimal Min(params decimal[] values)
        {
            decimal min = decimal.MaxValue;
            for (int i = 0; i < values.Length; i++)
                min = Math.Min(min, values[i]);
            return min;
        }
        public static int Min(params int[] values)
        {
            int min = int.MaxValue;
            for (int i = 0; i < values.Length; i++)
                min = Math.Min(min, values[i]);
            return min;
        }
        public static uint Min(params uint[] values)
        {
            uint min = uint.MaxValue;
            for (int i = 0; i < values.Length; i++)
                min = Math.Min(min, values[i]);
            return min;
        }
        public static short Min(params short[] values)
        {
            short min = short.MaxValue;
            for (int i = 0; i < values.Length; i++)
                min = Math.Min(min, values[i]);
            return min;
        }
        public static ushort Min(params ushort[] values)
        {
            ushort min = ushort.MaxValue;
            for (int i = 0; i < values.Length; i++)
                min = Math.Min(min, values[i]);
            return min;
        }
        public static byte Min(params byte[] values)
        {
            byte min = byte.MaxValue;
            for (int i = 0; i < values.Length; i++)
                min = Math.Min(min, values[i]);
            return min;
        }
        public static sbyte Min(params sbyte[] values)
        {
            sbyte min = sbyte.MaxValue;
            for (int i = 0; i < values.Length; i++)
                min = Math.Min(min, values[i]);
            return min;
        }
        public static Vector2 ComponentMin(params Vector2[] values)
        {
            Vector2 min = Globals.Max.Vector2;
            for (int i = 0; i < 2; ++i)
                for (int x = 0; x < values.Length; x++)
                    min[i] = Math.Min(min[i], values[x][i]);
            return min;
        }
        public static Vector3 ComponentMin(params Vector3[] values)
        {
            Vector3 min = Globals.Max.Vector3;
            for (int i = 0; i < 3; ++i)
                for (int x = 0; x < values.Length; x++)
                    min[i] = Math.Min(min[i], values[x][i]);
            return min;
        }
        public static Vector4 ComponentMin(params Vector4[] values)
        {
            Vector4 min = Globals.Max.Vector4;
            for (int i = 0; i < 4; ++i)
                for (int x = 0; x < values.Length; x++)
                    min[i] = Math.Min(min[i], values[x][i]);
            return min;
        }
        public static void MinMax(out float min, out float max, params float[] values)
        {
            min = float.MaxValue;
            max = float.MinValue;
            float value;
            for (int i = 0; i < values.Length; i++)
            {
                value = values[i];
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
        }
        public static void ComponentMinMax(out Vector2 min, out Vector2 max, params Vector2[] values)
        {
            min = Globals.Max.Vector2;
            max = Globals.Min.Vector2;
            float value;
            for (int i = 0; i < 2; ++i)
                for (int x = 0; x < values.Length; x++)
                {
                    value = values[x][i];
                    min[i] = Math.Min(min[i], value);
                    max[i] = Math.Max(max[i], value);
                }
        }
        public static void ComponentMinMax(out Vector3 min, out Vector3 max, params Vector3[] values)
        {
            min = Globals.Max.Vector3;
            max = Globals.Min.Vector3;
            float value;
            for (int i = 0; i < 3; ++i)
                for (int x = 0; x < values.Length; x++)
                {
                    value = values[x][i];
                    min[i] = Math.Min(min[i], value);
                    max[i] = Math.Max(max[i], value);
                }
        }
        public static void ComponentMinMax(out Vector4 min, out Vector4 max, params Vector4[] values)
        {
            min = Globals.Max.Vector4;
            max = Globals.Min.Vector4;
            float value;
            for (int i = 0; i < 4; ++i)
                for (int x = 0; x < values.Length; x++)
                {
                    value = values[x][i];
                    min[i] = Math.Min(min[i], value);
                    max[i] = Math.Max(max[i], value);
                }
        }
        #endregion

        public static int[] PascalTriangleRow(int rowIndex)
        {
            int[] values = new int[rowIndex + 1];
            int c = 1;
            for (int row = 0; row <= rowIndex; ++row)
                for (int val = 0; val <= row; val++)
                {
                    if (val == 0 || row == 0)
                        c = 1;
                    else
                    {
                        c = c * (row - val + 1) / val;
                        if (row == rowIndex)
                            values[val] = c;
                    }
                }
            return values;
        }
        public static int[] PascalTriangleRow(int rowIndex, out int sum)
        {
            sum = (int)Pow(2, rowIndex);
            return PascalTriangleRow(rowIndex);
        }

        /// <summary>
        /// Returns the Y-value from a normal distribution given the following parameters.
        /// </summary>
        /// <param name="x">The X-value on the distribution.</param>
        /// <param name="sigma">The standard deviation.</param>
        /// <param name="mu">Mu is the mean or expectation of the distribution (and also its median and mode),</param>
        /// <returns>The Y-value.</returns>
        public static double NormalDistribution(double x, double sigma = 1.0, double mu = 0.0)
        {
            x -= mu;
            x *= x;
            double m = sigma * sigma;
            double power = -x * 0.5 / m;
            return Exp(power) / (sigma * Sqrt(2.0 * PI));
        }
        public static double[] NormalDistributionKernelDouble(int pascalRow)
        {
            int[] rowValues = PascalTriangleRow(pascalRow, out int sum);
            return [.. rowValues.Select(x => (double)x / sum)];
        }
        /// <summary>
        /// Returns the Y-value from a normal distribution given the following parameters.
        /// </summary>
        /// <param name="x">The X-value on the distribution.</param>
        /// <param name="sigma">The standard deviation.</param>
        /// <param name="mu">Mu is the mean or expectation of the distribution (and also its median and mode),</param>
        /// <returns>The Y-value.</returns>
        public static float NormalDistribution(float x, float sigma = 1.0f, float mu = 0.0f)
        {
            x -= mu;
            x *= x;
            float m = sigma * sigma;
            float power = -x * 0.5f / m;
            return (float)Exp(power) / (sigma * (float)Sqrt(2.0f * PIf));
        }
        public static float[] NormalDistributionKernelFloat(int pascalRow)
        {
            int[] rowValues = PascalTriangleRow(pascalRow, out int sum);
            return [.. rowValues.Select(x => (float)x / sum)];
        }

        public static Quaternion RotationBetweenVectors(Vector3 current, Vector3 target)
        {
            AxisAngleBetween(current, target, out Vector3 axis, out float radians);
            return Quaternion.CreateFromAxisAngle(axis, radians);
        }

        public static float GetPlaneDistance(Vector3 planePoint, Vector3 planeNormal)
            => -Vector3.Dot(planePoint, planeNormal);

        /// <summary>
        /// Constructs a normal given three points.
        /// Points must be specified in this order 
        /// to ensure the normal points in the right direction.
        ///   ^
        ///   |   p2
        /// n |  /
        ///   | / u
        ///   |/_______ p1
        ///  p0    v
        /// </summary>
        public static Vector3 CalculateNormal(Vector3 point0, Vector3 point1, Vector3 point2)
        {
            //Get two difference vectors between points
            Vector3 v = point1 - point0;
            Vector3 u = point2 - point0;
            //Cross them to get normal vector
            return Vector3.Cross(v, u).Normalized();
        }

        public static float AngleBetween(Vector3 vec1, Vector3 Vector2, bool returnRadians = false)
        {
            float angle = (float)Acos(Vector3.Dot(vec1, Vector2));
            if (returnRadians)
                return angle;
            return RadToDeg(angle);
        }

        /// <summary>
        /// Returns a new Vector that is the linear blend of the 2 given Vectors
        /// </summary>
        /// <param name="a">First input vector</param>
        /// <param name="b">Second input vector</param>
        /// <param name="blend">The blend factor. a when blend=0, b when blend=1.</param>
        /// <returns>a when blend=0, b when blend=1, and a linear combination otherwise</returns>
        public static Vector3 Lerp(Vector3 a, Vector3 b, float time)

            //initial value with a percentage of the difference between the two vectors added to it.
            => a + (b - a) * time;

        /// <summary>
        /// Interpolate 3 Vectors using Barycentric coordinates
        /// </summary>
        /// <param name="a">First input Vector</param>
        /// <param name="b">Second input Vector</param>
        /// <param name="c">Third input Vector</param>
        /// <param name="u">First Barycentric Coordinate</param>
        /// <param name="v">Second Barycentric Coordinate</param>
        /// <returns>a when u=v=0, b when u=1,v=0, c when u=0,v=1, and a linear combination of a,b,c otherwise</returns>
        public static Vector3 BaryCentric(Vector3 a, Vector3 b, Vector3 c, float u, float v)
            => a + u * (b - a) + v * (c - a);

        /// <summary>
        /// Returns pitch, yaw, and roll angles from a quaternion in that order.
        /// Angles are in radians.
        /// </summary>
        /// <param name="rotation"></param>
        /// <returns></returns>
        public static Vector3 QuaternionToEuler(Quaternion rotation)
        {
            Vector3 euler = new();
            float sqw = rotation.W * rotation.W;
            float sqx = rotation.X * rotation.X;
            float sqy = rotation.Y * rotation.Y;
            float sqz = rotation.Z * rotation.Z;
            float unit = sqx + sqy + sqz + sqw; // if normalised is one, otherwise is correction factor
            float test = rotation.X * rotation.Y + rotation.Z * rotation.W;
            if (test > 0.499f * unit)
            { 
                // singularity at north pole
                euler.Y = 2.0f * MathF.Atan2(rotation.X, rotation.W);
                euler.Z = MathF.PI / 2.0f;
                euler.X = 0;
            }
            if (test < -0.499f * unit)
            {
                // singularity at south pole
                euler.Y = -2.0f * MathF.Atan2(rotation.X, rotation.W);
                euler.Z = -MathF.PI / 2.0f;
                euler.X = 0;
            }
            else
            {
                euler.Y = MathF.Atan2(2 * rotation.Y * rotation.W - 2 * rotation.X * rotation.Z, sqx - sqy - sqz + sqw);
                euler.Z = MathF.Asin(2 * test / unit);
                euler.X = MathF.Atan2(2 * rotation.X * rotation.W - 2 * rotation.Y * rotation.Z, -sqx + sqy - sqz + sqw);
            }
            return euler;
        }

        private static Vector3 NormalizeDegrees(Vector3 euler)
        {
            euler.X = NormalizeDegree(euler.X);
            euler.Y = NormalizeDegree(euler.Y);
            euler.Z = NormalizeDegree(euler.Z);
            return euler;
        }

        private static float NormalizeDegree(float deg)
        {
            deg %= 360.0f;
            if (deg < 0)
                deg += 360.0f;
            return deg;
        }

        public static Vector3 GetPlanePoint(Plane plane)
            => plane.Normal * -plane.D;

        public static Plane SetPlanePoint(Plane plane, Vector3 point)
        {
            plane.D = -Vector3.Dot(point, plane.Normal);
            return plane;
        }

        /// <summary>
        /// Creates a plane object from a point and normal.
        /// </summary>
        /// <param name="point"></param>
        /// <param name="normal"></param>
        /// <returns></returns>
        public static Plane CreatePlaneFromPointAndNormal(Vector3 point, Vector3 normal)
        {
            normal = normal.Normalized();
            return new(normal, -Vector3.Dot(normal, point));
        }

        /// <summary>
        /// Fast inverse square root approximation.
        /// </summary>
        /// <param name="lengthSquared"></param>
        /// <returns></returns>
        public static float InverseSqrtFast(float lengthSquared)
        {
            float x2 = lengthSquared * 0.5f;
            int i = BitConverter.SingleToInt32Bits(lengthSquared);
            i = 0x5f3759df - (i >> 1);
            lengthSquared = BitConverter.Int32BitsToSingle(i);
            lengthSquared *= 1.5f - x2 * lengthSquared * lengthSquared;
            return lengthSquared;
        }

        public static bool Approx(float value1, float value2, float tolerance = 0.0001f)
            => MathF.Abs(value1 - value2) < tolerance;

        public static bool Approx(double value1, double value2, double tolerance = 0.0001)
            => Math.Abs(value1 - value2) < tolerance;

        public static bool Approx(Vector2 value1, Vector2 value2, float tolerance = 0.0001f)
            => Approx(value1.X, value2.X, tolerance) && Approx(value1.Y, value2.Y, tolerance);

        public static bool Approx(Vector3 value1, Vector3 value2, float tolerance = 0.0001f)
            => Approx(value1.X, value2.X, tolerance) && Approx(value1.Y, value2.Y, tolerance) && Approx(value1.Z, value2.Z, tolerance);

        public static bool Approx(Vector4 value1, Vector4 value2, float tolerance = 0.0001f)
            => Approx(value1.X, value2.X, tolerance) && Approx(value1.Y, value2.Y, tolerance) && Approx(value1.Z, value2.Z, tolerance) && Approx(value1.W, value2.W, tolerance);

        public static bool IsApproximatelyIdentity(Quaternion r, float tolerance) =>
            Approx(r.X, 0.0f, tolerance) &&
            Approx(r.Y, 0.0f, tolerance) &&
            Approx(r.Z, 0.0f, tolerance) &&
            Approx(r.W, 1.0f, tolerance);

        public static uint NextPowerOfTwo(uint value)
        {
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return ++value;
        }

        public static unsafe bool MatrixEquals(Matrix4x4 left, Matrix4x4 right)
        {
            for (int x = 0; x < 4; x++)
                for (int y = 0; y < 4; y++)
                    if (!Approx(left[x, y], right[x, y]))
                        return false;
            return true;
        }

        public static bool VolumeEquals(AABB left, AABB right)
            => Approx(left.Min, left.Min) && Approx(right.Max, right.Max);

        public static bool VolumeEquals(AABB? left, AABB? right)
            => left.HasValue && right.HasValue ? VolumeEquals(left.Value, right.Value) : left.HasValue == right.HasValue;

        public static bool VectorsEqual(Vector2 left, Vector2 right) =>
            Approx(left.X, right.X) &&
            Approx(left.Y, right.Y);

        public static bool VectorsEqual(Vector3 left, Vector3 right) =>
            Approx(left.X, right.X) &&
            Approx(left.Y, right.Y) &&
            Approx(left.Z, right.Z);

        public static bool VectorsEqual(Vector4 left, Vector4 right) =>
            Approx(left.X, right.X) &&
            Approx(left.Y, right.Y) &&
            Approx(left.Z, right.Z) &&
            Approx(left.W, right.W);

        //public static Vector3 PositionFromBarycentricUV(VertexTriangle triangle, Vector2 UV)
        //    => (1.0f - UV.X - UV.Y) * triangle.Vertex0.Position + UV.X * triangle.Vertex1.Position + UV.Y * triangle.Vertex2.Position;
        public static Vector3 PositionFromBarycentricUV(Vector3 v0, Vector3 v1, Vector3 v2, Vector2 uv)
            => (1.0f - uv.X - uv.Y) * v0 + uv.X * v1 + uv.Y * v2;
    }
}
