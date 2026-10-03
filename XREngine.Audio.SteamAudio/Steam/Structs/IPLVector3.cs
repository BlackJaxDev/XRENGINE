using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

[StructLayout(LayoutKind.Sequential)]
public struct IPLVector3
{
    public float x;
    public float y;
    public float z;

    public IPLVector3()
    {
        x = 0.0f;
        y = 0.0f;
        z = 0.0f;
    }
    public IPLVector3(float value)
    {
        x = value;
        y = value;
        z = value;
    }
    public IPLVector3(float x, float y, float z)
    {
        this.x = x;
        this.y = y;
        this.z = z;
    }

    public static implicit operator IPLVector3(Vector3 value)
        => new(value.X, value.Y, value.Z);
    public static implicit operator Vector3(IPLVector3 value)
        => new(value.x, value.y, value.z);

    public static implicit operator IPLVector3((float x, float y, float z) value)
        => new(value.x, value.y, value.z);
    public static implicit operator IPLVector3((float x, float y) value)
        => new(value.x, value.y, 0.0f);

    public static implicit operator IPLVector3(float value)
        => new(value);
    public static implicit operator IPLVector3(float[] value)
        => new(value[0], value[1], value[2]);
}