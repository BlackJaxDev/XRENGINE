namespace XREngine.Rendering.Commands;

/// <summary>
/// Ring-owned immutable submission image. Access is valid only while its lease
/// remains held, including asynchronous backend work and queue completion.
/// </summary>
public sealed class GpuMeshSubmissionPublication
{
    private GpuMeshSubmissionRecord[] _records = [];
    private GpuMeshSubmissionSourceBindings?[] _bindingClosures = [];
    private readonly Dictionary<IRenderCommandMesh,
        (EGpuMeshSubmissionSourceOwnership Ownership, int PrimitiveCount, int ExpectedPrimitiveCount)> _sourceOwnership =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ulong> _sourcePrimitiveKeys = [];
    public ulong FrameId { get; private set; }
    public ulong Sequence { get; private set; }
    public int Count { get; private set; }
    public bool HasMixedSourceOwnership { get; private set; }
    public bool HasIncompleteSources { get; private set; }
    public ReadOnlySpan<GpuMeshSubmissionRecord> Records => _records.AsSpan(0, Count);
    internal int PinCount { get; set; }
    internal Span<GpuMeshSubmissionRecord> WritableRecords => _records.AsSpan(0, Count);

    /// <summary>
    /// Resolves CPU/GPU ownership exclusively from this leased publication.
    /// Missing and mixed ownership must be rejected before whole-source replay.
    /// </summary>
    public EGpuMeshSubmissionSourceOwnership GetSourceOwnership(IRenderCommandMesh source)
    {
        if (!_sourceOwnership.TryGetValue(source, out var state))
            return EGpuMeshSubmissionSourceOwnership.Missing;
        return state.PrimitiveCount != state.ExpectedPrimitiveCount
            ? EGpuMeshSubmissionSourceOwnership.IncompleteSource : state.Ownership;
    }

    public bool TryGetSourceOwnership(RenderCommand source, out EGpuMeshSubmissionSourceOwnership ownership)
    {
        ownership = source is IRenderCommandMesh meshSource
            ? GetSourceOwnership(meshSource) : EGpuMeshSubmissionSourceOwnership.Missing;
        return ownership != EGpuMeshSubmissionSourceOwnership.Missing;
    }

    /// <summary>Preflights only nonzero sources selected by this authored pass before any whole-source replay.</summary>
    public bool TryGetInvalidSourceOwnership(int renderPass, out EGpuMeshSubmissionSourceOwnership ownership)
    {
        for (int index = 0; index < Count; index++)
        {
            ref readonly GpuMeshSubmissionRecord record = ref _records[index];
            if (record.InstanceCount == 0 || renderPass >= 0 && record.RenderPass >= 0 && record.RenderPass != renderPass)
                continue;
            ownership = GetSourceOwnership(record.Source);
            if (ownership is EGpuMeshSubmissionSourceOwnership.Missing or EGpuMeshSubmissionSourceOwnership.IncompleteSource or
                EGpuMeshSubmissionSourceOwnership.MixedExplicitOwnership)
                return true;
        }
        ownership = default;
        return false;
    }

    internal void BeginCapture(ulong frameId, ulong sequence, ReadOnlySpan<GpuMeshSubmissionRecord> records)
    {
        if (PinCount != 0)
            throw new InvalidOperationException("GPUScene.MeshSubmission.PinnedPublication: a leased image cannot be overwritten.");
        if (_records.Length < records.Length)
        {
            int capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(64, records.Length));
            Array.Resize(ref _records, capacity);
            Array.Resize(ref _bindingClosures, capacity);
        }
        if (records.Length < Count)
            Array.Clear(_records, records.Length, Count - records.Length);
        records.CopyTo(_records);
        for (int index = 0; index < records.Length; index++)
        {
            GpuMeshSubmissionSourceBindings closure = records[index].SourceBindings.CapturePublication(_bindingClosures[index]);
            _bindingClosures[index] = closure;
            _records[index] = records[index] with { SourceBindings = closure };
        }
        FrameId = frameId;
        Sequence = sequence;
        Count = records.Length;
    }

    internal void CompleteCapture()
    {
        if (PinCount != 0)
            throw new InvalidOperationException("GPUScene.MeshSubmission.PinnedPublication: a leased image cannot be overwritten.");
        _sourceOwnership.Clear();
        _sourcePrimitiveKeys.Clear();
        HasMixedSourceOwnership = false;
        HasIncompleteSources = false;
        // Topology changes may grow this retained map at the swap boundary;
        // warmed publication and source lookup allocate no managed storage.
        _sourceOwnership.EnsureCapacity(Count);
        _sourcePrimitiveKeys.EnsureCapacity(Count);
        for (int index = 0; index < Count; index++)
        {
            ref readonly GpuMeshSubmissionRecord record = ref _records[index];
            if (record.InstanceCount == 0)
                continue;
            bool explicitCpu = record.ForceCpuRendering
                || (record.Metadata.Flags & (uint)GPUIndirectRenderFlags.CpuFallbackOnly) != 0;
            EGpuMeshSubmissionSourceOwnership ownership = explicitCpu
                ? EGpuMeshSubmissionSourceOwnership.ExplicitCpu : EGpuMeshSubmissionSourceOwnership.Gpu;
            ulong primitiveKey = ((ulong)record.StableQueryKey << 32) | unchecked((uint)record.PrimitiveIndex);
            bool uniquePrimitive = _sourcePrimitiveKeys.Add(primitiveKey);
            int primitiveCount = uniquePrimitive ? 1 : 0;
            int expectedPrimitiveCount = record.SourcePrimitiveCount;
            bool invalidMembership = !uniquePrimitive || expectedPrimitiveCount <= 0
                || (uint)record.PrimitiveIndex >= (uint)expectedPrimitiveCount;
            if (_sourceOwnership.TryGetValue(record.Source, out var previous))
            {
                primitiveCount += previous.PrimitiveCount;
                invalidMembership |= previous.Ownership == EGpuMeshSubmissionSourceOwnership.IncompleteSource
                    || previous.ExpectedPrimitiveCount != expectedPrimitiveCount;
                if (previous.Ownership == EGpuMeshSubmissionSourceOwnership.MixedExplicitOwnership
                    || ((previous.Ownership is EGpuMeshSubmissionSourceOwnership.Gpu or EGpuMeshSubmissionSourceOwnership.ExplicitCpu)
                        && previous.Ownership != ownership))
                {
                    ownership = EGpuMeshSubmissionSourceOwnership.MixedExplicitOwnership;
                    HasMixedSourceOwnership = true;
                }
            }
            if (invalidMembership)
                ownership = EGpuMeshSubmissionSourceOwnership.IncompleteSource;
            _sourceOwnership[record.Source] = (ownership, primitiveCount, expectedPrimitiveCount);
        }
        foreach (var entry in _sourceOwnership)
        {
            var state = entry.Value;
            if (state.PrimitiveCount != state.ExpectedPrimitiveCount
                || state.Ownership == EGpuMeshSubmissionSourceOwnership.IncompleteSource)
            {
                HasIncompleteSources = true;
                break;
            }
        }
    }

    internal void ClearRetainedSources()
    {
        if (PinCount != 0)
            return;
        for (int index = 0; index < Count; index++)
            _bindingClosures[index]?.ReleaseRetainedSources();
        Array.Clear(_records, 0, Count);
        _sourceOwnership.Clear();
        _sourcePrimitiveKeys.Clear();
        HasMixedSourceOwnership = false;
        HasIncompleteSources = false;
        Count = 0;
        FrameId = 0;
        Sequence = 0;
    }
}
