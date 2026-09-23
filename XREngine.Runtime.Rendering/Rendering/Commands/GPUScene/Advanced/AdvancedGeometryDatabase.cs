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

    /// <summary>
    /// Builds a copy-forward replacement for every current geometry row without
    /// changing the live database. Tombstoned rows deliberately prevent staging:
    /// their old stream references remain part of an in-flight publication.
    /// </summary>
    internal bool TryStageCompactionAtBoundary(
        in AdvancedGpuSceneCapacityProfile capacities,
        out AdvancedGeometryCompactionPlan plan)
    {
        plan = null!;
        if (!StaticVertexArena.TryCreateSuccessorAtBoundary(
                capacities.StaticVertexBytes, out AdvancedImmutableByteArena staticVertices) ||
            !IndexArena.TryCreateSuccessorAtBoundary(
                capacities.IndexBytes, out AdvancedImmutableByteArena indices) ||
            !PreSkinnedCurrentArena.TryCreateSuccessorAtBoundary(
                capacities.PreSkinnedCurrentBytes, out AdvancedImmutableByteArena preSkinnedCurrent) ||
            !PreSkinnedPreviousArena.TryCreateSuccessorAtBoundary(
                capacities.PreSkinnedPreviousBytes, out AdvancedImmutableByteArena preSkinnedPrevious) ||
            !MeshletDescriptorArena.TryCreateSuccessorAtBoundary(
                capacities.MeshletDescriptorBytes == 0u
                    ? capacities.MeshletBytes
                    : capacities.MeshletDescriptorBytes,
                out AdvancedImmutableByteArena meshletDescriptors) ||
            !MeshletVertexIndexArena.TryCreateSuccessorAtBoundary(
                capacities.MeshletVertexIndexBytes, out AdvancedImmutableByteArena meshletVertexIndices) ||
            !MeshletTriangleWordArena.TryCreateSuccessorAtBoundary(
                capacities.MeshletTriangleWordBytes, out AdvancedImmutableByteArena meshletTriangleWords))
        {
            return false;
        }

        ReadOnlySpan<AdvancedGeometryRecord> physicalRecords = Records.PhysicalRecords;
        ReadOnlySpan<AdvancedGpuHandle> physicalHandles = Records.PhysicalHandles;
        ReadOnlySpan<byte> occupancy = Records.PhysicalOccupancy;
        int liveCount = checked((int)Records.Count);
        AdvancedGpuHandle[] handles = new AdvancedGpuHandle[liveCount];
        AdvancedGeometryRecord[] records = new AdvancedGeometryRecord[liveCount];
        int writeIndex = 0;
        for (int physicalIndex = 0; physicalIndex < physicalRecords.Length; ++physicalIndex)
        {
            if (occupancy[physicalIndex] == 0)
                continue;

            AdvancedGpuHandle handle = physicalHandles[physicalIndex];
            // Occupied but no longer current means a publication still pins a
            // tombstone. It cannot be omitted or rewritten safely.
            if (!Records.IsCurrent(handle) || writeIndex >= handles.Length ||
                !TryRemapRecord(
                    in physicalRecords[physicalIndex],
                    staticVertices,
                    indices,
                    preSkinnedCurrent,
                    preSkinnedPrevious,
                    meshletDescriptors,
                    meshletVertexIndices,
                    meshletTriangleWords,
                    out AdvancedGeometryRecord remapped))
            {
                return false;
            }

            handles[writeIndex] = handle;
            records[writeIndex] = remapped;
            ++writeIndex;
        }

        if (writeIndex != handles.Length)
            return false;

        ulong oldBytes = (ulong)StaticVertexArena.CountBytes + IndexArena.CountBytes +
            PreSkinnedCurrentArena.CountBytes + PreSkinnedPreviousArena.CountBytes +
            MeshletDescriptorArena.CountBytes + MeshletVertexIndexArena.CountBytes +
            MeshletTriangleWordArena.CountBytes;
        ulong packedBytes = (ulong)staticVertices.CountBytes + indices.CountBytes +
            preSkinnedCurrent.CountBytes + preSkinnedPrevious.CountBytes +
            meshletDescriptors.CountBytes + meshletVertexIndices.CountBytes +
            meshletTriangleWords.CountBytes;
        if (packedBytes >= oldBytes)
            return false;

        plan = new AdvancedGeometryCompactionPlan(
            staticVertices,
            indices,
            preSkinnedCurrent,
            preSkinnedPrevious,
            meshletDescriptors,
            meshletVertexIndices,
            meshletTriangleWords,
            handles,
            records)
        {
            ReclaimedBytes = oldBytes - packedBytes,
        };
        return true;
    }

    /// <summary>
    /// Calculates successor stream extents without allocating or copying. This
    /// lets the publisher skip cold-growth compaction when no dead bytes can
    /// shrink the capacity required by the next append transaction.
    /// </summary>
    internal bool TryEstimateCompactionAtBoundary(
        out AdvancedGeometryCompactionEstimate estimate)
    {
        estimate = default;
        uint staticVertices = 0u, indices = 0u, preSkinnedCurrent = 0u,
            preSkinnedPrevious = 0u, meshletDescriptors = 0u,
            meshletVertexIndices = 0u, meshletTriangleWords = 0u;
        int liveCount = 0;
        ReadOnlySpan<AdvancedGeometryRecord> records = Records.PhysicalRecords;
        ReadOnlySpan<AdvancedGpuHandle> handles = Records.PhysicalHandles;
        ReadOnlySpan<byte> occupancy = Records.PhysicalOccupancy;
        for (int index = 0; index < records.Length; ++index)
        {
            if (occupancy[index] == 0)
                continue;
            if (!Records.IsCurrent(handles[index]) ||
                !TryEstimateRecord(in records[index], ref staticVertices, ref indices,
                    ref preSkinnedCurrent, ref preSkinnedPrevious, ref meshletDescriptors,
                    ref meshletVertexIndices, ref meshletTriangleWords))
            {
                return false;
            }
            ++liveCount;
        }

        if (liveCount != Records.Count)
            return false;

        ulong oldBytes = (ulong)StaticVertexArena.CountBytes + IndexArena.CountBytes +
            PreSkinnedCurrentArena.CountBytes + PreSkinnedPreviousArena.CountBytes +
            MeshletDescriptorArena.CountBytes + MeshletVertexIndexArena.CountBytes +
            MeshletTriangleWordArena.CountBytes;
        ulong packedBytes = (ulong)staticVertices + indices + preSkinnedCurrent +
            preSkinnedPrevious + meshletDescriptors + meshletVertexIndices +
            meshletTriangleWords;
        if (packedBytes >= oldBytes)
            return false;

        estimate = new AdvancedGeometryCompactionEstimate(staticVertices, indices,
            preSkinnedCurrent, preSkinnedPrevious, meshletDescriptors,
            meshletVertexIndices, meshletTriangleWords, liveCount, oldBytes - packedBytes);
        return true;
    }

    /// <summary>
    /// Prevalidates every arena and row before adopting pre-copied generations
    /// and journaling live geometry-row binding rewrites. Call only after the
    /// publication has selected its sequence and preflighted journal capacity.
    /// </summary>
    internal bool TryApplyCompactionAtBoundary(AdvancedGeometryCompactionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Handles.Length != plan.Records.Length ||
            !Records.CanApply(0, plan.ReplacementCount, 0) ||
            !StaticVertexArena.CanAdoptSuccessorAtBoundary(plan.StaticVertices) ||
            !IndexArena.CanAdoptSuccessorAtBoundary(plan.Indices) ||
            !PreSkinnedCurrentArena.CanAdoptSuccessorAtBoundary(plan.PreSkinnedCurrent) ||
            !PreSkinnedPreviousArena.CanAdoptSuccessorAtBoundary(plan.PreSkinnedPrevious) ||
            !MeshletDescriptorArena.CanAdoptSuccessorAtBoundary(plan.MeshletDescriptors) ||
            !MeshletVertexIndexArena.CanAdoptSuccessorAtBoundary(plan.MeshletVertexIndices) ||
            !MeshletTriangleWordArena.CanAdoptSuccessorAtBoundary(plan.MeshletTriangleWords))
        {
            return false;
        }

        for (int index = 0; index < plan.ReplacementCount; ++index)
            if (!Records.IsCurrent(plan.Handles[index]))
                return false;

        // No publication mutation above this line. The successor and row
        // operations below are infallible under the publication owner lock.
        StaticVertexArena.TryAdoptSuccessorAtBoundary(plan.StaticVertices);
        IndexArena.TryAdoptSuccessorAtBoundary(plan.Indices);
        PreSkinnedCurrentArena.TryAdoptSuccessorAtBoundary(plan.PreSkinnedCurrent);
        PreSkinnedPreviousArena.TryAdoptSuccessorAtBoundary(plan.PreSkinnedPrevious);
        MeshletDescriptorArena.TryAdoptSuccessorAtBoundary(plan.MeshletDescriptors);
        MeshletVertexIndexArena.TryAdoptSuccessorAtBoundary(plan.MeshletVertexIndices);
        MeshletTriangleWordArena.TryAdoptSuccessorAtBoundary(plan.MeshletTriangleWords);

        for (int index = 0; index < plan.ReplacementCount; ++index)
            if (!Records.TryReplace(
                    plan.Handles[index],
                    plan.Records[index],
                    EAdvancedGpuMutationDomain.ResourceBinding))
            {
                throw new InvalidOperationException(
                    "A prevalidated geometry binding rewrite failed during compaction.");
            }

        return true;
    }

    private bool TryEstimateRecord(
        in AdvancedGeometryRecord source,
        ref uint staticVertices, ref uint indices, ref uint preSkinnedCurrent,
        ref uint preSkinnedPrevious, ref uint meshletDescriptors,
        ref uint meshletVertexIndices, ref uint meshletTriangleWords)
        => TryEstimateCurrentVertex(source.CurrentVertexData, ref staticVertices, ref preSkinnedCurrent) &&
           TryEstimatePreviousVertex(source.CurrentVertexData, source.PreviousVertexData,
               ref preSkinnedCurrent, ref preSkinnedPrevious) &&
           TryEstimateReference(IndexArena, source.IndexData, ref indices) &&
           TryEstimateReference(MeshletDescriptorArena, source.MeshletDescriptors, ref meshletDescriptors) &&
           TryEstimateReference(MeshletVertexIndexArena, source.MeshletVertexIndices, ref meshletVertexIndices) &&
           TryEstimateReference(MeshletTriangleWordArena, source.MeshletTriangleWords, ref meshletTriangleWords);

    private bool TryEstimateCurrentVertex(
        in AdvancedBufferReference source,
        ref uint staticVertices,
        ref uint preSkinnedCurrent)
    {
        if (!source.IsValid)
            return true;
        if (source.Buffer == StaticVertexArena.BufferHandle)
            return TryEstimateReference(StaticVertexArena, source, ref staticVertices);
        return source.Buffer == PreSkinnedCurrentArena.BufferHandle &&
            TryEstimateReference(PreSkinnedCurrentArena, source, ref preSkinnedCurrent);
    }

    private bool TryEstimatePreviousVertex(
        in AdvancedBufferReference current,
        in AdvancedBufferReference previous,
        ref uint preSkinnedCurrent,
        ref uint preSkinnedPrevious)
    {
        if (!previous.IsValid || previous == current)
            return true;
        if (previous.Buffer == PreSkinnedCurrentArena.BufferHandle)
            return TryEstimateReference(PreSkinnedCurrentArena, previous, ref preSkinnedCurrent);
        return previous.Buffer == PreSkinnedPreviousArena.BufferHandle &&
            TryEstimateReference(PreSkinnedPreviousArena, previous, ref preSkinnedPrevious);
    }

    private static bool TryEstimateReference(
        AdvancedImmutableByteArena arena,
        in AdvancedBufferReference source,
        ref uint end)
    {
        if (!source.IsValid)
            return true;
        if (!arena.IsCurrentReference(source))
            return false;
        uint remainder = end % source.ElementStride;
        uint aligned = remainder == 0u ? end : checked(end + source.ElementStride - remainder);
        end = checked(aligned + checked((uint)source.ByteLength));
        return true;
    }

    private bool TryRemapRecord(
        in AdvancedGeometryRecord source,
        AdvancedImmutableByteArena staticVertices,
        AdvancedImmutableByteArena indices,
        AdvancedImmutableByteArena preSkinnedCurrent,
        AdvancedImmutableByteArena preSkinnedPrevious,
        AdvancedImmutableByteArena meshletDescriptors,
        AdvancedImmutableByteArena meshletVertexIndices,
        AdvancedImmutableByteArena meshletTriangleWords,
        out AdvancedGeometryRecord remapped)
    {
        remapped = source;
        if (!TryRemapVertexCurrent(
                source.CurrentVertexData,
                staticVertices,
                preSkinnedCurrent,
                out remapped.CurrentVertexData) ||
            !TryRemapVertexPrevious(
                source.CurrentVertexData,
                remapped.CurrentVertexData,
                source.PreviousVertexData,
                preSkinnedCurrent,
                preSkinnedPrevious,
                out remapped.PreviousVertexData) ||
            !TryRemapReference(IndexArena, indices, source.IndexData, out remapped.IndexData) ||
            !TryRemapReference(
                MeshletDescriptorArena,
                meshletDescriptors,
                source.MeshletDescriptors,
                out remapped.MeshletDescriptors) ||
            !TryRemapReference(
                MeshletVertexIndexArena,
                meshletVertexIndices,
                source.MeshletVertexIndices,
                out remapped.MeshletVertexIndices) ||
            !TryRemapReference(
                MeshletTriangleWordArena,
                meshletTriangleWords,
                source.MeshletTriangleWords,
                out remapped.MeshletTriangleWords))
        {
            return false;
        }

        remapped.VertexBase = remapped.CurrentVertexData.IsValid
            ? remapped.CurrentVertexData.ElementOffset
            : 0u;
        remapped.IndexBase = remapped.IndexData.IsValid
            ? remapped.IndexData.ElementOffset
            : 0u;
        remapped.MeshletFirst = remapped.MeshletDescriptors.IsValid
            ? remapped.MeshletDescriptors.ElementOffset
            : source.MeshletFirst;
        return true;
    }

    private bool TryRemapVertexPrevious(
        in AdvancedBufferReference sourceCurrent,
        in AdvancedBufferReference remappedCurrent,
        in AdvancedBufferReference sourcePrevious,
        AdvancedImmutableByteArena preSkinnedCurrent,
        AdvancedImmutableByteArena preSkinnedPrevious,
        out AdvancedBufferReference remappedPrevious)
    {
        remappedPrevious = AdvancedBufferReference.Invalid;
        if (!sourcePrevious.IsValid)
            return true;
        if (sourcePrevious == sourceCurrent)
        {
            remappedPrevious = remappedCurrent;
            return true;
        }

        if (sourcePrevious.Buffer == PreSkinnedCurrentArena.BufferHandle)
            return PreSkinnedCurrentArena.TryCopyReferenceTo(
                preSkinnedCurrent, sourcePrevious, out remappedPrevious);
        if (sourcePrevious.Buffer == PreSkinnedPreviousArena.BufferHandle)
            return PreSkinnedPreviousArena.TryCopyReferenceTo(
                preSkinnedPrevious, sourcePrevious, out remappedPrevious);
        return false;
    }

    private bool TryRemapVertexCurrent(
        in AdvancedBufferReference sourceCurrent,
        AdvancedImmutableByteArena staticVertices,
        AdvancedImmutableByteArena preSkinnedCurrent,
        out AdvancedBufferReference remappedCurrent)
    {
        remappedCurrent = AdvancedBufferReference.Invalid;
        if (!sourceCurrent.IsValid)
            return true;
        if (sourceCurrent.Buffer == StaticVertexArena.BufferHandle)
            return StaticVertexArena.TryCopyReferenceTo(
                staticVertices, sourceCurrent, out remappedCurrent);
        if (sourceCurrent.Buffer == PreSkinnedCurrentArena.BufferHandle)
            return PreSkinnedCurrentArena.TryCopyReferenceTo(
                preSkinnedCurrent, sourceCurrent, out remappedCurrent);
        return false;
    }

    private static bool TryRemapReference(
        AdvancedImmutableByteArena sourceArena,
        AdvancedImmutableByteArena successorArena,
        in AdvancedBufferReference source,
        out AdvancedBufferReference remapped)
    {
        remapped = AdvancedBufferReference.Invalid;
        return !source.IsValid || sourceArena.TryCopyReferenceTo(successorArena, source, out remapped);
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
