using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.WebGPU;

/// <summary>Immutable physical image of one ownership-validated payload, including its exact source primitive permutation.</summary>
internal sealed class WebGpuMeshletGeometry : IDisposable
{
    private readonly record struct Triangle(uint A, uint B, uint C);
    private readonly XRMesh _mesh;
    private readonly MeshletPayload _payload;
    private readonly long _geometryRevision;
    private readonly long _validationRevision;
    private readonly ulong _ownerToken;
    private byte[]? _initialImage;

    internal WebGpuOwnedStorageBuffer Arena { get; }
    internal uint MeshletCount { get; }
    internal uint SourceTriangleCount { get; }
    internal uint VertexCount { get; }
    internal uint DescriptorWordOffset => 0;
    internal uint RemapWordOffset { get; }
    internal uint RemapCount { get; }
    internal uint TriangleWordOffset { get; }
    internal uint TriangleByteCount { get; }
    internal uint PrimitiveWordOffset { get; }
    internal uint PrimitiveCount { get; }
    internal int LeaseCount { get; set; }

    internal WebGpuMeshletGeometry(WebGpuRendererHost renderer, XRMesh mesh, MeshletPayload payload)
    {
        if (!payload.IsValidatedFor(mesh) || !payload.HasMeshlets || payload.PayloadVersion != MeshletPayload.CurrentPayloadVersion)
            throw Rejected("PayloadUnavailable", "an ownership-validated immutable portable meshlet payload is required; runtime cooking is forbidden");
        _mesh = mesh;
        _payload = payload;
        _geometryRevision = mesh.GeometryRevision;
        _validationRevision = payload.ValidationRevision;
        _ownerToken = payload.OwnerValidationToken;
        MeshletCount = checked((uint)payload.Meshlets.Length);
        SourceTriangleCount = checked((uint)payload.SourceTriangleCount);
        VertexCount = checked((uint)payload.SourceVertexCount);
        RemapWordOffset = checked(MeshletCount * 20);
        RemapCount = checked((uint)payload.VertexIndices.Length);
        TriangleWordOffset = checked(RemapWordOffset + RemapCount);
        TriangleByteCount = checked((uint)payload.TriangleIndices.Length);
        PrimitiveWordOffset = checked(TriangleWordOffset + (TriangleByteCount + 3) / 4);
        PrimitiveCount = checked(MeshletCount * MeshletPayload.PortableMaxTriangles);
        ulong byteCount = ((ulong)PrimitiveWordOffset + PrimitiveCount) * 4;
        if (byteCount > (ulong)renderer.MaximumAdvancedStorageBytes || SourceTriangleCount >= 0x40000000u)
            throw Rejected("GeometryCapacity", "the immutable meshlet arena exceeds the device storage range");
        byte[] bytes = new byte[checked((int)byteCount)];
        Span<byte> image = bytes;
        for (int index = 0; index < payload.Meshlets.Length; index++)
        {
            CpuMeshletDescriptor descriptor = payload.Meshlets[index];
            Span<byte> target = image.Slice(index * 80, 80);
            WriteVector(target, descriptor.BoundsSphere);
            Write(target, 4, descriptor.VertexOffset);
            Write(target, 5, descriptor.TriangleOffset);
            Write(target, 6, descriptor.VertexCount);
            Write(target, 7, descriptor.TriangleCount);
            WriteVector(target[32..], descriptor.Cone);
            WriteVector(target[48..], descriptor.ConeApex);
            Write(target, 16, descriptor.PackedCone);
        }
        MemoryMarshal.AsBytes(payload.VertexIndices.AsSpan()).CopyTo(image[(int)(RemapWordOffset * 4)..]);
        payload.TriangleIndices.AsSpan().CopyTo(image[(int)(TriangleWordOffset * 4)..]);
        BuildSourcePrimitiveMap(mesh, payload, image[(int)(PrimitiveWordOffset * 4)..]);
        Arena = new(renderer, "Immutable authored meshlet geometry");
        _initialImage = bytes;
    }

    internal void Prepare()
    {
        if (_initialImage is not { } image) return;
        Arena.EnsureCapacity(image.Length);
        Arena.UploadPreparation(image);
        _initialImage = null;
    }

