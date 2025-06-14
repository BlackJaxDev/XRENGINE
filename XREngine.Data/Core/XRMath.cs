using Extensions;
using SimpleScene.Util.ssBVH;
using System;
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
        /// Returns the angle in degrees between two vectors.
        /// </summary>
        public static float GetBestAngleDegreesBetween(Vector3 vector1, Vector3 vector2)
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
                return 180.0f;
            else
                return float.RadiansToDegrees(MathF.Acos(dot));
        }
        public static float GetBestAngleRadiansBetween(Vector3 start, Vector3 end, float tolerance = 0.001f)
        {
            start = start.Normalized();
            end = end.Normalized();

            float dot = Vector3.Dot(start, end);

            //dot is the cosine adj/hyp ratio between the two vectors, so
            //dot == 1 is same direction
            //dot == -1 is opposite direction
            //dot == 0 is a 90 degree angle

            tolerance = 1.0f - tolerance;

            if (dot > tolerance)
                return 0.0f;
            else if (dot < -tolerance)
                return PIf;
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
                ? Globals.Backward
                : Vector3.Cross(initialVector, finalVector);
        }
        /// <summary>
        /// Returns a rotation axis and angle in radians between two vectors.
        /// </summary>
        public static void AxisAngleBetween(Vector3 initialVector, Vector3 finalVector, out Vector3 axis, out float rad)
        {
            float dot = Vector3.Dot(initialVector.Normalized(), finalVector.Normalized());

            //dot is the cosine adj/hyp ratio between the two vectors, so
            //dot == 1 is same direction
            //dot == -1 is opposite direction
            //dot == 0 is a 90 degree angle

            if (dot > 1.0f - float.Epsilon)
            {
                axis = Globals.Backward;
                rad = 0.0f;
            }
            else if (dot < -1.0f + float.Epsilon)
            {
                axis = -Globals.Backward;
                rad = DegToRad(180.0f);
            }
            else
            {
                axis = Vector3.Cross(initialVector, finalVector).Normalized();
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
        /// Initial vector is assumed to be pointing along the -Z axis.
        /// Yaw rotates counterclockwise around the Y axis,
        /// and pitch rotates upward around the X axis, after X has been yawed.
        /// </summary>
        public static Rotator LookatAngles(Vector3 vector)
            => new(GetPitchAfterYaw(vector), GetYaw(vector), 0.0f);

        public static Rotator LookatAngles(Vector3 origin, Vector3 point)
            => LookatAngles(point - origin);

        ///// <summary>
        ///// Calculates the yaw angle (rotation around Y-axis) for a vector in radians.
        ///// The yaw calculation uses atan2 to find the angle between the vector's X and Z components.
        ///// A negative sign is applied to account for right-handed coordinate system conventions.
        ///// </summary>
        ///// <param name="vector">The vector to calculate yaw angle from</param>
        ///// <returns>The yaw angle in radians, where 0 points along -Z and positive rotation is counterclockwise around Y axis</returns>
        //public static float GetYaw(Vector3 vector)
        //    => MathF.Atan2(-vector.X, -vector.Z);

        /// <summary>
        /// Calculates the pitch angle (rotation around X-axis) for a vector after yaw has been applied.
        /// The pitch is calculated as the angle between:
        /// - The Y component (height)
        /// - The length of the horizontal components (sqrt(x^2 + z^2))
        /// </summary>
        /// <param name="vector">The vector to calculate pitch from</param>
        /// <returns>The pitch angle in degrees, where positive values rotate the vector upward</returns>
        public static float GetPitchAfterYaw(Vector3 vector)
        {
            return float.RadiansToDegrees(MathF.Atan2(vector.Y, MathF.Sqrt(vector.X * vector.X + vector.Z * vector.Z)));
        }

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

        ///// <summary>
        ///// Returns a new Vector that is the linear blend of the 2 given Vectors
        ///// </summary>
        ///// <param name="a">First input vector</param>
        ///// <param name="b">Second input vector</param>
        ///// <param name="blend">The blend factor. a when blend=0, b when blend=1.</param>
        ///// <returns>a when blend=0, b when blend=1, and a linear combination otherwise</returns>
        //public static Vector3 Lerp(Vector3 a, Vector3 b, float time)

        //    //initial value with a percentage of the difference between the two vectors added to it.
        //    => a + (b - a) * time;

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

        public static float DeltaAngle(float angleFrom, float angleTo)
        {
            float num = Repeat(angleTo - angleFrom, 360.0f);
            if (num > 180.0f)
                num -= 360.0f;
            return num;
        }

        /// <summary>
        /// Loops the value t to the range [0, length).
        /// </summary>
        /// <param name="t"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public static float Repeat(float t, float length)
            => Clamp(t - MathF.Floor(t / length) * length, 0.0f, length);

        /// <summary>
        /// Returns yaw angle (-180 - 180) of 'forward' vector relative to rotation space defined by spaceForward and spaceUp axes.
        /// </summary>
        public static float GetYaw(Quaternion space, Vector3 forward)
        {
            Vector3 dirLocal = Vector3.Transform(forward, Quaternion.Inverse(space));
            if (dirLocal.X == 0f && dirLocal.Z == 0f || float.IsInfinity(dirLocal.X) || float.IsInfinity(dirLocal.Z))
                return 0f;
            return float.RadiansToDegrees(MathF.Atan2(dirLocal.X, dirLocal.Z));
        }

        /// <summary>
        /// Returns pitch angle (-90 - 90) of 'forward' vector relative to rotation space defined by spaceForward and spaceUp axes.
        /// </summary>
        public static float GetPitch(Quaternion space, Vector3 forward)
        {
            forward = forward.Normalized();
            Vector3 dirLocal = Vector3.Transform(forward, Quaternion.Inverse(space));
            if (MathF.Abs(dirLocal.Y) > 1f)
                dirLocal = dirLocal.Normalized();
            return float.RadiansToDegrees(-MathF.Asin(dirLocal.Y));
        }

        /// <summary>
        /// Returns bank angle (-180 - 180) of 'forward' and 'up' vectors relative to rotation space defined by spaceForward and spaceUp axes.
        /// </summary>
        public static float GetBank(Quaternion space, Vector3 forward, Vector3 up)
        {
            Vector3 spaceUp = Vector3.Transform(Globals.Up, space);

            Quaternion invSpace = Quaternion.Inverse(space);
            forward = Vector3.Transform(forward, invSpace);
            up = Vector3.Transform(up, invSpace);

            Quaternion q = Quaternion.Inverse(LookRotation(spaceUp, forward));
            up = Vector3.Transform(up, q);
            float result = float.RadiansToDegrees(MathF.Atan2(up.X, up.Z));
            return result.Clamp(-180.0f, 180.0f);
        }

        /// <summary>
        /// Returns yaw angle (-180 - 180) of 'forward' vector relative to rotation space defined by spaceForward and spaceUp axes.
        /// </summary>
        public static float GetYaw(Quaternion space, Quaternion rotation)
        {
            Vector3 dirLocal = Vector3.Transform(Vector3.Transform(Globals.Backward, rotation), Quaternion.Inverse(space));
            if (dirLocal.X == 0.0f && dirLocal.Z == 0.0f || float.IsInfinity(dirLocal.X) || float.IsInfinity(dirLocal.Z))
                return 0.0f;
            return float.RadiansToDegrees(MathF.Atan2(dirLocal.X, dirLocal.Z));
        }

        /// <summary>
        /// Returns pitch angle (-90 - 90) of 'forward' vector relative to rotation space defined by spaceForward and spaceUp axes.
        /// </summary>
        public static float GetPitch(Quaternion space, Quaternion rotation)
        {
            Vector3 dirLocal = Vector3.Transform(Vector3.Transform(Globals.Backward, rotation), Quaternion.Inverse(space));
            if (MathF.Abs(dirLocal.Y) > 1.0f)
                dirLocal = dirLocal.Normalized();
            return float.RadiansToDegrees(-MathF.Asin(dirLocal.Y));
        }

        /// <summary>
        /// Returns bank angle (-180 - 180) of 'forward' and 'up' vectors relative to rotation space defined by spaceForward and spaceUp axes.
        /// </summary>
        public static float GetBank(Quaternion space, Quaternion rotation)
        {
            Vector3 spaceUp = Vector3.Transform(Globals.Up, space);

            Quaternion invSpace = Quaternion.Inverse(space);
            Vector3 forward = Vector3.Transform(Vector3.Transform(Globals.Backward, rotation), invSpace);
            Vector3 up = Vector3.Transform(Vector3.Transform(Globals.Up, rotation), invSpace);

            Quaternion q = Quaternion.Inverse(LookRotation(spaceUp, forward));
            up = Vector3.Transform(up, q);
            float result = float.RadiansToDegrees(MathF.Atan2(up.X, up.Z));
            return result.Clamp(-180.0f, 180.0f);
        }

        /// <summary>
        /// Optimized Quaternion.Lerp
        /// </summary>
        public static Quaternion Lerp(Quaternion fromRotation, Quaternion toRotation, float weight)
        {
            if (weight <= 0.0f)
                return fromRotation;
            if (weight >= 1.0f)
                return toRotation;
            return Quaternion.Lerp(fromRotation, toRotation, weight);
        }

        /// <summary>
        /// Optimized Quaternion.Slerp
        /// </summary>
        public static Quaternion Slerp(Quaternion fromRotation, Quaternion toRotation, float weight)
        {
            if (weight <= 0.0f)
                return fromRotation;
            if (weight >= 1.0f)
                return toRotation;
            return Quaternion.Slerp(fromRotation, toRotation, weight);
        }

        /// <summary>
        /// Returns the rotation from identity Quaternion to "q", interpolated linearily by "weight".
        /// </summary>
        public static Quaternion LinearBlend(Quaternion q, float weight)
        {
            if (weight <= 0.0f)
                return Quaternion.Identity;
            if (weight >= 1.0f)
                return q;
            return Quaternion.Lerp(Quaternion.Identity, q, weight);
        }

        /// <summary>
        /// Returns the rotation from identity Quaternion to "q", interpolated spherically by "weight".
        /// </summary>
        public static Quaternion SphericalBlend(Quaternion q, float weight)
        {
            if (weight <= 0.0f)
                return Quaternion.Identity;
            if (weight >= 1.0f)
                return q;
            return Quaternion.Slerp(Quaternion.Identity, q, weight);
        }

        /// <summary>
        /// Creates a FromToRotation, but makes sure its axis remains fixed near to the Quaternion singularity point.
        /// </summary>
        /// <returns>
        /// The from to rotation around an axis.
        /// </returns>
        /// <param name='fromDirection'>
        /// From direction.
        /// </param>
        /// <param name='toDirection'>
        /// To direction.
        /// </param>
        /// <param name='axis'>
        /// Axis. Should be normalized before passing into this method.
        /// </param>
        public static Quaternion FromToAroundAxis(Vector3 fromDirection, Vector3 toDirection, Vector3 axis)
        {
            Quaternion fromTo = RotationBetweenVectors(fromDirection, toDirection);
            ToAngleAxis(fromTo, out float angle, out Vector3 freeAxis);

            if (Vector3.Dot(freeAxis, axis) < 0.0f)
                angle = -angle;

            return Quaternion.CreateFromAxisAngle(axis, angle);
        }

        private static void ToAngleAxis(Quaternion rotation, out float angle, out Vector3 axis)
        {
            angle = 2.0f * MathF.Acos(rotation.W);
            float den = MathF.Sqrt(1.0f - rotation.W * rotation.W);
            axis = den > 0.0001f ? new(rotation.X / den, rotation.Y / den, rotation.Z / den) : Globals.Backward;
        }

        /// <summary>
        /// Gets the rotation that can be used to convert a rotation from one axis space to another.
        /// </summary>
        public static Quaternion RotationToLocalSpace(Quaternion space, Quaternion rotation)
            => Quaternion.Inverse(Quaternion.Inverse(space) * rotation);

        /// <summary>
        /// Gets the Quaternion from rotation "from" to rotation "to".
        /// </summary>
        public static Quaternion FromToRotation(Quaternion from, Quaternion to)
            => to == from ? Quaternion.Identity : to * Quaternion.Inverse(from);

        /// <summary>
        /// Gets the closest direction axis to a vector.
        /// Input vector must be normalized.
        /// </summary>
        public static Vector3 GetAxis(Vector3 v)
        {
            Vector3 closest = Globals.Right;
            bool neg = false;

            float x = Vector3.Dot(v, Globals.Right);
            float maxAbsDot = MathF.Abs(x);
            if (x < 0f) neg = true;

            float y = Vector3.Dot(v, Globals.Up);
            float absDot = MathF.Abs(y);
            if (absDot > maxAbsDot)
            {
                maxAbsDot = absDot;
                closest = Globals.Up;
                neg = y < 0f;
            }

            float z = Vector3.Dot(v, Globals.Backward);
            absDot = MathF.Abs(z);
            if (absDot > maxAbsDot)
            {
                closest = Globals.Backward;
                neg = z < 0f;
            }

            if (neg) closest = -closest;
            return closest;
        }

        public static Quaternion ClampRotation(Quaternion rotation, float clampWeight, int clampSmoothing)
        {
            if (clampWeight >= 1.0f)
                return Quaternion.Identity;

            if (clampWeight <= 0.0f)
                return rotation;

            float angle = AngleBetween(Quaternion.Identity, rotation);
            float dot = 1.0f - (angle / 180.0f);
            float targetClampScale = (1.0f - ((clampWeight - dot) / (1.0f - dot))).Clamp(0.0f, 1.0f);
            float clampScale = (dot / clampWeight).Clamp(0.0f, 1.0f);

            // Sine smoothing iterations
            for (int i = 0; i < clampSmoothing; i++)
                clampScale = MathF.Sin(clampScale * MathF.PI * 0.5f);
            
            return Quaternion.Slerp(Quaternion.Identity, rotation, clampScale * targetClampScale);
        }

        private static float AngleBetween(Quaternion from, Quaternion to)
        {
            // Calculate the dot product and clamp it between -1 and 1
            float dot = Quaternion.Dot(from, to);
            dot = MathF.Min(MathF.Max(dot, -1.0f), 1.0f);

            // The angle between two quaternions is defined as 2 * acos(|dot|)
            return float.RadiansToDegrees(2.0f * MathF.Acos(MathF.Abs(dot)));
        }

        /// <summary>
        /// Clamps an angular value.
        /// </summary>
        public static float ClampAngle(float angle, float clampWeight, int clampSmoothing)
        {
            if (clampWeight >= 1f)
                return 0f;
            if (clampWeight <= 0f)
                return angle;

            float dot = 1f - (MathF.Abs(angle) / 180f);
            float targetClampMlp = (1f - ((clampWeight - dot) / (1f - dot))).Clamp(0f, 1f);
            float clampMlp = (dot / clampWeight).Clamp(0f, 1f);

            // Sine smoothing iterations
            for (int i = 0; i < clampSmoothing; i++)
            {
                float sinF = clampMlp * MathF.PI * 0.5f;
                clampMlp = MathF.Sin(sinF);
            }

            return Interp.Lerp(0f, angle, clampMlp * targetClampMlp);
        }

        /// <summary>
        /// Used for matching the rotations of objects that have different orientations.
        /// </summary>
        public static Quaternion MatchRotation(
            Quaternion targetRotation,
            Vector3 targetForward,
            Vector3 targetUp,
            Vector3 forward,
            Vector3 up)
        {
            Quaternion fCurrent = LookRotation(forward, up);
            Quaternion fTarget = LookRotation(targetForward, targetUp);
            Quaternion d = targetRotation * fTarget;
            return d * Quaternion.Inverse(fCurrent);
        }

        /// <summary>
        /// Converts an Euler rotation from 0 to 360 representation to -180 to 180.
        /// </summary>
        public static Vector3 ToBiPolar(Vector3 euler)
            => new(ToBiPolar(euler.X), ToBiPolar(euler.Y), ToBiPolar(euler.Z));

        /// <summary>
        /// Converts an angular value from 0 to 360 representation to -180 to 180.
        /// </summary>
        public static float ToBiPolar(float angle)
        {
            angle %= 360f;
            return angle switch
            {
                >= 180f => angle - 360f,
                <= -180f => angle + 360f,
                _ => angle
            };
        }

        /// <summary>
        /// Mirrors a Quaternion on the YZ plane in provided rotation space.
        /// </summary>
        public static Quaternion MirrorYZ(Quaternion r, Quaternion space)
        {
            r = Quaternion.Inverse(space) * r;
            Vector3 forward = Vector3.Transform(Globals.Backward, r);
            Vector3 up = Vector3.Transform(Globals.Up, r);

            forward.X *= -1;
            up.X *= -1;

            return space * LookRotation(forward, up);
        }

        /// <summary>
        /// Mirrors a Quaternion on the world space YZ plane.
        /// </summary>
        public static Quaternion MirrorYZ(Quaternion r)
        {
            Vector3 forward = Vector3.Transform(Globals.Backward, r);
            Vector3 up = Vector3.Transform(Globals.Up, r);

            forward.X *= -1;
            up.X *= -1;

            return LookRotation(forward, up);
        }

        public static Quaternion LookRotation(Vector3 forward)
            => LookRotation(forward, Globals.Up);
        public static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            if (forward.LengthSquared() < float.Epsilon || float.IsNaN(forward.X) || float.IsNaN(forward.Y) || float.IsNaN(forward.Z))
                return Quaternion.Identity;
            if (up.LengthSquared() < float.Epsilon || float.IsNaN(up.X) || float.IsNaN(up.Y) || float.IsNaN(up.Z))
                up = Globals.Up; // Default to world up if up is invalid

            forward = forward.Normalized();
            up = up.Normalized();

            // If up is invalid or nearly parallel to forward, pick a safe up vector
            if (Abs(Vector3.Dot(up, forward)) > 0.999f)
            {
                // Prefer world-up unless it's almost colinear, then choose world-right
                up = Abs(Vector3.Dot(Globals.Up, forward)) < 0.99f
                    ? Globals.Up
                    : Globals.Right;
            }

            Vector3 right = Vector3.Cross(forward, up).Normalized();
            Vector3 orthoUp = Vector3.Cross(right, forward).Normalized();
            return Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, forward, orthoUp));
        }

        /// <summary>
        /// Returns yaw angle (-180 - 180) of 'forward' vector.
        /// </summary>
        public static float GetYaw(Vector3 forward)
        {
            if (forward.X == 0f && forward.Z == 0f)
                return 0f;

            if (float.IsInfinity(forward.X) || float.IsInfinity(forward.Z))
                return 0;

            return float.RadiansToDegrees(MathF.Atan2(forward.X, forward.Z));
        }

        /// <summary>
        /// Returns pitch angle (-90 - 90) of 'forward' vector.
        /// </summary>
        public static float GetPitch(Vector3 forward)
        {
            forward = forward.Normalized(); // Asin range -1 - 1
            return float.RadiansToDegrees(-MathF.Asin(forward.Y));
        }

        /// <summary>
        /// Returns bank angle (-180 - 180) of 'forward' and 'up' vectors.
        /// </summary>
        public static float GetBank(Vector3 forward, Vector3 up)
        {
            Quaternion q = Quaternion.Inverse(LookRotation(Globals.Up, forward));
            up = Vector3.Transform(up, q);
            float result = float.RadiansToDegrees(MathF.Atan2(up.X, up.Z));
            return result.Clamp(-180f, 180f);
        }

        /// <summary>
        /// Returns yaw angle (-180 - 180) of 'forward' vector relative to rotation space defined by spaceForward and spaceUp axes.
        /// </summary>
        public static float GetYaw(Vector3 spaceForward, Vector3 spaceUp, Vector3 forward)
        {
            Quaternion space = Quaternion.Inverse(LookRotation(spaceForward, spaceUp));
            Vector3 dirLocal = Vector3.Transform(forward, space);

            if (dirLocal.X == 0f && dirLocal.Z == 0f)
                return 0f;

            if (float.IsInfinity(dirLocal.X) || float.IsInfinity(dirLocal.Z))
                return 0;

            return float.RadiansToDegrees(MathF.Atan2(dirLocal.X, dirLocal.Z));
        }

        /// <summary>
        /// Returns pitch angle (-90 - 90) of 'forward' vector relative to rotation space defined by spaceForward and spaceUp axes.
        /// </summary>
        public static float GetPitch(Vector3 spaceForward, Vector3 spaceUp, Vector3 forward)
        {
            Quaternion space = Quaternion.Inverse(LookRotation(spaceForward, spaceUp));
            forward = forward.Normalized();
            Vector3 dirLocal = Vector3.Transform(forward, space);
            return float.RadiansToDegrees(-MathF.Asin(dirLocal.Y));
        }

        /// <summary>
        /// Returns bank angle (-180 - 180) of 'forward' and 'up' vectors relative to rotation space defined by spaceForward and spaceUp axes.
        /// </summary>
        public static float GetBank(Vector3 spaceForward, Vector3 spaceUp, Vector3 forward, Vector3 up)
        {
            Quaternion space = Quaternion.Inverse(LookRotation(spaceForward, spaceUp));
            forward = Vector3.Transform(forward, space);
            up = Vector3.Transform(up, space);

            Quaternion q = Quaternion.Inverse(LookRotation(spaceUp, forward));
            up = Vector3.Transform(up, q);
            float result = float.RadiansToDegrees(MathF.Atan2(up.X, up.Z));
            return result.Clamp(-180f, 180f);
        }

        public static Vector3 Lerp(Vector3 fromVector, Vector3 toVector, float weight)
        {
            if (weight <= 0f)
                return fromVector;
            if (weight >= 1f)
                return toVector;
            return Vector3.Lerp(fromVector, toVector, weight);
        }

        public static Vector3 Slerp(Vector3 fromVector, Vector3 toVector, float weight)
        {
            if (weight <= 0.0f)
                return fromVector;
            if (weight >= 1.0f)
                return toVector;

            float dot = Vector3.Dot(fromVector, toVector).Clamp(-1.0f, 1.0f);
            float theta = MathF.Acos(dot) * weight;
            Vector3 relative = toVector - fromVector * dot;
            return fromVector * MathF.Cos(theta) + relative.Normalized() * MathF.Sin(theta);
        }

        /// <summary>
        /// Returns vector projection on axis multiplied by weight.
        /// </summary>
        public static Vector3 ExtractVertical(Vector3 v, Vector3 verticalAxis, float weight)
        {
            if (weight <= 0.0f)
                return Vector3.Zero;
            if (verticalAxis == Globals.Up)
                return Globals.Up * v.Y * weight;
            return ProjectVector(v, verticalAxis) * weight;
        }

        /// <summary>
        /// Projects a vector along a normal.
        /// </summary>
        /// <param name="vector"></param>
        /// <param name="normal"></param>
        /// <returns></returns>
        public static Vector3 ProjectVector(Vector3 vector, Vector3 normal)
        {
            //float sqrMag = normal.LengthSquared();
            //if (sqrMag < Epsilon)
            //    return Vector3.Zero;

            //float dot = Vector3.Dot(vector, normal);
            //return normal * (dot / sqrMag);

            float num = Vector3.Dot(normal, normal);
            if (num < float.Epsilon)
                return Vector3.Zero;
            
            float num2 = Vector3.Dot(vector, normal);
            return new Vector3(
                normal.X * num2 / num,
                normal.Y * num2 / num,
                normal.Z * num2 / num);
        }

        /// <summary>
        /// Returns vector projected to a plane and multiplied by weight.
        /// </summary>
        public static Vector3 ExtractHorizontal(Vector3 v, Vector3 normal, float weight)
        {
            if (weight <= 0f)
                return Vector3.Zero;

            if (normal == Globals.Up)
                return new Vector3(v.X, 0f, v.Z) * weight;

            Vector3 tangent = v;
            OrthoNormalize(ref normal, ref tangent);
            return ProjectVector(v, tangent) * weight;
        }

        /// <summary>
        /// Flattens a vector on a plane defined by 'normal'.
        /// </summary>
        public static Vector3 Flatten(Vector3 v, Vector3 normal)
            => normal == Globals.Up ? new Vector3(v.X, 0f, v.Z) : v - ProjectVector(v, normal);

        /// <summary>
        /// Clamps the direction to clampWeight from normalDirection, clampSmoothing is the number of sine smoothing iterations applied on the result.
        /// </summary>
        public static Vector3 ClampDirection(Vector3 direction, Vector3 normalDirection, float clampWeight, int clampSmoothing)
        {
            if (clampWeight <= 0)
                return direction;

            if (clampWeight >= 1f)
                return normalDirection;

            // Getting the angle between direction and normalDirection
            float angle = GetBestAngleDegreesBetween(normalDirection, direction);
            float dot = 1f - (angle / 180f);

            if (dot > clampWeight)
                return direction;

            // Clamping the target
            float targetClampMlp = clampWeight > 0 ? (1f - ((clampWeight - dot) / (1f - dot))).Clamp(0f, 1f) : 1f;

            // Calculating the clamp multiplier
            float clampMlp = clampWeight > 0 ? (dot / clampWeight).Clamp(0f, 1f) : 1f;

            // Sine smoothing iterations
            for (int i = 0; i < clampSmoothing; i++)
            {
                float sinF = clampMlp * MathF.PI * 0.5f;
                clampMlp = MathF.Sin(sinF);
            }

            // Slerping the direction (don't use Lerp here, it breaks it)
            return Slerp(normalDirection, direction, clampMlp * targetClampMlp);
        }

        /// <summary>
        /// Clamps the direction to clampWeight from normalDirection, clampSmoothing is the number of sine smoothing iterations applied on the result.
        /// </summary>
        public static Vector3 ClampDirection(Vector3 direction, Vector3 normalDirection, float clampWeight, int clampSmoothing, out bool changed)
        {
            changed = false;

            if (clampWeight <= 0) return direction;

            if (clampWeight >= 1f)
            {
                changed = true;
                return normalDirection;
            }

            // Getting the angle between direction and normalDirection
            float angle = GetBestAngleDegreesBetween(normalDirection, direction);
            float dot = 1f - (angle / 180f);

            if (dot > clampWeight)
                return direction;
            changed = true;

            // Clamping the target
            float targetClampMlp = clampWeight > 0 ? (1f - ((clampWeight - dot) / (1f - dot))).Clamp(0f, 1f) : 1f;

            // Calculating the clamp multiplier
            float clampMlp = clampWeight > 0 ? (dot / clampWeight).Clamp(0f, 1f) : 1f;

            // Sine smoothing iterations
            for (int i = 0; i < clampSmoothing; i++)
            {
                float sinF = clampMlp * MathF.PI * 0.5f;
                clampMlp = MathF.Sin(sinF);
            }

            // Slerping the direction (don't use Lerp here, it breaks it)
            return Slerp(normalDirection, direction, clampMlp * targetClampMlp);
        }

        /// <summary>
        /// Clamps the direction to clampWeight from normalDirection, clampSmoothing is the number of sine smoothing iterations applied on the result.
        /// </summary>
        public static Vector3 ClampDirection(Vector3 direction, Vector3 normalDirection, float clampWeight, int clampSmoothing, out float clampValue)
        {
            clampValue = 1f;

            if (clampWeight <= 0)
                return direction;

            if (clampWeight >= 1f)
                return normalDirection;

            // Getting the angle between direction and normalDirection
            float angle = GetBestAngleDegreesBetween(normalDirection, direction);
            float dot = 1f - (angle / 180f);

            if (dot > clampWeight)
            {
                clampValue = 0f;
                return direction;
            }

            // Clamping the target
            float targetClampMlp = clampWeight > 0
                ? (1f - ((clampWeight - dot) / (1f - dot))).Clamp(0f, 1f)
                : 1f;

            // Calculating the clamp multiplier
            float clampMlp = clampWeight > 0 ? (dot / clampWeight).Clamp(0f, 1f) : 1f;

            // Sine smoothing iterations
            for (int i = 0; i < clampSmoothing; i++)
                clampMlp = MathF.Sin(clampMlp * MathF.PI * 0.5f);

            // Slerping the direction (don't use Lerp here, it breaks it)
            float slerp = clampMlp * targetClampMlp;
            clampValue = 1f - slerp;
            return Slerp(normalDirection, direction, slerp);
        }

        /// <summary>
        /// Get the intersection point of line and plane
        /// </summary>
        public static Vector3 LineToPlane(Vector3 origin, Vector3 direction, Vector3 planeNormal, Vector3 planePoint)
        {
            float dot = Vector3.Dot(planePoint - origin, planeNormal);
            float normalDot = Vector3.Dot(direction, planeNormal);

            if (normalDot == 0.0f)
                return Vector3.Zero;

            float dist = dot / normalDot;
            return origin + direction.Normalized() * dist;
        }

        /// <summary>
        /// Projects a point to a plane.
        /// </summary>
        public static Vector3 ProjectPointToPlane(Vector3 point, Vector3 planePosition, Vector3 planeNormal)
        {
            if (planeNormal == Globals.Up)
                return new Vector3(point.X, planePosition.Y, point.Z);

            Vector3 tangent = point - planePosition;
            Vector3 normal = planeNormal;
            OrthoNormalize(ref normal, ref tangent);

            return planePosition + ProjectVector(point - planePosition, tangent);
        }

        /// <summary>
        /// Makes vectors normalized and orthogonal to each other.
        /// Normalizes normal. Normalizes tangent and makes sure it is orthogonal to normal 
        /// (that is, angle between them is 90 degrees).
        /// </summary>
        /// <param name="normal">Reference to the normal vector to be normalized</param>
        /// <param name="tangent">Reference to the tangent vector to be made orthogonal to normal and normalized</param>
        public static void OrthoNormalize(ref Vector3 normal, ref Vector3 tangent)
        {
            normal = normal.Normalized();
            tangent -= Vector3.Dot(tangent, normal) * normal;
            float magnitude = tangent.Length();
            if (magnitude > Epsilon)
                tangent /= magnitude;
            else
                tangent = GetSafeNormal(Vector3.Cross(normal, Globals.Up));
        }

        /// <summary>
        /// Makes vectors normalized and orthogonal to each other.
        /// Normalizes normal. Normalizes tangent and makes sure it is orthogonal to normal. 
        /// Normalizes binormal and makes sure it is orthogonal to both normal and tangent.
        /// </summary>
        /// <param name="normal">Reference to the normal vector to be normalized</param>
        /// <param name="tangent">Reference to the tangent vector to be made orthogonal to normal and normalized</param>
        /// <param name="binormal">Reference to the binormal vector to be made orthogonal to both normal and tangent and normalized</param>
        public static void OrthoNormalize(ref Vector3 normal, ref Vector3 tangent, ref Vector3 binormal)
        {
            normal = normal.Normalized();
            tangent -= Vector3.Dot(tangent, normal) * normal;
            float magnitude = tangent.Length();
            if (magnitude > Epsilon)
                tangent /= magnitude;
            else
                tangent = GetSafeNormal(Vector3.Cross(normal, Globals.Up));

            binormal = binormal - Vector3.Dot(binormal, normal) * normal - Vector3.Dot(binormal, tangent) * tangent;
            magnitude = binormal.Length();
            if (magnitude > Epsilon)
                binormal /= magnitude;
            else
                binormal = Vector3.Cross(normal, tangent);
        }

        public static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxDegreesDelta)
        {
            float angle = AngleBetween(from, to);
            if (angle == 0f)
                return to;
            float t = MathF.Min(1f, maxDegreesDelta / angle);
            return Slerp(from, to, t);
        }

        public static Vector3 SmoothDamp(Vector3 current, Vector3 target, ref Vector3 currentVelocity, float smoothTime, float maxSpeed, float deltaTime)
        {
            // Based on Game Programming Gems 4 Chapter 1.10
            smoothTime = Math.Max(0.0001f, smoothTime);
            float omega = 2f / smoothTime;

            float x = omega * deltaTime;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

            Vector3 change = current - target;
            Vector3 originalTo = target;

            // Clamp maximum speed
            float maxChange = maxSpeed * smoothTime;
            float maxChangeSq = maxChange * maxChange;
            float sqrMagnitude = change.LengthSquared();

            // If we're moving too fast, limit the movement
            if (sqrMagnitude > maxChangeSq)
            {
                float magnitude = MathF.Sqrt(sqrMagnitude);
                change *= maxChange / magnitude;
            }

            target = current - change;

            Vector3 temp = (currentVelocity + omega * change) * deltaTime;

            currentVelocity = (currentVelocity - omega * temp) * exp;

            Vector3 output = target + (change + temp) * exp;

            // Prevent overshooting
            Vector3 origMinusCurr = originalTo - current;
            Vector3 outMinusOrig = output - originalTo;

            if (Vector3.Dot(origMinusCurr, outMinusOrig) > 0)
            {
                output = originalTo;
                currentVelocity = (output - originalTo) / deltaTime;
            }

            return output;
        }

        public static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime, float maxSpeed, float deltaTime)
        {
            // Based on Game Programming Gems 4 Chapter 1.10
            smoothTime = Math.Max(0.0001f, smoothTime);
            float omega = 2f / smoothTime;
            float x = omega * deltaTime;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float originalTo = target;
            // Clamp maximum speed
            float maxChange = maxSpeed * smoothTime;
            float maxChangeSq = maxChange * maxChange;
            float sqrMagnitude = change * change;
            // If we're moving too fast, limit the movement
            if (sqrMagnitude > maxChangeSq)
            {
                float magnitude = MathF.Sqrt(sqrMagnitude);
                change *= maxChange / magnitude;
            }
            target = current - change;
            float temp = (currentVelocity + omega * change) * deltaTime;
            currentVelocity = (currentVelocity - omega * temp) * exp;
            float output = target + (change + temp) * exp;
            // Prevent overshooting
            float origMinusCurr = originalTo - current;
            float outMinusOrig = output - originalTo;
            if (origMinusCurr * outMinusOrig > 0)
            {
                output = originalTo;
                currentVelocity = (output - originalTo) / deltaTime;
            }
            return output;
        }
        /// <summary>
		/// Converts an Axis to Vector3.
		/// </summary>
		public static Vector3 AxisToVector(Axis axis)
        {
            return axis switch
            {
                Axis.X => Globals.Right,
                Axis.Y => Globals.Up,
                _ => Globals.Backward,
            };
        }

        /// <summary>
        /// Converts a Vector3 to Axis.
        /// </summary>
        public static Axis VectorToAxis(Vector3 v)
        {
            float absX = MathF.Abs(v.X);
            float absY = MathF.Abs(v.Y);
            float absZ = MathF.Abs(v.Z);

            Axis d = Axis.X;
            if (absY > absX && absY > absZ)
                d = Axis.Y;
            if (absZ > absX && absZ > absY)
                d = Axis.Z;
            return d;
        }

        /// <summary>
        /// Returns the Axis of the Transform towards a world space direction.
        /// </summary>
        public static Axis GetAxisToDirection(Quaternion r, Vector3 direction)
        {
            Vector3 axis = GetAxisVectorToDirection(r, direction);
            if (axis == Globals.Right)
                return Axis.X;
            if (axis == Globals.Up)
                return Axis.Y;
            return Axis.Z;
        }

        /// <summary>
        /// Returns the local axis of a rotation space that aligns the most with a direction.
        /// </summary>
        public static Vector3 GetAxisVectorToDirection(Quaternion r, Vector3 direction)
        {
            direction = direction.Normalized();
            Vector3 axis = Globals.Right;
            float dotX = MathF.Abs(Vector3.Dot(r.Rotate(Globals.Right), direction));
            float dotY = MathF.Abs(Vector3.Dot(r.Rotate(Globals.Up), direction));
            if (dotY > dotX)
                axis = Globals.Up;
            float dotZ = MathF.Abs(Vector3.Dot(r.Rotate(Globals.Forward), direction));
            if (dotZ < dotX && dotZ < dotY)
                axis = Globals.Forward;
            return axis;
        }

        /// <summary>
        /// Reflects a vector across a plane defined by a normal vector.
        /// 
        /// The reflection formula is: R = V - 2(V·N)N
        /// Where:
        /// - R is the reflected vector
        /// - V is the incident vector to reflect
        /// - N is the normal vector of the reflection plane (must be normalized)
        /// - V·N is the dot product representing how much V points in the same direction as N
        /// 
        /// The formula works by:
        /// 1. Computing how much the vector points in the normal direction (V·N)
        /// 2. Scaling the normal by this amount and doubling it (2(V·N)N)
        /// 3. Subtracting this from the original vector to get the reflection
        /// </summary>
        /// <param name="vector">The vector to reflect</param>
        /// <param name="normal">The normalized normal vector of the reflection plane</param>
        /// <returns>The reflected vector</returns>
        public static Vector3 Reflect(Vector3 vector, Vector3 normal)
            => vector - 2.0f * Vector3.Dot(vector, normal) * normal;

        /// <summary>
        /// Calculates the angle between two vectors in radians, taking into account the rotation normal to calculate a -180 to 180 angle.
        /// </summary>
        /// <param name="end"></param>
        /// <param name="start"></param>
        /// <param name="rotationNormal"></param>
        /// <returns></returns>
        public static float GetFullAngleRadiansBetween(Vector3 start, Vector3 end, Vector3 rotationNormal)
        {
            // Cross product determines rotation direction
            Vector3 cross = Vector3.Cross(start, end);
            float direction = Vector3.Dot(cross, rotationNormal) < 0 ? -1 : 1;

            // Calculate angle with direction
            float dot = Vector3.Dot(start, end);
            dot = dot.Clamp(-1.0f, 1.0f); // Ensure dot is within valid range
            return (float)Acos(dot) * direction;
        }

        /// <summary>
        /// Calculates the angle between two vectors in degrees, taking into account the rotation normal to calculate a -180 to 180 angle.
        /// </summary>
        /// <param name="end"></param>
        /// <param name="start"></param>
        /// <param name="rotationNormal"></param>
        /// <returns></returns>
        public static float GetFullAngleDegreesBetween(Vector3 start, Vector3 end, Vector3 rotationNormal)
            => float.RadiansToDegrees(GetFullAngleRadiansBetween(start, end, rotationNormal));
    }
}
