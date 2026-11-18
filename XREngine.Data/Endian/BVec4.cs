using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Data;

[Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BVector4(float x, float y, float z, float w)
{
    public bfloat _x = x;
    public bfloat _y = y;
    public bfloat _z = z;
    public bfloat _w = w;

    public override readonly string ToString()
        => $"({(float)_x}, {(float)_y}, {(float)_z}, {(float)_w})";

    public static implicit operator Vector4(BVector4 v)
        => new(v._x, v._y, v._z, v._w);
    public static implicit operator BVector4(Vector4 v)
        => new(v.X, v.Y, v.Z, v.W);
}
