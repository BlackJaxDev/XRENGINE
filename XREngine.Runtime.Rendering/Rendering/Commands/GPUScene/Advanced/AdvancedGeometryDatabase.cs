using System.Runtime.InteropServices;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Canonical geometry table plus scene-owned immutable source arenas.
/// </summary>
public sealed class AdvancedGeometryDatabase
{
    private const uint StaticVertexBufferIndex = 1u;
    private const uint IndexBufferIndex = 2u;
    private const uint PreSkinnedCurrentBufferIndex = 3u;
    private const uint PreSkinnedPreviousBufferIndex = 4u;
    private const uint MeshletDescriptorBufferIndex = 5u;
    private const uint MeshletVertexIndexBufferIndex = 6u;
    private const uint MeshletTriangleWordBufferIndex = 7u;

    public AdvancedGeometryDatabase(
        uint recordCapacity,
        uint staticVertexCapacityBytes,
        uint indexCapacityBytes,
        uint preSkinnedCurrentCapacityBytes,
        uint preSkinnedPreviousCapacityBytes,
        uint meshletCapacityBytes,
        uint meshletDescriptorCapacityBytes,
        uint meshletVertexIndexCapacityBytes,
        uint meshletTriangleWordCapacityBytes)
    {
        Records = new AdvancedGpuRecordTable<AdvancedGeometryRecord>(recordCapacity);
        StaticVertexArena = new AdvancedImmutableByteArena(StaticVertexBufferIndex, staticVertexCapacityBytes);
        IndexArena = new AdvancedImmutableByteArena(IndexBufferIndex, indexCapacityBytes);
        PreSkinnedCurrentArena = new AdvancedImmutableByteArena(PreSkinnedCurrentBufferIndex, preSkinnedCurrentCapacityBytes);
        PreSkinnedPreviousArena = new AdvancedImmutableByteArena(PreSkinnedPreviousBufferIndex, preSkinnedPreviousCapacityBytes);
        // MeshletBytes is retained only for source compatibility with early Phase 3
        // capacity profiles. New profiles must budget all three ABI streams explicitly.
        MeshletDescriptorArena = new AdvancedImmutableByteArena(
            MeshletDescriptorBufferIndex,
            meshletDescriptorCapacityBytes == 0u ? meshletCapacityBytes : meshletDescriptorCapacityBytes);
        MeshletVertexIndexArena = new AdvancedImmutableByteArena(
            MeshletVertexIndexBufferIndex,
            meshletVertexIndexCapacityBytes);
        MeshletTriangleWordArena = new AdvancedImmutableByteArena(
            MeshletTriangleWordBufferIndex,
            meshletTriangleWordCapacityBytes);
    }

    public AdvancedGpuRecordTable<AdvancedGeometryRecord> Records { get; }

    public AdvancedImmutableByteArena StaticVertexArena { get; }

    public AdvancedImmutableByteArena IndexArena { get; }

    public AdvancedImmutableByteArena PreSkinnedCurrentArena { get; }

    public AdvancedImmutableByteArena PreSkinnedPreviousArena { get; }

    public AdvancedImmutableByteArena MeshletDescriptorArena { get; }

    public AdvancedImmutableByteArena MeshletVertexIndexArena { get; }

    public AdvancedImmutableByteArena MeshletTriangleWordArena { get; }

    public bool TryAddStatic(
        ReadOnlySpan<byte> vertices,
        ReadOnlySpan<uint> indices,
        in AdvancedGeometryRegistration registration,
        out AdvancedGpuHandle handle)
    {
        handle = AdvancedGpuHandle.Invalid;
        if (!ValidateRegistration(vertices, indices, registration) ||
            Records.Count >= Records.Capacity)
        {
            return false;
        }

        ReadOnlySpan<byte> indexBytes = MemoryMarshal.AsBytes(indices);
        if (!StaticVertexArena.CanAppend((uint)vertices.Length, registration.VertexStride) ||
            !IndexArena.CanAppend((uint)indexBytes.Length, sizeof(uint)))
        {
            return false;
        }

        StaticVertexArena.TryAppend(vertices, registration.VertexStride, out AdvancedBufferReference vertexData);
        IndexArena.TryAppend(indexBytes, sizeof(uint), out AdvancedBufferReference indexData);
        AdvancedGeometryRecord record = CreateResidentRecord(
            registration,
            EAdvancedGeometrySource.Static,
            vertexData,
            vertexData,
            indexData,
            AdvancedBufferReference.Invalid,
            AdvancedBufferReference.Invalid,
            AdvancedBufferReference.Invalid);
        return Records.TryAdd(record, out handle);
    }

