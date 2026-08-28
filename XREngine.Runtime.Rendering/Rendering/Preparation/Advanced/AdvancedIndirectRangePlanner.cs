using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Allocation-free visibility-range classifier. A command missing meshlets
/// falls back independently, so skinned commands never reject meshlet
/// submission for the rest of the scene.
/// </summary>
public sealed class AdvancedIndirectRangePlanner
{
    private readonly AdvancedIndirectRange[] _ranges;
    private readonly int[] _payloadIndices;
    private readonly EAdvancedGeometryProducer[] _producers;
    private readonly EAdvancedGeometryProducer[] _producersByPayload;
    private readonly uint[] _writeCursors;
    private int _rangeCount;
    private int _payloadCount;
    private ulong _structuralSignature;
    private ulong _structuralGeneration;

    public AdvancedIndirectRangePlanner(
        int maximumPayloads,
        int maximumRanges)
    {
        if (maximumPayloads <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloads));
        if (maximumRanges <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRanges));

        _ranges = new AdvancedIndirectRange[maximumRanges];
        _payloadIndices = new int[maximumPayloads];
        _producers = new EAdvancedGeometryProducer[maximumPayloads];
        _producersByPayload =
            new EAdvancedGeometryProducer[maximumPayloads];
        _writeCursors = new uint[maximumRanges];
    }

    public ReadOnlySpan<AdvancedIndirectRange> Ranges
        => _ranges.AsSpan(0, _rangeCount);
    public ReadOnlySpan<int> PayloadIndices
        => _payloadIndices.AsSpan(0, _payloadCount);
    public ReadOnlySpan<EAdvancedGeometryProducer> Producers
        => _producers.AsSpan(0, _payloadCount);
    public ReadOnlySpan<EAdvancedGeometryProducer> ProducersByPayload
        => _producersByPayload.AsSpan(0, _payloadCount);

    public AdvancedIndirectPreparationResult Build(
        ReadOnlySpan<AdvancedVisibilityPayload> payloads,
        uint argumentBufferBase,
        uint countBufferBase,
        uint argumentStride,
        uint countStride,
        EMeshSubmissionStrategy submissionStrategy =
            EMeshSubmissionStrategy.GpuMeshletZeroReadback)
    {
        if (payloads.Length > _payloadIndices.Length)
            throw new ArgumentOutOfRangeException(nameof(payloads));
        if (argumentStride == 0u || countStride == 0u)
            throw new ArgumentOutOfRangeException(nameof(argumentStride));

        _rangeCount = 0;
        _payloadCount = 0;
        uint staticMeshlet = 0u;
        uint skinnedMeshlet = 0u;
        uint indirectIndexed = 0u;
        uint cpuDirectStatic = 0u;
        uint cpuDirectPreSkinned = 0u;

        for (int payloadIndex = 0;
             payloadIndex < payloads.Length;
             payloadIndex++)
        {
            AdvancedVisibilityPayload payload = payloads[payloadIndex];
            EAdvancedGeometryProducer producer =
                ResolveProducer(payload, submissionStrategy);
            _producersByPayload[payloadIndex] = producer;
            switch (producer)
            {
                case EAdvancedGeometryProducer.StaticMeshlet:
                    staticMeshlet++;
                    break;
                case EAdvancedGeometryProducer.SkinnedMeshlet:
                    skinnedMeshlet++;
                    break;
                case EAdvancedGeometryProducer.IndirectIndexed:
                    indirectIndexed++;
                    break;
                case EAdvancedGeometryProducer.CpuDirectStaticIndexed:
                    cpuDirectStatic++;
                    break;
                case EAdvancedGeometryProducer.CpuDirectPreSkinned:
                    cpuDirectPreSkinned++;
                    break;
            }

            AdvancedIndirectRangeKey key = new(
                payload.Geometry,
                payload.RasterStateClass,
                payload.Coverage,
                payload.CullMode,
                payload.PrimitiveTopology,
                producer);
            int rangeIndex = FindRange(key);
            if (rangeIndex < 0)
            {
                if (_rangeCount >= _ranges.Length)
                {
                    throw new InvalidOperationException(
                        "Visibility indirect range capacity was exhausted.");
                }

                rangeIndex = _rangeCount++;
                _ranges[rangeIndex] = new AdvancedIndirectRange(
                    key,
                    FirstPayloadIndex: 0u,
                    PayloadCapacity: 0u,
                    ArgumentBufferOffset: 0u,
                    CountBufferOffset: checked(
                        countBufferBase +
                        (uint)rangeIndex * countStride),
                    CountWrittenByGpu: true);
            }

            AdvancedIndirectRange range = _ranges[rangeIndex];
            _ranges[rangeIndex] = range with
            {
                PayloadCapacity = checked(range.PayloadCapacity + 1u),
            };
        }

        uint prefix = 0u;
        for (int rangeIndex = 0; rangeIndex < _rangeCount; rangeIndex++)
        {
            AdvancedIndirectRange range = _ranges[rangeIndex];
            _ranges[rangeIndex] = range with
            {
                FirstPayloadIndex = prefix,
                ArgumentBufferOffset = checked(
                    argumentBufferBase + prefix * argumentStride),
            };
            _writeCursors[rangeIndex] = prefix;
            prefix = checked(prefix + range.PayloadCapacity);
        }

        for (int payloadIndex = 0;
             payloadIndex < payloads.Length;
             payloadIndex++)
        {
            EAdvancedGeometryProducer producer = _producersByPayload[payloadIndex];
            int rangeIndex = FindRange(new AdvancedIndirectRangeKey(
                payloads[payloadIndex].Geometry,
                payloads[payloadIndex].RasterStateClass,
                payloads[payloadIndex].Coverage,
                payloads[payloadIndex].CullMode,
                payloads[payloadIndex].PrimitiveTopology,
                producer));
            uint destination = _writeCursors[rangeIndex]++;
            _payloadIndices[destination] = payloadIndex;
            _producers[destination] = producer;
        }

        _payloadCount = payloads.Length;
        ulong signature = ComputeStructuralSignature();
        bool requiresRerecord = signature != _structuralSignature;
        if (requiresRerecord)
        {
            _structuralSignature = signature;
            _structuralGeneration++;
        }

        return new AdvancedIndirectPreparationResult(
            PayloadCount: checked((uint)payloads.Length),
            RangeCount: checked((uint)_rangeCount),
            StaticMeshletCount: staticMeshlet,
            SkinnedMeshletCount: skinnedMeshlet,
            IndirectIndexedCount: indirectIndexed,
            CpuDirectStaticIndexedCount: cpuDirectStatic,
            CpuDirectPreSkinnedCount: cpuDirectPreSkinned,
            StructuralGeneration: _structuralGeneration,
            RequiresPrimaryRerecord: requiresRerecord,
            RequiresCpuCount: false);
    }

    public static EAdvancedGeometryProducer ResolveProducer(
        in AdvancedVisibilityPayload payload)
        => ResolveProducer(
            payload,
            EMeshSubmissionStrategy.GpuMeshletZeroReadback);

    public static EAdvancedGeometryProducer ResolveProducer(
        in AdvancedVisibilityPayload payload,
        EMeshSubmissionStrategy submissionStrategy)
        => AdvancedVisibilityProducerResolver.Resolve(
            submissionStrategy,
            payload);

    private int FindRange(in AdvancedIndirectRangeKey key)
    {
        for (int i = 0; i < _rangeCount; i++)
            if (_ranges[i].Key == key)
                return i;
        return -1;
    }

    private ulong ComputeStructuralSignature()
    {
        ulong hash = 14695981039346656037UL;
        HashValue(ref hash, checked((uint)_rangeCount));
        for (int i = 0; i < _rangeCount; i++)
        {
            AdvancedIndirectRange range = _ranges[i];
            HashValue(ref hash, range.Key.Geometry.Index);
            HashValue(ref hash, range.Key.Geometry.Generation);
            HashValue(ref hash, range.Key.RasterStateClass);
            HashValue(ref hash, (uint)range.Key.Coverage);
            HashValue(ref hash, range.Key.CullMode);
            HashValue(ref hash, range.Key.PrimitiveTopology);
            HashValue(ref hash, (uint)range.Key.Producer);
            HashValue(ref hash, range.PayloadCapacity);
        }
        return hash;
    }

    private static void HashValue(ref ulong hash, uint value)
    {
        hash ^= value;
        hash *= 1099511628211UL;
    }
}
