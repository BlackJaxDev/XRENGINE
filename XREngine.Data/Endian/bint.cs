using Extensions;

#pragma warning disable CS8981 // Type name only contains lower-cased ascii characters
using System.Runtime.InteropServices;

namespace XREngine.Data;

[Serializable]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct bint
{
    public int _data;

    public static implicit operator int(bint val)
        => Endian.SerializeBig ? val._data.Reverse() : val._data;
    public static implicit operator bint(int val)
        => new() { _data = Endian.SerializeBig ? val.Reverse() : val };

    public int Value
    {
        readonly get => this;
        set => this = value;
    }
    public override readonly string ToString()
        => Value.ToString();

    public VoidPtr OffsetAddress
    {
        get => Address + Value;
        set => Value = value - Address;
    }

    public VoidPtr Address { get { fixed (void* p = &this) return p; } }
}

#pragma warning restore CS8981 // Type name only contains lower-cased ascii characters