    public bool TryAddPreSkinned(
        ReadOnlySpan<byte> currentVertices,
        ReadOnlySpan<byte> previousVertices,
        ReadOnlySpan<uint> indices,
        in AdvancedGeometryRegistration registration,
        out AdvancedGpuHandle handle)
    {
        handle = AdvancedGpuHandle.Invalid;
        if (!ValidateRegistration(currentVertices, indices, registration) ||
            previousVertices.Length != currentVertices.Length ||
            Records.Count >= Records.Capacity)
        {
            return false;
        }

        ReadOnlySpan<byte> indexBytes = MemoryMarshal.AsBytes(indices);
        if (!PreSkinnedCurrentArena.CanAppend((uint)currentVertices.Length, registration.VertexStride) ||
            !PreSkinnedPreviousArena.CanAppend((uint)previousVertices.Length, registration.VertexStride) ||
            !IndexArena.CanAppend((uint)indexBytes.Length, sizeof(uint)))
        {
            return false;
        }

        PreSkinnedCurrentArena.TryAppend(currentVertices, registration.VertexStride, out AdvancedBufferReference currentData);
        PreSkinnedPreviousArena.TryAppend(previousVertices, registration.VertexStride, out AdvancedBufferReference previousData);
        IndexArena.TryAppend(indexBytes, sizeof(uint), out AdvancedBufferReference indexData);
        AdvancedGeometryRecord record = CreateResidentRecord(
            registration,
            EAdvancedGeometrySource.PreSkinnedCurrentAndPrevious,
            currentData,
            previousData,
            indexData,
            AdvancedBufferReference.Invalid,
            AdvancedBufferReference.Invalid,
            AdvancedBufferReference.Invalid);
        return Records.TryAdd(record, out handle);
    }

