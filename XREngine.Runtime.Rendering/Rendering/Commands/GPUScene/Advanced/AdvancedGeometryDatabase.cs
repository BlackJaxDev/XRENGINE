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
    private const uint MeshletBufferIndex = 5u;

    public AdvancedGeometryDatabase(
        uint recordCapacity,
        uint staticVertexCapacityBytes,
        uint indexCapacityBytes,
        uint preSkinnedCurrentCapacityBytes,
        uint preSkinnedPreviousCapacityBytes,
        uint meshletCapacityBytes)
    {
        Records = new AdvancedGpuRecordTable<AdvancedGeometryRecord>(recordCapacity);
        StaticVertexArena = new AdvancedImmutableByteArena(StaticVertexBufferIndex, staticVertexCapacityBytes);
        IndexArena = new AdvancedImmutableByteArena(IndexBufferIndex, indexCapacityBytes);
        PreSkinnedCurrentArena = new AdvancedImmutableByteArena(PreSkinnedCurrentBufferIndex, preSkinnedCurrentCapacityBytes);
        PreSkinnedPreviousArena = new AdvancedImmutableByteArena(PreSkinnedPreviousBufferIndex, preSkinnedPreviousCapacityBytes);
        MeshletArena = new AdvancedImmutableByteArena(MeshletBufferIndex, meshletCapacityBytes);
    }

    public AdvancedGpuRecordTable<AdvancedGeometryRecord> Records { get; }

    public AdvancedImmutableByteArena StaticVertexArena { get; }

    public AdvancedImmutableByteArena IndexArena { get; }

    public AdvancedImmutableByteArena PreSkinnedCurrentArena { get; }

    public AdvancedImmutableByteArena PreSkinnedPreviousArena { get; }

    public AdvancedImmutableByteArena MeshletArena { get; }

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
            AdvancedBufferReference.Invalid);
        return Records.TryAdd(record, out handle);
    }

    public bool TryAddMeshletLocal(
        ReadOnlySpan<byte> vertices,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<byte> meshletData,
        uint meshletStride,
        in AdvancedGeometryRegistration registration,
        out AdvancedGpuHandle handle)
    {
        handle = AdvancedGpuHandle.Invalid;
        if (!ValidateRegistration(vertices, indices, registration) ||
            registration.MeshletCount == 0u ||
            meshletStride == 0u ||
            (ulong)registration.MeshletCount * meshletStride != (uint)meshletData.Length ||
            Records.Count >= Records.Capacity)
        {
            return false;
        }

        ReadOnlySpan<byte> indexBytes = MemoryMarshal.AsBytes(indices);
        if (!StaticVertexArena.CanAppend((uint)vertices.Length, registration.VertexStride) ||
            !IndexArena.CanAppend((uint)indexBytes.Length, sizeof(uint)) ||
            !MeshletArena.CanAppend((uint)meshletData.Length, meshletStride))
        {
            return false;
        }

        StaticVertexArena.TryAppend(vertices, registration.VertexStride, out AdvancedBufferReference vertexData);
        IndexArena.TryAppend(indexBytes, sizeof(uint), out AdvancedBufferReference indexData);
        MeshletArena.TryAppend(meshletData, meshletStride, out AdvancedBufferReference localData);
        AdvancedGeometryRecord record = CreateResidentRecord(
            registration,
            EAdvancedGeometrySource.MeshletLocal,
            vertexData,
            vertexData,
            indexData,
            localData);
        return Records.TryAdd(record, out handle);
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
        uint meshletCapacityBytes)
    {
        Records.GrowAtBoundary(recordCapacity);
        StaticVertexArena.GrowAtBoundary(staticVertexCapacityBytes);
        IndexArena.GrowAtBoundary(indexCapacityBytes);
        PreSkinnedCurrentArena.GrowAtBoundary(preSkinnedCurrentCapacityBytes);
        PreSkinnedPreviousArena.GrowAtBoundary(preSkinnedPreviousCapacityBytes);
        MeshletArena.GrowAtBoundary(meshletCapacityBytes);
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
        in AdvancedBufferReference meshletData)
        => new()
        {
            CurrentVertexData = currentVertexData,
            PreviousVertexData = previousVertexData,
            IndexData = indexData,
            MeshletData = meshletData,
            VertexBase = currentVertexData.ElementOffset,
            VertexCount = registration.VertexCount,
            IndexBase = indexData.ElementOffset,
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
            Source = source,
            Residency = EAdvancedGeometryResidency.Resident,
            MissingBehavior = EAdvancedMissingGeometryBehavior.SkipDraw,
            CookedLayoutVersion = registration.CookedLayoutVersion,
        };
}
