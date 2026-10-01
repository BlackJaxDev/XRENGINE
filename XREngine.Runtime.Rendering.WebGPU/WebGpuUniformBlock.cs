using System.Buffers.Binary;
using System.Numerics;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

/// <summary>Retains one cooked uniform structure before its bytes are snapshotted for a draw.</summary>
internal sealed class WebGpuUniformBlock(ShaderAbiResourceContract contract)
{
    public ShaderAbiResourceContract Contract { get; } = contract;
    public byte[] Bytes { get; } = new byte[checked((int)contract.ByteSize)];

    public void Write(ShaderAbiMemberContract member, ReadOnlySpan<float> components)
    {
        int size = checked(components.Length * 4);
        string expectedType = components.Length switch
        {
            1 => "f32", 2 => "vec2<f32>", 3 => "vec3<f32>", 4 => "vec4<f32>",
            16 => "mat4x4<f32>", _ => string.Empty,
        };
        if (expectedType.Length == 0 || member.PhysicalType != expectedType ||
            member.ArrayCount != 0 || member.Size < size || member.Offset > Bytes.Length - size)
            throw new NotSupportedException($"WebGPU.Uniform.LayoutUnsupported: '{member.ProviderName}' has an incompatible physical extent.");
        Span<byte> destination = Bytes.AsSpan(checked((int)member.Offset), size);
        for (int i = 0; i < components.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(destination[(i * 4)..], components[i]);
    }

    public void WriteMatrix(ShaderAbiMemberContract member, in Matrix4x4 matrix)
    {
        if (member.MatrixOrder != ShaderAbiMatrixOrder.ColumnMajor || member.MatrixStride != 16 || member.Size != 64)
            throw new NotSupportedException($"WebGPU.Uniform.MatrixUnsupported: '{member.ProviderName}' requires the explicit column-major mat4x4 ABI.");
        // Engine row-vector composition is transposed by writing row-major source
        // fields as WGSL columns. No runtime transpose or ambiguous CLR packing.
        Write(member,
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        ]);
    }

    public void WriteInteger(ShaderAbiMemberContract member, uint value)
    {
        if (member.PhysicalType is not ("u32" or "i32") ||
            member.ArrayCount != 0 || member.Size != 4 || member.Offset > Bytes.Length - 4)
            throw new NotSupportedException($"WebGPU.Uniform.IntegerUnsupported: '{member.ProviderName}' requires a scalar 32-bit ABI.");
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(checked((int)member.Offset), 4), value);
    }
}
