using Extensions;

#pragma warning disable CS8981 // Type name only contains lower-cased ascii characters
using System.Runtime.InteropServices;

namespace XREngine.Data;

[Serializable]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct bushort
{
    public ushort _data;

    public static implicit operator ushort(bushort val)
        => Endian.SerializeBig ? val._data.Reverse() : val._data;
    public static implicit operator bushort(ushort val)
        => new() { _data = Endian.SerializeBig ? val.Reverse() : val };

    public ushort Value
    {
        readonly get => this;
        set => this = value;
    }

    public override readonly string ToString()
        => Value.ToString();

    public VoidPtr Address { get { fixed (void* p = &this) return p; } }
}

#pragma warning restore CS8981 // Type name only contains lower-cased ascii characters
