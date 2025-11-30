using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    private void InitMeshBuffers(bool hasNormals, bool hasTangents, int colorCount, int texCoordCount, bool? forceInterleaved = null)
    {
        using var _ = Engine.Profiler.Start();
        ColorCount = (uint)colorCount;
        TexCoordCount = (uint)texCoordCount;
        bool targetInterleaved = forceInterleaved ?? Engine.Rendering.Settings.UseInterleavedMeshBuffer;
        Interleaved = targetInterleaved;

        if (targetInterleaved)
        {
            InterleavedVertexBuffer = new XRDataBuffer(ECommonBufferType.InterleavedVertex.ToString(), EBufferTarget.ArrayBuffer, false)
            {
                BindingIndexOverride = 0
            };

            List<InterleavedAttribute> attributes = [];
            attributes.Add((null, ECommonBufferType.Position.ToString(), 0u, EComponentType.Float, 3, false));

            uint stride = 12u;
            PositionOffset = 0;

            if (hasNormals)
            {
                NormalOffset = stride;
                attributes.Add((null, ECommonBufferType.Normal.ToString(), stride, EComponentType.Float, 3, false));
                stride += 12u;
            }
            if (hasTangents)
            {
                TangentOffset = stride;
                attributes.Add((null, ECommonBufferType.Tangent.ToString(), stride, EComponentType.Float, 3, false));
                stride += 12u;
            }
            if (colorCount > 0)
            {
                ColorOffset = stride;
                for (int i = 0; i < colorCount; i++)
                {
                    string binding = $"{ECommonBufferType.Color}{i}";
                    attributes.Add((null, binding, stride, EComponentType.Float, 4, false));
                    stride += 16u;
                }
            }
            if (texCoordCount > 0)
            {
                TexCoordOffset = stride;
                for (int i = 0; i < texCoordCount; i++)
                {
                    string binding = $"{ECommonBufferType.TexCoord}{i}";
                    attributes.Add((null, binding, stride, EComponentType.Float, 2, false));
                    stride += 8u;
                }
            }

            InterleavedStride = stride;
            InterleavedVertexBuffer.InterleavedAttributes = [.. attributes];
            InterleavedVertexBuffer.Allocate(stride, (uint)VertexCount);
            Buffers.Add(ECommonBufferType.InterleavedVertex.ToString(), InterleavedVertexBuffer);
        }
        else
        {
            PositionsBuffer = new XRDataBuffer(ECommonBufferType.Position.ToString(), EBufferTarget.ArrayBuffer, false);
            PositionsBuffer.Allocate<Vector3>((uint)VertexCount);
            Buffers.Add(ECommonBufferType.Position.ToString(), PositionsBuffer);

            if (hasNormals)
            {
                NormalsBuffer = new XRDataBuffer(ECommonBufferType.Normal.ToString(), EBufferTarget.ArrayBuffer, false);
                NormalsBuffer.Allocate<Vector3>((uint)VertexCount);
                Buffers.Add(ECommonBufferType.Normal.ToString(), NormalsBuffer);
            }
            if (hasTangents)
            {
                TangentsBuffer = new XRDataBuffer(ECommonBufferType.Tangent.ToString(), EBufferTarget.ArrayBuffer, false);
                TangentsBuffer.Allocate<Vector3>((uint)VertexCount);
                Buffers.Add(ECommonBufferType.Tangent.ToString(), TangentsBuffer);
            }
            if (colorCount > 0)
            {
                ColorBuffers = new XRDataBuffer[colorCount];
                for (int i = 0; i < colorCount; i++)
                {
                    string binding = $"{ECommonBufferType.Color}{i}";
                    var buf = new XRDataBuffer(binding, EBufferTarget.ArrayBuffer, false);
                    buf.Allocate<System.Numerics.Vector4>((uint)VertexCount);
                    ColorBuffers[i] = buf;
                    Buffers.Add(binding, buf);
                }
            }
            if (texCoordCount > 0)
            {
                TexCoordBuffers = new XRDataBuffer[texCoordCount];
                for (int i = 0; i < texCoordCount; i++)
                {
                    string binding = $"{ECommonBufferType.TexCoord}{i}";
                    var buf = new XRDataBuffer(binding, EBufferTarget.ArrayBuffer, false);
                    buf.Allocate<System.Numerics.Vector2>((uint)VertexCount);
                    TexCoordBuffers[i] = buf;
                    Buffers.Add(binding, buf);
                }
            }
        }
    }
}