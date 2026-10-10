using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>Builds fixed indexed-instance segments from exact retained source data.</summary>
internal sealed class AdvancedIndexedInstanceGroupPlanner
{
    private readonly AdvancedIndexedInstanceGroup[] _groups;
    private readonly int[] _representatives;
    private readonly int[] _groupContentClasses;
    private readonly int[] _groupByPayload;
    private readonly int[] _lookupGroups;
    private readonly ulong[] _lookupHashes;
    private readonly uint[] _lookupEpochs;
    private readonly uint[] _rangeCursors;
    private readonly int _lookupMask;
    private readonly Dictionary<AdvancedIndexedInstanceContentKey, (ulong Hash, int ClassId)> _contentCache;
    private ulong _contentDatabaseEpoch;
    private AdvancedGpuOwnerGenerations _contentGeometryGeneration;
    private AdvancedMaterialDatabaseGenerations _contentMaterialGeneration;
    private AdvancedGpuHandle _contentVertexBuffer;
    private AdvancedGpuHandle _contentIndexBuffer;
    private int _nextContentClass;
    private uint _lookupEpoch;
    private int _groupCount;

    internal AdvancedIndexedInstanceGroupPlanner(int maximumPayloads, int maximumRanges)
    {
        _groups = new AdvancedIndexedInstanceGroup[maximumPayloads];
        _representatives = new int[maximumPayloads];
        _groupContentClasses = new int[maximumPayloads];
        _contentCache = new Dictionary<AdvancedIndexedInstanceContentKey, (ulong, int)>(maximumPayloads);
        _groupByPayload = new int[maximumPayloads];
        _rangeCursors = new uint[maximumRanges];
        int capacity = 1;
        while (capacity < checked(maximumPayloads * 2))
            capacity = checked(capacity * 2);
        _lookupGroups = new int[capacity];
        _lookupHashes = new ulong[capacity];
        _lookupEpochs = new uint[capacity];
        _lookupMask = capacity - 1;
    }

    internal ReadOnlySpan<AdvancedIndexedInstanceGroup> Groups
        => _groups.AsSpan(0, _groupCount);