    internal bool Matches(XRMesh mesh, MeshletPayload payload)
        => ReferenceEquals(mesh, _mesh) && ReferenceEquals(payload, _payload) &&
           _geometryRevision == mesh.GeometryRevision && _validationRevision == payload.ValidationRevision &&
           _ownerToken == payload.OwnerValidationToken && payload.IsValidatedFor(mesh);

    private static void BuildSourcePrimitiveMap(XRMesh mesh, MeshletPayload payload, Span<byte> output)
    {
        // This is an immutable topology permutation, prepared once per payload generation.
        // It does not regenerate meshlets or replay the source index stream during rendering.
        int[] source = mesh.GetIndices(EPrimitiveType.Triangles)
            ?? throw Rejected("SourceTopologyMissing", "the validated source triangle identities are unavailable");
        if (source.Length != checked(payload.SourceTriangleCount * 3))
            throw Rejected("SourceTopologyChanged", "the source triangle extent differs from its validated payload");
        Dictionary<Triangle, Queue<uint>> originals = new(payload.SourceTriangleCount);
        for (int primitive = 0; primitive < payload.SourceTriangleCount; primitive++)
        {
            int start = primitive * 3;
            Triangle key = new(checked((uint)source[start]), checked((uint)source[start + 1]), checked((uint)source[start + 2]));
            if (!originals.TryGetValue(key, out Queue<uint>? matches)) originals.Add(key, matches = new());
            matches.Enqueue((uint)primitive);
        }
        output.Fill(0xff);
        uint matched = 0;
        for (int meshlet = 0; meshlet < payload.Meshlets.Length; meshlet++)
        {
            CpuMeshletDescriptor descriptor = payload.Meshlets[meshlet];
            for (uint triangle = 0; triangle < descriptor.TriangleCount; triangle++)
            {
                int start = checked((int)(descriptor.TriangleOffset + triangle * 3));
                uint a = payload.VertexIndices[checked((int)descriptor.VertexOffset + payload.TriangleIndices[start])];
                uint b = payload.VertexIndices[checked((int)descriptor.VertexOffset + payload.TriangleIndices[start + 1])];
                uint c = payload.VertexIndices[checked((int)descriptor.VertexOffset + payload.TriangleIndices[start + 2])];
                uint selected = uint.MaxValue, rotation = 0;
                for (uint turn = 0; turn < 3; turn++)
                {
                    Triangle key = turn switch { 0 => new(a, b, c), 1 => new(b, c, a), _ => new(c, a, b) };
                    if (originals.TryGetValue(key, out Queue<uint>? matches) && matches.TryPeek(out uint candidate) && candidate < selected)
                    { selected = candidate; rotation = turn; }
                }
                if (selected == uint.MaxValue)
                    throw Rejected("SourceTopologyMismatch", "a cooked meshlet triangle cannot be matched to the original oriented primitive stream");
                Triangle selectedKey = rotation switch { 0 => new(a, b, c), 1 => new(b, c, a), _ => new(c, a, b) };
                originals[selectedKey].Dequeue();
                Write(output, checked(meshlet * (int)MeshletPayload.PortableMaxTriangles + (int)triangle), selected | rotation << 30);
                matched++;
            }
        }
        if (matched != payload.SourceTriangleCount)
            throw Rejected("SourceTopologyIncomplete", "the immutable meshlet stream does not cover every source primitive exactly once");
    }

    private static void WriteVector(Span<byte> destination, Vector4 value)
    {
        Write(destination, 0, BitConverter.SingleToUInt32Bits(value.X));
        Write(destination, 1, BitConverter.SingleToUInt32Bits(value.Y));
        Write(destination, 2, BitConverter.SingleToUInt32Bits(value.Z));
        Write(destination, 3, BitConverter.SingleToUInt32Bits(value.W));
    }
    private static void Write(Span<byte> destination, int word, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(word * 4, 4), value);
    private static NotSupportedException Rejected(string code, string reason) => new($"WebGPU.Meshlets.{code}: {reason}.");
    public void Dispose() { _initialImage = null; Arena.Dispose(); }
}
