using System.Runtime.InteropServices;

namespace XREngine.Rendering.Models.Gaussian.Cache.Encoding;

/// <summary>
/// The decoded record of one splat, for CPU use and for upload to the GPU. The size is 48 bytes.
/// This type holds plain data. The fields are public by design.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GaussianCacheSplatRecord
{
    /// <summary>The size of one record in bytes.</summary>
    public const int ByteSize = 48;

    /// <summary>The value of <see cref="Triangle"/> for a splat that is not bound to a triangle.</summary>
    public const uint StaticTriangle = uint.MaxValue;

    /// <summary>Bit 0 of <see cref="Flags"/>. It is set for a static splat.</summary>
    public const ushort StaticFlag = 1;

    /// <summary>The index of the driver triangle, or <see cref="StaticTriangle"/>.</summary>
    public uint Triangle;

    /// <summary>For a bound splat, u. For a static splat, grid x.</summary>
    public ushort A;

    /// <summary>For a bound splat, v. For a static splat, grid y.</summary>
    public ushort B;

    /// <summary>For a bound splat, the half-float bits of h. For a static splat, grid z.</summary>
    public ushort C;

    /// <summary>Bit 0 means static.</summary>
    public ushort Flags;

    /// <summary>The rotation as 16-bit signed normalized values.</summary>
    public short RotX, RotY, RotZ, RotW;

    public float ScaleX, ScaleY, ScaleZ;

    /// <summary>sRGB red, green, and blue, and linear alpha. The alpha is the opacity.</summary>
    public uint ColorRgba8;

    public uint ChildStart, ChildCount, Reserved;
}
