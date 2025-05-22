using System.Numerics;
using Valve.VR;

public static class OpenVRExtensions
{
    public static Matrix4x4 ToNumerics(this HmdMatrix33_t matrix)
        => new(
            matrix.m0, matrix.m1, matrix.m2, 0,
            matrix.m3, matrix.m4, matrix.m5, 0,
            matrix.m6, matrix.m7, matrix.m8, 0,
            0, 0, 0, 1
        );

    public static Matrix4x4 ToNumerics(this HmdMatrix34_t matrix)
        => new(
            matrix.m0, matrix.m1, matrix.m2, matrix.m3,
            matrix.m4, matrix.m4, matrix.m6, matrix.m7,
            matrix.m8, matrix.m9, matrix.m10, matrix.m11,
            0, 0, 0, 1
        );

    public static Matrix4x4 ToNumerics(this HmdMatrix44_t matrix)
        => new(
            matrix.m0, matrix.m1, matrix.m2, matrix.m3,
            matrix.m4, matrix.m5, matrix.m6, matrix.m7,
            matrix.m8, matrix.m9, matrix.m10, matrix.m11,
            matrix.m12, matrix.m13, matrix.m14, matrix.m15
        );

    public static Vector3 ToNumerics(this HmdVector3_t vector)
        => new(vector.v0, vector.v1, vector.v2);

    public static Vector3 ToNumerics(this HmdVector3d_t vector)
        => new((float)vector.v0, (float)vector.v1, (float)vector.v2);

    public static Vector2 ToNumerics(this HmdVector2_t vector)
        => new(vector.v0, vector.v1);

    public static Vector4 ToNumerics(this HmdVector4_t vector)
        => new(vector.v0, vector.v1, vector.v2, vector.v3);

    public static Quaternion ToNumerics(this HmdQuaternion_t quaternion)
        => new((float)quaternion.x, (float)quaternion.y, (float)quaternion.z, (float)quaternion.w);
}