    internal void Build(
        Span<AdvancedVisibilityPayload> payloads,
        ReadOnlySpan<EAdvancedGeometryProducer> producers,
        ReadOnlySpan<int> rangeIndices,
        ReadOnlySpan<AdvancedIndirectRange> ranges,
        AdvancedGpuScenePublicationSnapshot publication)
    {
        if (payloads.Length > _groups.Length || producers.Length != payloads.Length ||
            rangeIndices.Length != payloads.Length || ranges.Length > _rangeCursors.Length)
            throw new InvalidOperationException("Indexed instance group capacity does not match the visibility publication.");

        if (_contentDatabaseEpoch != publication.DatabaseEpoch ||
            _contentGeometryGeneration != publication.Geometry.Generations ||
            _contentMaterialGeneration != publication.MaterialPayloads.Generations ||
            _contentVertexBuffer != publication.GeometryPayloads.StaticVertices.BufferHandle ||
            _contentIndexBuffer != publication.GeometryPayloads.Indices.BufferHandle)
        {
            _contentCache.Clear();
            _nextContentClass = 0;
            _contentDatabaseEpoch = publication.DatabaseEpoch;
            _contentGeometryGeneration = publication.Geometry.Generations;
            _contentMaterialGeneration = publication.MaterialPayloads.Generations;
            _contentVertexBuffer = publication.GeometryPayloads.StaticVertices.BufferHandle;
            _contentIndexBuffer = publication.GeometryPayloads.Indices.BufferHandle;
        }

        _lookupEpoch = unchecked(_lookupEpoch + 1u);
        if (_lookupEpoch == 0u)
        {
            Array.Clear(_lookupEpochs);
            _lookupEpoch = 1u;
        }
        _groupCount = 0;
        for (int payloadIndex = 0; payloadIndex < payloads.Length; ++payloadIndex)
        {
            payloads[payloadIndex] = payloads[payloadIndex] with
            {
                InstanceGroupIndexPlusOne = 0u,
            };
            if (producers[payloadIndex] != EAdvancedGeometryProducer.IndirectIndexed)
                continue;

            AdvancedVisibilityPayload payload = payloads[payloadIndex];
            int rangeIndex = rangeIndices[payloadIndex];
            if ((uint)rangeIndex >= (uint)ranges.Length)
                throw new InvalidOperationException("An indexed payload has no sealed visibility range.");

            AdvancedIndexedInstanceContentKey contentKey = new(
                payload.Geometry, payload.Material, payload.PrimitiveSection,
                payload.IndexCount, payload.VertexCount);
            bool hasCachedContent = _contentCache.TryGetValue(contentKey,
                out (ulong Hash, int ClassId) content);
            ulong hash = hasCachedContent ? content.Hash : 0UL;
            bool canShare = payload.InstanceCount == 1u &&
                (hasCachedContent || TryHashGeometry(in payload, publication, out hash));
            int lookupSlot = canShare ? (int)hash & _lookupMask : -1;
            int groupIndex = -1;
            if (canShare)
            {
                while (_lookupEpochs[lookupSlot] == _lookupEpoch)
                {
                    int candidate = _lookupGroups[lookupSlot];
                    int representative = _representatives[candidate];
                    if (_lookupHashes[lookupSlot] == hash &&
                        _groups[candidate].RangeIndex == (uint)rangeIndex &&
                        (hasCachedContent && content.ClassId == _groupContentClasses[candidate] ||
                         AreEquivalent(in payload, in payloads[representative], publication)))
                    {
                        groupIndex = candidate;
                        break;
                    }
                    lookupSlot = (lookupSlot + 1) & _lookupMask;
                }
            }

            if (groupIndex < 0)
            {
                groupIndex = _groupCount++;
                _representatives[groupIndex] = payloadIndex;
                if (canShare)
                    _groupContentClasses[groupIndex] = hasCachedContent
                        ? content.ClassId
                        : checked(++_nextContentClass);
                _groups[groupIndex] = new((uint)rangeIndex, 0u, 1u, (uint)payloadIndex);
                if (canShare)
                {
                    _lookupEpochs[lookupSlot] = _lookupEpoch;
                    _lookupHashes[lookupSlot] = hash;
                    _lookupGroups[lookupSlot] = groupIndex;
                }
            }
            else
            {
                AdvancedIndexedInstanceGroup group = _groups[groupIndex];
                _groups[groupIndex] = group with
                {
                    MemberCapacity = checked(group.MemberCapacity + 1u),
                };
            }
            _groupByPayload[payloadIndex] = groupIndex;
            if (canShare && (!hasCachedContent ||
                content.ClassId != _groupContentClasses[groupIndex]) &&
                (hasCachedContent || _contentCache.Count < _groups.Length))
                _contentCache[contentKey] = (hash, _groupContentClasses[groupIndex]);
        }

        for (int rangeIndex = 0; rangeIndex < ranges.Length; ++rangeIndex)
            _rangeCursors[rangeIndex] = ranges[rangeIndex].FirstPayloadIndex;
        for (int groupIndex = 0; groupIndex < _groupCount; ++groupIndex)
        {
            AdvancedIndexedInstanceGroup group = _groups[groupIndex];
            int rangeIndex = checked((int)group.RangeIndex);
            uint first = _rangeCursors[rangeIndex];
            uint end = checked(first + group.MemberCapacity);
            AdvancedIndirectRange range = ranges[rangeIndex];
            if (end > checked(range.FirstPayloadIndex + range.PayloadCapacity))
                throw new InvalidOperationException("An indexed instance group exceeds its sealed range.");
            _groups[groupIndex] = group with { FirstMember = first };
            _rangeCursors[rangeIndex] = end;
        }
        for (int payloadIndex = 0; payloadIndex < payloads.Length; ++payloadIndex)
        {
            if (producers[payloadIndex] == EAdvancedGeometryProducer.IndirectIndexed)
                payloads[payloadIndex] = payloads[payloadIndex] with
                {
                    InstanceGroupIndexPlusOne = checked((uint)_groupByPayload[payloadIndex] + 1u),
                };
        }
    }

    private static bool TryHashGeometry(in AdvancedVisibilityPayload payload,
        AdvancedGpuScenePublicationSnapshot publication, out ulong hash)
    {
        hash = 14695981039346656037UL;
        if (!publication.Geometry.TryGet(payload.Geometry, out AdvancedGeometryRecord geometry) ||
            geometry.Source != EAdvancedGeometrySource.Static ||
            !TrySlice(publication.GeometryPayloads.Indices.Data,
                publication.GeometryPayloads.Indices.BufferHandle,
                geometry.IndexData, out ReadOnlySpan<byte> indices) ||
            !TrySlice(publication.GeometryPayloads.StaticVertices.Data,
                publication.GeometryPayloads.StaticVertices.BufferHandle,
                geometry.CurrentVertexData,
                out ReadOnlySpan<byte> vertices))
            return false;

        Hash(ref hash, (uint)geometry.PrimitiveTopology);
        Hash(ref hash, geometry.VertexLayoutId);
        Hash(ref hash, payload.PrimitiveSection);
        Hash(ref hash, payload.IndexCount);
        Hash(ref hash, payload.VertexCount);
        Hash(ref hash, geometry.IndexData.ElementStride);
        Hash(ref hash, geometry.CurrentVertexData.ElementStride);
        Hash(ref hash, indices);
        Hash(ref hash, vertices);
        return TryHashMaterial(payload.Material, publication.MaterialPayloads, ref hash);
    }

