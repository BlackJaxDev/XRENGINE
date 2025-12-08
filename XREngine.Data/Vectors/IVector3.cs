using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Objects;

namespace XREngine.Data.Vectors;

[Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct IVector3(int x, int y, int z) : IBufferable, IUniformable
{
    public static readonly IVector3 Zero = new(0, 0, 0);
    public static readonly IVector3 One = new(1, 1, 1);

    public int X { readonly get => x; set => x = value; }
    public int Y { readonly get => y; set => y = value; }
    public int Z { readonly get => z; set => z = value; }

    [Browsable(false)]
    public int* Data { get { fixed (void* ptr = &this) return (int*)ptr; } }

    [Browsable(false)]
    public readonly EComponentType ComponentType => EComponentType.Int;
    [Browsable(false)]
    public readonly uint ComponentCount => 3;
    [Browsable(false)]
    public readonly bool Normalize => false;

    public void Write(VoidPtr address)
    {
        int* dPtr = (int*)address;
        for (int i = 0; i < ComponentCount; ++i)
            *dPtr++ = Data[i];
    }
    public void Read(VoidPtr address)
    {
        int* data = (int*)address;
        for (int i = 0; i < ComponentCount; ++i)
            Data[i] = *data++;
    }

    public static IVector3 Min(IVector3 l, IVector3 r)
        => new(
            Math.Min(l.X, r.X),
            Math.Min(l.Y, r.Y),
            Math.Min(l.Z, r.Z));

    public int this[int index]
    {
        get => index is not < 0 and not > 2 ? Data[index] : throw new IndexOutOfRangeException($"Cannot access vector at index {index}");
        set
        {
            switch (index)
            {
                case < 0:
                case > 2:
                    throw new IndexOutOfRangeException($"Cannot access vector at index {index}");
            }
            Data[index] = value;
        }
    }

    public static explicit operator IVector3(Vector3 v) => new((int)v.X, (int)v.Y, (int)v.Z);
    public static implicit operator Vector3(IVector3 v) => new(v.X, v.Y, v.Z);
}