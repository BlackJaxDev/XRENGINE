using System;
using System.Collections.Generic;
using System.Text;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public readonly record struct MeshGeometryLayoutSignature(
    ulong StableHash,
    string DebugSummary,
    int VertexBufferCount,
    int StorageBufferCount,
    int VertexAttributeCount,
    int InterleavedAttributeCount,
    bool HasInstancedAttributes,
    bool HasRuntimeDeformationBuffers,
    bool HasMeshletPayload,
    bool HasIndexBuffers,
    IndexSize PrimaryIndexSize,
    EPrimitiveType PrimitiveType,
    string DrawCountSource)
{
    public static MeshGeometryLayoutSignature Empty { get; } =
        new(14695981039346656037UL, "empty", 0, 0, 0, 0, false, false, false, false, IndexSize.FourBytes, EPrimitiveType.Triangles, "None");
}
