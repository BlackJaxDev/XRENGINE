using System.Numerics;

namespace XREngine.Rendering;

public partial class XRMesh
{
    public void SetPosition(uint index, Vector3 value)
    {
        if (Interleaved)
            InterleavedVertexBuffer?.SetVector3AtOffset(index * InterleavedStride + PositionOffset, value);
        else
            PositionsBuffer?.SetVector3(index, value);
    }
    public Vector3 GetPosition(uint index)
        => Interleaved
            ? InterleavedVertexBuffer?.GetVector3AtOffset(index * InterleavedStride + PositionOffset) ?? Vector3.Zero
            : PositionsBuffer?.GetVector3(index) ?? Vector3.Zero;

    public void SetNormal(uint index, Vector3 value)
    {
        if (Interleaved && NormalOffset.HasValue)
            InterleavedVertexBuffer?.SetVector3AtOffset(index * InterleavedStride + NormalOffset.Value, value);
        else if (!Interleaved)
            NormalsBuffer?.SetVector3(index, value);
    }
    public Vector3 GetNormal(uint index)
    {
        if (Interleaved && NormalOffset.HasValue)
            return InterleavedVertexBuffer?.GetVector3AtOffset(index * InterleavedStride + NormalOffset.Value) ?? Vector3.Zero;
        return Interleaved ? Vector3.Zero : (NormalsBuffer?.GetVector3(index) ?? Vector3.Zero);
    }

    public void SetTangent(uint index, Vector3 direction, float bitangentSign = 1.0f)
    {
        Vector4 value = new(direction, bitangentSign);
        if (Interleaved && TangentOffset.HasValue)
            InterleavedVertexBuffer?.SetVector4AtOffset(index * InterleavedStride + TangentOffset.Value, value);
        else if (!Interleaved)
            TangentsBuffer?.SetVector4(index, value);
    }
    public Vector3 GetTangent(uint index)
    {
        Vector4 v4 = GetTangentWithSign(index);
        return new Vector3(v4.X, v4.Y, v4.Z);
    }
    public Vector4 GetTangentWithSign(uint index)
    {
        if (Interleaved && TangentOffset.HasValue)
            return InterleavedVertexBuffer?.GetVector4AtOffset(index * InterleavedStride + TangentOffset.Value) ?? new Vector4(0, 0, 0, 1);
        return Interleaved ? new Vector4(0, 0, 0, 1) : (TangentsBuffer?.GetVector4(index) ?? new Vector4(0, 0, 0, 1));
    }

    public void SetColor(uint index, Vector4 value, uint colorIndex)
    {
        if (Interleaved && ColorOffset is not null && ColorCount > colorIndex)
        {
            uint offset = ColorOffset.Value + colorIndex * 16u;
            InterleavedVertexBuffer?.SetVector4AtOffset(index * InterleavedStride + offset, value);
        }
        else if (!Interleaved && ColorBuffers is not null && colorIndex < ColorBuffers.Length)
        {
            XRDataBuffer? colorBuffer = ColorBuffers[(int)colorIndex];
            colorBuffer?.SetVector4(index, value);
        }
    }
    public Vector4 GetColor(uint index, uint colorIndex)
    {
        if (Interleaved && ColorOffset is not null && ColorCount > colorIndex)
        {
            uint offset = ColorOffset.Value + colorIndex * 16u;
            return InterleavedVertexBuffer?.GetVector4AtOffset(index * InterleavedStride + offset) ?? Vector4.Zero;
        }
        if (!Interleaved && ColorBuffers is not null && colorIndex < ColorBuffers.Length)
        {
            XRDataBuffer? colorBuffer = ColorBuffers[(int)colorIndex];
            return colorBuffer?.GetVector4(index) ?? Vector4.Zero;
        }
        return Vector4.Zero;
    }

    public void SetTexCoord(uint index, Vector2 value, uint texCoordIndex)
    {
        if (Interleaved && TexCoordOffset is not null && TexCoordCount > texCoordIndex)
        {
            uint offset = TexCoordOffset.Value + texCoordIndex * 8u;
            InterleavedVertexBuffer?.SetVector2AtOffset(index * InterleavedStride + offset, value);
        }
        else if (TexCoordBuffers is not null && texCoordIndex < TexCoordBuffers.Length)
        {
            XRDataBuffer? texCoordBuffer = TexCoordBuffers[(int)texCoordIndex];
            texCoordBuffer?.SetVector2(index, value);
        }
    }
    public Vector2 GetTexCoord(uint index, uint texCoordIndex)
    {
        if (Interleaved && TexCoordOffset is not null && TexCoordCount > texCoordIndex)
        {
            uint offset = TexCoordOffset.Value + texCoordIndex * 8u;
            return InterleavedVertexBuffer?.GetVector2AtOffset(index * InterleavedStride + offset) ?? Vector2.Zero;
        }
        if (!Interleaved && TexCoordBuffers is not null && texCoordIndex < TexCoordBuffers.Length)
        {
            XRDataBuffer? texCoordBuffer = TexCoordBuffers[(int)texCoordIndex];
            return texCoordBuffer?.GetVector2(index) ?? Vector2.Zero;
        }
        return Vector2.Zero;
    }
}