    private static bool TryHashMaterial(AdvancedGpuHandle handle,
        AdvancedMaterialPublicationSnapshot materials, ref ulong hash)
    {
        if (!materials.Materials.TryGet(handle, out AdvancedMaterialRecord material) ||
            !materials.TryGetConstantWords(material, out ReadOnlySpan<uint> constants) ||
            !materials.TryGetTextureBindings(material,
                out ReadOnlySpan<AdvancedMaterialTextureBinding> bindings) ||
            !materials.TryGetLayoutHandle(handle, out AdvancedGpuHandle layoutHandle) ||
            !materials.Layouts.TryGet(layoutHandle, out AdvancedMaterialLayoutRecord layout) ||
            !materials.TryGetLayoutMembers(layout,
                out ReadOnlySpan<AdvancedMaterialLayoutMember> members))
            return false;

        Hash(ref hash, material.ShadingKernelId);
        Hash(ref hash, material.ShadingKernelGeneration);
        Hash(ref hash, material.MaterialLayoutHash);
        Hash(ref hash, (uint)material.RenderStateClass);
        Hash(ref hash, (uint)material.CoverageMode);
        Hash(ref hash, (uint)material.RequiredAttributeMask);
        Hash(ref hash, (uint)material.FeatureFlags);
        Hash(ref hash, (uint)material.EligibilityFlags);
        Hash(ref hash, (uint)material.SourceContract);
        Hash(ref hash, layout.LayoutHash);
        Hash(ref hash, layout.ConstantWordCount);
        Hash(ref hash, layout.TextureReferenceCount);
        Hash(ref hash, (uint)layout.RequiredAttributeMask);
        Hash(ref hash, layout.Flags);
        foreach (uint constant in constants)
            Hash(ref hash, constant);
        foreach (AdvancedMaterialTextureBinding binding in bindings)
            Hash(ref hash, unchecked((uint)binding.GetHashCode()));
        foreach (AdvancedMaterialLayoutMember member in members)
            Hash(ref hash, unchecked((uint)member.GetHashCode()));
        return true;
    }

    private static bool AreEquivalent(in AdvancedVisibilityPayload first,
        in AdvancedVisibilityPayload second,
        AdvancedGpuScenePublicationSnapshot publication)
    {
        if (first.InstanceCount != 1u || second.InstanceCount != 1u ||
            first.PrimitiveSection != second.PrimitiveSection ||
            first.IndexCount != second.IndexCount ||
            first.VertexCount != second.VertexCount ||
            first.PrimitiveTopology != second.PrimitiveTopology ||
            !publication.Geometry.TryGet(first.Geometry, out AdvancedGeometryRecord firstGeometry) ||
            !publication.Geometry.TryGet(second.Geometry, out AdvancedGeometryRecord secondGeometry) ||
            firstGeometry.Source != secondGeometry.Source ||
            firstGeometry.VertexLayoutId != secondGeometry.VertexLayoutId ||
            firstGeometry.IndexData.ElementStride != secondGeometry.IndexData.ElementStride ||
            firstGeometry.CurrentVertexData.ElementStride != secondGeometry.CurrentVertexData.ElementStride ||
            !TrySlice(publication.GeometryPayloads.Indices.Data,
                publication.GeometryPayloads.Indices.BufferHandle,
                firstGeometry.IndexData, out ReadOnlySpan<byte> firstIndices) ||
            !TrySlice(publication.GeometryPayloads.Indices.Data,
                publication.GeometryPayloads.Indices.BufferHandle,
                secondGeometry.IndexData, out ReadOnlySpan<byte> secondIndices) ||
            !firstIndices.SequenceEqual(secondIndices) ||
            !TrySlice(publication.GeometryPayloads.StaticVertices.Data,
                publication.GeometryPayloads.StaticVertices.BufferHandle,
                firstGeometry.CurrentVertexData,
                out ReadOnlySpan<byte> firstVertices) ||
            !TrySlice(publication.GeometryPayloads.StaticVertices.Data,
                publication.GeometryPayloads.StaticVertices.BufferHandle,
                secondGeometry.CurrentVertexData,
                out ReadOnlySpan<byte> secondVertices) ||
            !firstVertices.SequenceEqual(secondVertices))
            return false;

        return AreMaterialsEquivalent(first.Material, second.Material, publication.MaterialPayloads);
    }

