using System.Numerics;
using Valve.VR;

namespace XREngine.Core;

public static class OpenVRExtensions
{
    public static Matrix4x4 ToNumerics(this HmdMatrix33_t matrix)
        => new(
            matrix.m0, matrix.m1, matrix.m2, 0.0f,
            matrix.m3, matrix.m4, matrix.m5, 0.0f,
            matrix.m6, matrix.m7, matrix.m8, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);

    public static Matrix4x4 ToNumerics(this HmdMatrix34_t matrix)
        => new(
            matrix.m0, matrix.m1, matrix.m2, matrix.m3,
            matrix.m4, matrix.m5, matrix.m6, matrix.m7,
            matrix.m8, matrix.m9, matrix.m10, matrix.m11,
            0.0f, 0.0f, 0.0f, 1.0f);

    public static Matrix4x4 ToNumerics(this HmdMatrix44_t matrix)
        => new(
            matrix.m0, matrix.m1, matrix.m2, matrix.m3,
            matrix.m4, matrix.m5, matrix.m6, matrix.m7,
            matrix.m8, matrix.m9, matrix.m10, matrix.m11,
            matrix.m12, matrix.m13, matrix.m14, matrix.m15);
}