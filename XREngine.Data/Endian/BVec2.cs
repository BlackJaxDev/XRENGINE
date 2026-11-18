using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Data;

[Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct BVector2(float x, float y)
{
    public const int Size = 8;

    public bfloat _x = x;
    public bfloat _y = y;

    public override readonly string ToString()
        => $"({(float)_x}, {(float)_y})";

    public static implicit operator Vector2(BVector2 v)
        => new(v._x, v._y);
    public static implicit operator BVector2(Vector2 v)
        => new(v.X, v.Y);
}