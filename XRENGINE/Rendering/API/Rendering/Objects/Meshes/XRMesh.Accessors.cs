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

    public void SetTangent(uint index, Vector3 value)
    {
        if (Interleaved && TangentOffset.HasValue)
            InterleavedVertexBuffer?.SetVector3AtOffset(index * InterleavedStride + TangentOffset.Value, value);
        else if (!Interleaved)
            TangentsBuffer?.SetVector3(index, value);
    }
    public Vector3 GetTangent(uint index)
    {
        if (Interleaved && TangentOffset.HasValue)
            return InterleavedVertexBuffer?.GetVector3AtOffset(index * InterleavedStride + TangentOffset.Value) ?? Vector3.Zero;
        return Interleaved ? Vector3.Zero : (TangentsBuffer?.GetVector3(index) ?? Vector3.Zero);
    }

    public void SetColor(uint index, Vector4 value, uint colorIndex)
    {
        if (Interleaved && ColorOffset is not null && ColorCount > colorIndex)
        {
            uint offset = ColorOffset.Value + colorIndex * 16u;
            InterleavedVertexBuffer?.SetVector4AtOffset(index * InterleavedStride + offset, value);
        }
        else
            ColorBuffers?[(int)colorIndex].SetVector4(index, value);
    }
    public Vector4 GetColor(uint index, uint colorIndex)
    {
        if (Interleaved && ColorOffset is not null && ColorCount > colorIndex)
        {
            uint offset = ColorOffset.Value + colorIndex * 16u;
            return InterleavedVertexBuffer?.GetVector4AtOffset(index * InterleavedStride + offset) ?? Vector4.Zero;
        }
        return Interleaved ? Vector4.Zero : (ColorBuffers?[(int)colorIndex].GetVector4(index) ?? Vector4.Zero);
    }

    public void SetTexCoord(uint index, Vector2 value, uint texCoordIndex)
    {
        if (Interleaved && TexCoordOffset is not null && TexCoordCount > texCoordIndex)
        {
            uint offset = TexCoordOffset.Value + texCoordIndex * 8u;
            InterleavedVertexBuffer?.SetVector2AtOffset(index * InterleavedStride + offset, value);
        }
        else
            TexCoordBuffers?[(int)texCoordIndex].SetVector2(index, value);
    }
    public Vector2 GetTexCoord(uint index, uint texCoordIndex)
    {
        if (Interleaved && TexCoordOffset is not null && TexCoordCount > texCoordIndex)
        {
            uint offset = TexCoordOffset.Value + texCoordIndex * 8u;
            return InterleavedVertexBuffer?.GetVector2AtOffset(index * InterleavedStride + offset) ?? Vector2.Zero;
        }
        return Interleaved ? Vector2.Zero : (TexCoordBuffers?[(int)texCoordIndex].GetVector2(index) ?? Vector2.Zero);
    }
}