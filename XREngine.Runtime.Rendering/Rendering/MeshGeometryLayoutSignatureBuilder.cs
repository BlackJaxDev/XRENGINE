using System.Text;

namespace XREngine.Rendering;

public static class MeshGeometryLayoutSignatureBuilder
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static MeshGeometryLayoutSignature Create(
        XRMesh? mesh,
        XRMeshRenderer? renderer,
        IEnumerable<KeyValuePair<string, XRDataBuffer>> namedBuffers,
        IndexSize primaryIndexSize,
        bool hasIndexBuffers,
        string drawCountSource)
    {
        List<KeyValuePair<string, XRDataBuffer>> buffers = [];
        foreach (KeyValuePair<string, XRDataBuffer> pair in namedBuffers)
            if (pair.Value is not null)
                buffers.Add(pair);

        buffers.Sort(static (a, b) =>
        {
            uint aBinding = a.Value.BindingIndexOverride ?? uint.MaxValue;
            uint bBinding = b.Value.BindingIndexOverride ?? uint.MaxValue;
            int bindingCompare = aBinding.CompareTo(bBinding);
            if (bindingCompare != 0)
                return bindingCompare;

            int targetCompare = a.Value.Target.CompareTo(b.Value.Target);
            return targetCompare != 0
                ? targetCompare
                : string.Compare(a.Key, b.Key, StringComparison.Ordinal);
        });

        ulong hash = FnvOffset;
        int vertexBufferCount = 0;
        int storageBufferCount = 0;
        int vertexAttributeCount = 0;
        int interleavedAttributeCount = 0;
        bool hasInstancedAttributes = false;
        bool hasRuntimeDeformationBuffers = false;

        Add(ref hash, (int)(mesh?.Type ?? EPrimitiveType.Triangles));
        Add(ref hash, mesh?.InterleavedStride ?? 0u);
        Add(ref hash, mesh?.VertexCount ?? 0);
        Add(ref hash, hasIndexBuffers);
        Add(ref hash, (int)primaryIndexSize);
        Add(ref hash, mesh?.HasMeshletPayload ?? false);
        Add(ref hash, drawCountSource);

        foreach (KeyValuePair<string, XRDataBuffer> pair in buffers)
        {
            string name = string.IsNullOrWhiteSpace(pair.Key) ? pair.Value.AttributeName : pair.Key;
            XRDataBuffer buffer = pair.Value;
            bool isVertexBuffer = buffer.Target == EBufferTarget.ArrayBuffer;
            bool isStorageBuffer = buffer.Target == EBufferTarget.ShaderStorageBuffer;
            bool isRuntimeDeformationBuffer = IsRuntimeDeformationBuffer(name);

            if (isVertexBuffer)
                vertexBufferCount++;
            if (isStorageBuffer)
                storageBufferCount++;
            if (buffer.InstanceDivisor > 0)
                hasInstancedAttributes = true;
            if (isRuntimeDeformationBuffer)
                hasRuntimeDeformationBuffers = true;

            Add(ref hash, name);
            Add(ref hash, (int)buffer.Target);
            Add(ref hash, (int)buffer.ComponentType);
            Add(ref hash, buffer.ComponentCount);
            Add(ref hash, buffer.ElementSize);
            Add(ref hash, buffer.ElementCount);
            Add(ref hash, buffer.Integral);
            Add(ref hash, buffer.Normalize);
            Add(ref hash, buffer.InstanceDivisor);
            Add(ref hash, buffer.BindingIndexOverride ?? uint.MaxValue);
            Add(ref hash, isRuntimeDeformationBuffer);

            if (buffer.InterleavedAttributes is { Length: > 0 } interleaved)
            {
                foreach (InterleavedAttribute attr in interleaved)
                {
                    vertexAttributeCount++;
                    interleavedAttributeCount++;
                    Add(ref hash, attr.AttributeName);
                    Add(ref hash, attr.AttribIndexOverride ?? uint.MaxValue);
                    Add(ref hash, attr.Offset);
                    Add(ref hash, (int)attr.Type);
                    Add(ref hash, attr.Count);
                    Add(ref hash, attr.Integral);
                }
            }
            else if (isVertexBuffer)
            {
                vertexAttributeCount++;
            }
        }

        bool hasPrecombinedBlendshapeDeltas = renderer?.HasValidPrecombinedBlendshapeDeltas == true;
        Add(ref hash, hasPrecombinedBlendshapeDeltas);

        string summary = BuildSummary(
            mesh,
            vertexBufferCount,
            storageBufferCount,
            vertexAttributeCount,
            interleavedAttributeCount,
            hasInstancedAttributes,
            hasRuntimeDeformationBuffers,
            mesh?.HasMeshletPayload ?? false,
            hasIndexBuffers,
            primaryIndexSize,
            drawCountSource,
            hasPrecombinedBlendshapeDeltas);

        return new(
            hash,
            summary,
            vertexBufferCount,
            storageBufferCount,
            vertexAttributeCount,
            interleavedAttributeCount,
            hasInstancedAttributes,
            hasRuntimeDeformationBuffers,
            mesh?.HasMeshletPayload ?? false,
            hasIndexBuffers,
            primaryIndexSize,
            mesh?.Type ?? EPrimitiveType.Triangles,
            drawCountSource);
    }

    private static bool IsRuntimeDeformationBuffer(string name)
        => name.StartsWith("Skinned", StringComparison.Ordinal) ||
           name.StartsWith("PrecombinedBlendshape", StringComparison.Ordinal) ||
           name.StartsWith("MeshDeform", StringComparison.Ordinal) ||
           name.StartsWith("Deformer", StringComparison.Ordinal);

    private static string BuildSummary(
        XRMesh? mesh,
        int vertexBufferCount,
        int storageBufferCount,
        int vertexAttributeCount,
        int interleavedAttributeCount,
        bool hasInstancedAttributes,
        bool hasRuntimeDeformationBuffers,
        bool hasMeshletPayload,
        bool hasIndexBuffers,
        IndexSize primaryIndexSize,
        string drawCountSource,
        bool hasPrecombinedBlendshapeDeltas)
    {
        StringBuilder builder = new(192);
        builder.Append("primitive=").Append(mesh?.Type.ToString() ?? "Triangles")
            .Append("; vertices=").Append(mesh?.VertexCount ?? 0)
            .Append("; vertexBuffers=").Append(vertexBufferCount)
            .Append("; storageBuffers=").Append(storageBufferCount)
            .Append("; attributes=").Append(vertexAttributeCount)
            .Append("; interleavedAttributes=").Append(interleavedAttributeCount)
            .Append("; instanced=").Append(hasInstancedAttributes)
            .Append("; deformation=").Append(hasRuntimeDeformationBuffers)
            .Append("; precombinedBlendshapes=").Append(hasPrecombinedBlendshapeDeltas)
            .Append("; meshlets=").Append(hasMeshletPayload)
            .Append("; indices=").Append(hasIndexBuffers ? primaryIndexSize.ToString() : "None")
            .Append("; drawCountSource=").Append(drawCountSource);
        return builder.ToString();
    }

    private static void Add(ref ulong hash, string? value)
    {
        if (value is null)
        {
            Add(ref hash, 0);
            return;
        }

        for (int i = 0; i < value.Length; i++)
            Add(ref hash, value[i]);
    }

    private static void Add(ref ulong hash, bool value)
        => Add(ref hash, value ? 1 : 0);

    private static void Add(ref ulong hash, int value)
        => Add(ref hash, unchecked((uint)value));

    private static void Add(ref ulong hash, uint value)
    {
        for (int i = 0; i < sizeof(uint); i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= FnvPrime;
        }
    }

    private static void Add(ref ulong hash, char value)
    {
        hash ^= (byte)value;
        hash *= FnvPrime;
        hash ^= (byte)(value >> 8);
        hash *= FnvPrime;
    }
}
