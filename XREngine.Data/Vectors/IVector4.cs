using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Objects;

namespace XREngine.Data.Vectors;

[Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct IVector4(int x, int y, int z, int w)// : IBufferable
{
    public int X
    {
        readonly get => _x;
        set => _x = value;
    }
    public int Y 
    {
        readonly get => _y;
        set => _y = value;
    }
    public int Z 
    {
        readonly get => _z;
        set => _z = value;
    }
    public int W 
    {
        readonly get => _w;
        set => _w = value;
    }

    private int
        _x = x,
        _y = y,
        _z = z,
        _w = w;

    [Browsable(false)]
    public int* Data { get { fixed (void* ptr = &this) return (int*)ptr; } }

    //public EComponentType ComponentType { get; } = EComponentType.Int;
    //public uint ComponentCount { get; } = 4;
    //public bool Normalize { get; } = false;

    public void Write(VoidPtr address)
    {
        int* dPtr = (int*)address;
        for (int i = 0; i < 4; ++i)
            *dPtr++ = Data[i];
    }
    public void Read(VoidPtr address)
    {
        int* data = (int*)address;
        for (int i = 0; i < 4; ++i)
            Data[i] = *data++;
    }

    public int this[int index]
    {
        get
        {
            if (index < 0 || index > 3)
                throw new IndexOutOfRangeException("Cannot access vector at index " + index);
            return Data[index];
        }
        set
        {
            if (index < 0 || index > 3)
                throw new IndexOutOfRangeException($"Cannot access vector at index {index}");
            Data[index] = value;
        }
    }

    public override readonly string ToString()
        => $"({X}, {Y}, {Z}, {W})";

    public static explicit operator IVector4(Vector4 v) => new((int)v.X, (int)v.Y, (int)v.Z, (int)v.W);
    public static implicit operator Vector4(IVector4 v) => new(v.X, v.Y, v.Z, v.W);
}
