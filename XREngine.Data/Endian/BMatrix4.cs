using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Data;

[Serializable]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct BMatrix4
{
    fixed float _data[16];

    public bfloat* Data { get { fixed (float* ptr = _data) return (bfloat*)ptr; } }

    public float this[int x, int y]
    {
        get => Data[(y << 2) + x];
        set => Data[(y << 2) + x] = value;
    }
    public float this[int index]
    {
        get => Data[index];
        set => Data[index] = value;
    }

    public override string ToString()
        => $"({this[0]},{this[1]},{this[2]},{this[3]})({this[4]},{this[5]},{this[6]},{this[7]})({this[8]},{this[9]},{this[10]},{this[11]})({this[12]},{this[13]},{this[14]},{this[15]})";

    public static implicit operator Matrix4x4(BMatrix4 bm)
    {
        Matrix4x4 m = new();
        float* dPtr = (float*)&m;
        bfloat* sPtr = (bfloat*)&bm;
        for (int i = 0; i < 16; i++)
            dPtr[i] = sPtr[i];
        return m;
    }

    public static implicit operator BMatrix4(Matrix4x4 m)
    {
        BMatrix4 bm = new();
        bfloat* dPtr = (bfloat*)&bm;
        float* sPtr = (float*)&m;
        for (int i = 0; i < 16; i++)
            dPtr[i] = sPtr[i];
        return bm;
    }
}