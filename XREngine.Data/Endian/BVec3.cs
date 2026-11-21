using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Data;

[Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BVector3(float x, float y, float z)
{
    public const int Size = 12;

    public bfloat _x = x;
    public bfloat _y = y;
    public bfloat _z = z;

    public override readonly string ToString()
        => $"({(float)_x}, {(float)_y}, {(float)_z})";

    public static implicit operator Vector3(BVector3 v)
        => new(v._x, v._y, v._z);
    public static implicit operator BVector3(Vector3 v)
        => new(v.X, v.Y, v.Z);

    public static implicit operator Vector4(BVector3 v)
        => new(v._x, v._y, v._z, 1);
    public static implicit operator BVector3(Vector4 v)
        => new(v.X / v.W, v.Y / v.W, v.Z / v.W);
}