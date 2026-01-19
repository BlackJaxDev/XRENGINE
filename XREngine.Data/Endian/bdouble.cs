using Extensions;

#pragma warning disable CS8981 // Type name only contains lower-cased ascii characters
using System.Runtime.InteropServices;

namespace XREngine.Data;

[Serializable]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct bdouble
{
    public double _data;

    public static implicit operator double(bdouble val)
        => Endian.SerializeBig ? val._data.Reverse() : val._data;
    public static implicit operator bdouble(double val)
        => new() { _data = Endian.SerializeBig ? val.Reverse() : val };

    public double Value
    {
        readonly get => this;
        set => this = value;
    }

    public override readonly string ToString()
        => Value.ToString();

    public VoidPtr Address { get { fixed (void* p = &this) return p; } }
}

#pragma warning restore CS8981 // Type name only contains lower-cased ascii characters