    private static bool AreMaterialsEquivalent(AdvancedGpuHandle firstHandle,
        AdvancedGpuHandle secondHandle, AdvancedMaterialPublicationSnapshot materials)
    {
        if (firstHandle == secondHandle)
            return true;
        if (!materials.Materials.TryGet(firstHandle, out AdvancedMaterialRecord first) ||
            !materials.Materials.TryGet(secondHandle, out AdvancedMaterialRecord second) ||
            first.ShadingKernelId != second.ShadingKernelId ||
            first.ShadingKernelGeneration != second.ShadingKernelGeneration ||
            first.MaterialLayoutHash != second.MaterialLayoutHash ||
            first.RenderStateClass != second.RenderStateClass ||
            first.CoverageMode != second.CoverageMode ||
            first.RequiredAttributeMask != second.RequiredAttributeMask ||
            first.FeatureFlags != second.FeatureFlags ||
            first.EligibilityFlags != second.EligibilityFlags ||
            first.SourceContract != second.SourceContract ||
            !materials.TryGetConstantWords(first, out ReadOnlySpan<uint> firstConstants) ||
            !materials.TryGetConstantWords(second, out ReadOnlySpan<uint> secondConstants) ||
            !firstConstants.SequenceEqual(secondConstants) ||
            !materials.TryGetTextureBindings(first, out ReadOnlySpan<AdvancedMaterialTextureBinding> firstBindings) ||
            !materials.TryGetTextureBindings(second, out ReadOnlySpan<AdvancedMaterialTextureBinding> secondBindings) ||
            !firstBindings.SequenceEqual(secondBindings) ||
            !materials.TryGetLayoutHandle(firstHandle, out AdvancedGpuHandle firstLayoutHandle) ||
            !materials.TryGetLayoutHandle(secondHandle, out AdvancedGpuHandle secondLayoutHandle) ||
            !materials.Layouts.TryGet(firstLayoutHandle, out AdvancedMaterialLayoutRecord firstLayout) ||
            !materials.Layouts.TryGet(secondLayoutHandle, out AdvancedMaterialLayoutRecord secondLayout) ||
            firstLayout.LayoutHash != secondLayout.LayoutHash ||
            firstLayout.ConstantWordCount != secondLayout.ConstantWordCount ||
            firstLayout.TextureReferenceCount != secondLayout.TextureReferenceCount ||
            firstLayout.RequiredAttributeMask != secondLayout.RequiredAttributeMask ||
            firstLayout.Flags != secondLayout.Flags ||
            !materials.TryGetLayoutMembers(firstLayout, out ReadOnlySpan<AdvancedMaterialLayoutMember> firstMembers) ||
            !materials.TryGetLayoutMembers(secondLayout, out ReadOnlySpan<AdvancedMaterialLayoutMember> secondMembers))
            return false;
        return firstMembers.SequenceEqual(secondMembers);
    }

    private static bool TrySlice(ReadOnlySpan<byte> arena, AdvancedGpuHandle buffer,
        in AdvancedBufferReference reference,
        out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (!reference.IsValid || reference.Buffer != buffer ||
            reference.ByteOffset > (ulong)arena.Length ||
            reference.ByteLength > (ulong)arena.Length - reference.ByteOffset)
            return false;
        bytes = arena.Slice(checked((int)reference.ByteOffset), checked((int)reference.ByteLength));
        return true;
    }

    private static void Hash(ref ulong hash, uint value)
    {
        hash = (hash ^ value) * 1099511628211UL;
    }

    private static void Hash(ref ulong hash, ulong value)
    {
        Hash(ref hash, (uint)value);
        Hash(ref hash, (uint)(value >> 32));
    }

    private static void Hash(ref ulong hash, ReadOnlySpan<byte> bytes)
    {
        for (int index = 0; index < bytes.Length; ++index)
            hash = (hash ^ bytes[index]) * 1099511628211UL;
    }
}
