using System.Buffers.Binary;
using System.Numerics;

namespace XREngine.Rendering.Shaders.Generation;

/// <summary>Byte layout and coordinate contract shared by the browser mesh shader and its uploads.</summary>
public static class BrowserShaderAbi
{
    public const string CoordinateConvention = "xrengine.webgpu.coordinates.v1";
    public const int VertexStride = 20;
    public const int PositionOffset = 0;
    public const int UvOffset = 12;
    public const int TransformBytes = 64;
    public const int MaterialBytes = 16;
    public const int UniformAlignment = 16;
    public const int MatrixColumnStride = 16;
    public const int ViewGroup = 0;
    public const int ViewBinding = 0;
    public const int MaterialGroup = 1;
    public const int TintBinding = 0;
    public const int ColorTextureBinding = 1;
    public const int ColorSamplerBinding = 2;

    /// <summary>
    /// Writes row-vector System.Numerics matrix elements in row order. WGSL reads
    /// these bytes as columns, so its column-vector multiplication has the same transform.
    /// The caller supplies a matrix already using WebGPU zero-to-one depth and positive viewport Y.
    /// </summary>
    public static void WriteTransform(Span<byte> destination, in Matrix4x4 transform)
    {
        if (destination.Length < TransformBytes)
            throw new ArgumentException("Browser transform storage requires 64 bytes.", nameof(destination));

        WriteFloat(destination, 0, transform.M11);
        WriteFloat(destination, 4, transform.M12);
        WriteFloat(destination, 8, transform.M13);
        WriteFloat(destination, 12, transform.M14);
        WriteFloat(destination, 16, transform.M21);
        WriteFloat(destination, 20, transform.M22);
        WriteFloat(destination, 24, transform.M23);
        WriteFloat(destination, 28, transform.M24);
        WriteFloat(destination, 32, transform.M31);
        WriteFloat(destination, 36, transform.M32);
        WriteFloat(destination, 40, transform.M33);
        WriteFloat(destination, 44, transform.M34);
        WriteFloat(destination, 48, transform.M41);
        WriteFloat(destination, 52, transform.M42);
        WriteFloat(destination, 56, transform.M43);
        WriteFloat(destination, 60, transform.M44);
    }

    /// <summary>Writes one tightly packed linear RGBA tint uniform.</summary>
    public static void WriteTint(Span<byte> destination, in Vector4 tint)
    {
        if (destination.Length < MaterialBytes)
            throw new ArgumentException("Browser material tint storage requires 16 bytes.", nameof(destination));

        WriteFloat(destination, 0, tint.X);
        WriteFloat(destination, 4, tint.Y);
        WriteFloat(destination, 8, tint.Z);
        WriteFloat(destination, 12, tint.W);
    }

    private static void WriteFloat(Span<byte> destination, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(destination.Slice(offset), value);
}