    public bool TryAddMeshletLocal(
        ReadOnlySpan<byte> vertices,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<AdvancedMeshletDescriptor> meshletDescriptors,
        ReadOnlySpan<uint> meshletVertexIndices,
        ReadOnlySpan<uint> meshletTriangleWords,
        in AdvancedGeometryRegistration registration,
        out AdvancedGpuHandle handle)
    {
        handle = AdvancedGpuHandle.Invalid;
        if (!ValidateRegistration(vertices, indices, registration) ||
            registration.MeshletCount == 0u ||
            registration.MeshletCount != (uint)meshletDescriptors.Length ||
            !ValidateMeshletStreams(meshletDescriptors, meshletVertexIndices, meshletTriangleWords) ||
            Records.Count >= Records.Capacity)
        {
            return false;
        }

        ReadOnlySpan<byte> indexBytes = MemoryMarshal.AsBytes(indices);
        ReadOnlySpan<byte> descriptorBytes = MemoryMarshal.AsBytes(meshletDescriptors);
        ReadOnlySpan<byte> meshletVertexIndexBytes = MemoryMarshal.AsBytes(meshletVertexIndices);
        ReadOnlySpan<byte> meshletTriangleWordBytes = MemoryMarshal.AsBytes(meshletTriangleWords);
        if (!StaticVertexArena.CanAppend((uint)vertices.Length, registration.VertexStride) ||
            !IndexArena.CanAppend((uint)indexBytes.Length, sizeof(uint)) ||
            !MeshletDescriptorArena.CanAppend(
                (uint)descriptorBytes.Length,
                checked((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<AdvancedMeshletDescriptor>())) ||
            !MeshletVertexIndexArena.CanAppend((uint)meshletVertexIndexBytes.Length, sizeof(uint)) ||
            !MeshletTriangleWordArena.CanAppend((uint)meshletTriangleWordBytes.Length, sizeof(uint)))
        {
            return false;
        }

        StaticVertexArena.TryAppend(vertices, registration.VertexStride, out AdvancedBufferReference vertexData);
        IndexArena.TryAppend(indexBytes, sizeof(uint), out AdvancedBufferReference indexData);
        MeshletDescriptorArena.TryAppend(
            descriptorBytes,
            checked((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<AdvancedMeshletDescriptor>()),
            out AdvancedBufferReference descriptorData);
        MeshletVertexIndexArena.TryAppend(meshletVertexIndexBytes, sizeof(uint), out AdvancedBufferReference vertexIndexData);
        MeshletTriangleWordArena.TryAppend(meshletTriangleWordBytes, sizeof(uint), out AdvancedBufferReference triangleWordData);
        AdvancedGeometryRecord record = CreateResidentRecord(
            registration,
            EAdvancedGeometrySource.MeshletLocal,
            vertexData,
            vertexData,
            indexData,
            descriptorData,
            vertexIndexData,
            triangleWordData);
        return Records.TryAdd(record, out handle);
    }

    /// <summary>
    /// Retained only so older producers fail explicitly instead of silently
    /// registering an opaque meshlet blob. Canonical meshlets require descriptor,
    /// vertex-index, and padded triangle-word streams.
    /// </summary>
    public bool TryAddMeshletLocal(
        ReadOnlySpan<byte> vertices,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<byte> opaqueMeshletData,
        uint meshletStride,
        in AdvancedGeometryRegistration registration,
        out AdvancedGpuHandle handle)
    {
        _ = vertices;
        _ = indices;
        _ = opaqueMeshletData;
        _ = meshletStride;
        _ = registration;
        handle = AdvancedGpuHandle.Invalid;
        return false;
    }

    public bool TryAddMissing(
        in AdvancedGeometryRegistration registration,
        AdvancedGpuHandle fallbackGeometry,
        out AdvancedGpuHandle handle)
    {
        handle = AdvancedGpuHandle.Invalid;
        if (!AdvancedGeometryCookedLayout.IsSupported(registration.CookedLayoutVersion) ||
            Records.Count >= Records.Capacity)
        {
            return false;
        }

        AdvancedGeometryRecord record = new()
        {
            FallbackGeometry = fallbackGeometry,
            VertexCount = registration.VertexCount,
            IndexCount = registration.IndexCount,
            MeshletFirst = registration.MeshletFirst,
            MeshletCount = registration.MeshletCount,
            VertexLayoutId = registration.VertexLayoutId,
            BoundsSphere = registration.BoundsSphere,
            BoundsMin = registration.BoundsMin,
            BoundsMax = registration.BoundsMax,
            MaterialSectionFirst = registration.MaterialSectionFirst,
            MaterialSectionCount = registration.MaterialSectionCount,
            PrimitiveTopology = registration.PrimitiveTopology,
            Source = EAdvancedGeometrySource.Static,
            Residency = EAdvancedGeometryResidency.Missing,
            MissingBehavior = fallbackGeometry.IsValid
                ? EAdvancedMissingGeometryBehavior.UseFallback
                : EAdvancedMissingGeometryBehavior.SkipDraw,
            CookedLayoutVersion = registration.CookedLayoutVersion,
        };
        return Records.TryAdd(record, out handle);
    }

    public bool TryGet(AdvancedGpuHandle handle, out AdvancedGeometryRecord record)
        => Records.TryGet(handle, out record);

    public bool TryResolveVisibilityGeometry(
        AdvancedGpuHandle handle,
        out AdvancedGeometryRecord record)
    {
        if (!Records.TryGet(handle, out record))
            return false;
        if (record.IsResident)
            return true;
        if (record.MissingBehavior != EAdvancedMissingGeometryBehavior.UseFallback ||
            !record.FallbackGeometry.IsValid ||
            record.FallbackGeometry == handle)
        {
            return false;
        }

        return Records.TryGet(record.FallbackGeometry, out record) && record.IsResident;
    }

    public void GrowAtBoundary(
        uint recordCapacity,
        uint staticVertexCapacityBytes,
        uint indexCapacityBytes,
        uint preSkinnedCurrentCapacityBytes,
        uint preSkinnedPreviousCapacityBytes,
        uint meshletCapacityBytes,
        uint meshletDescriptorCapacityBytes,
        uint meshletVertexIndexCapacityBytes,
        uint meshletTriangleWordCapacityBytes)
    {
        Records.GrowAtBoundary(recordCapacity);
        StaticVertexArena.GrowAtBoundary(staticVertexCapacityBytes);
        IndexArena.GrowAtBoundary(indexCapacityBytes);
        PreSkinnedCurrentArena.GrowAtBoundary(preSkinnedCurrentCapacityBytes);
        PreSkinnedPreviousArena.GrowAtBoundary(preSkinnedPreviousCapacityBytes);
        MeshletDescriptorArena.GrowAtBoundary(meshletDescriptorCapacityBytes == 0u ? meshletCapacityBytes : meshletDescriptorCapacityBytes);
        MeshletVertexIndexArena.GrowAtBoundary(meshletVertexIndexCapacityBytes);
        MeshletTriangleWordArena.GrowAtBoundary(meshletTriangleWordCapacityBytes);
    }

    private static bool ValidateRegistration(
        ReadOnlySpan<byte> vertices,
        ReadOnlySpan<uint> indices,
        in AdvancedGeometryRegistration registration)
        => AdvancedGeometryCookedLayout.IsSupported(registration.CookedLayoutVersion) &&
           registration.VertexCount != 0u &&
           registration.IndexCount != 0u &&
           registration.VertexStride != 0u &&
           (ulong)registration.VertexCount * registration.VertexStride == (uint)vertices.Length &&
           registration.IndexCount == (uint)indices.Length;

    private static AdvancedGeometryRecord CreateResidentRecord(
        in AdvancedGeometryRegistration registration,
        EAdvancedGeometrySource source,
        in AdvancedBufferReference currentVertexData,
        in AdvancedBufferReference previousVertexData,
        in AdvancedBufferReference indexData,
        in AdvancedBufferReference meshletDescriptors,
        in AdvancedBufferReference meshletVertexIndices,
        in AdvancedBufferReference meshletTriangleWords)
        => new()
        {
            CurrentVertexData = currentVertexData,
            PreviousVertexData = previousVertexData,
            IndexData = indexData,
            MeshletDescriptors = meshletDescriptors,
            MeshletVertexIndices = meshletVertexIndices,
            MeshletTriangleWords = meshletTriangleWords,
            VertexBase = currentVertexData.ElementOffset,
            VertexCount = registration.VertexCount,
            IndexBase = indexData.ElementOffset,
            IndexCount = registration.IndexCount,
            MeshletFirst = meshletDescriptors.IsValid ? meshletDescriptors.ElementOffset : registration.MeshletFirst,
            MeshletCount = meshletDescriptors.IsValid ? meshletDescriptors.ElementCount : registration.MeshletCount,
            VertexLayoutId = registration.VertexLayoutId,
            BoundsSphere = registration.BoundsSphere,
            BoundsMin = registration.BoundsMin,
            BoundsMax = registration.BoundsMax,
            MaterialSectionFirst = registration.MaterialSectionFirst,
            MaterialSectionCount = registration.MaterialSectionCount,
            PrimitiveTopology = registration.PrimitiveTopology,
            Source = source,
            Residency = EAdvancedGeometryResidency.Resident,
            MissingBehavior = EAdvancedMissingGeometryBehavior.SkipDraw,
            CookedLayoutVersion = registration.CookedLayoutVersion,
        };

    private static bool ValidateMeshletStreams(
        ReadOnlySpan<AdvancedMeshletDescriptor> descriptors,
        ReadOnlySpan<uint> vertexIndices,
        ReadOnlySpan<uint> triangleWords)
    {
        ulong triangleByteCapacity = (ulong)triangleWords.Length * sizeof(uint);
        foreach (ref readonly AdvancedMeshletDescriptor descriptor in descriptors)
            if ((ulong)descriptor.VertexOffset + descriptor.VertexCount > (uint)vertexIndices.Length ||
                (ulong)descriptor.TriangleByteOffset + (ulong)descriptor.TriangleCount * 3u > triangleByteCapacity)
            {
                return false;
            }

        return true;
    }
}
