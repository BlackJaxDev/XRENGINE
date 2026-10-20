using System.Runtime.InteropServices;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Keeps a sealed canonical visibility stream across frames. The records, bin
/// headers, manifests, and submission plans hold no frame binding, so a frame
/// whose seal inputs are equal to the inputs of the kept seal applies only the
/// frame bindings: the scene native generation and the deformation output buffers.
/// </summary>
/// <remarks>
/// The owning frame plan resets the stream after its frame completes. The reset
/// keeps the sealed content (<see cref="RetainForNextPlan"/>) but clears the
/// sealed state of the frame plan, so the next seal must either prove equal
/// inputs (<see cref="TryReuseVisibilitySeal"/>) or build the stream again.
/// </remarks>
internal sealed partial class VulkanPreparedStableBinStream
{
    // Vertex binding signature of every deformed visibility record ("DEFORMED").
    // The deformation output buffer itself is a frame binding.
    private const ulong DeformedVertexSourceSignatureTag = 0x4445464F524D4544UL;

    private AdvancedVisibilityPayload[] _retainedPayloads = [];
    private EAdvancedGeometryProducer[] _retainedProducers = [];
    private AdvancedDeformedArenaSlice[] _retainedSlices = [];
    private AdvancedIndirectRange[] _retainedRanges = [];
    private int[] _retainedPayloadIndices = [];
    private int _retainedPayloadCount;
    private int _retainedRangeCount;
    private bool _retainedSealValid;
    private bool _retainedRequiresDeformation;
    private VulkanVisibilitySealSignature _retainedSealSignature;
    // The largest byte extent that a deformed record reads in the current or
    // previous deformation output. Each frame's output must cover it.
    private ulong _deformedVertexExtent;
    // Set after the raster pipeline step validated every range member against
    // the sealed payloads. Kept with the seal, because a kept seal has equal payloads.
    private bool _rasterMembersValidated;
    private ulong _boundSceneNativeGeneration;

    /// <summary>The scene native generation that the current frame bound to this stream.</summary>
    internal ulong BoundSceneNativeGeneration => _boundSceneNativeGeneration;

    /// <summary>
    /// Ends the use of this stream by its frame plan. A kept visibility seal stays
    /// for the next frame of the plan; any other stream is thawed.
    /// </summary>
    internal void RetainForNextPlan()
    {
        if (!_retainedSealValid || !_frozen)
        {
            ThawForReuse();
            return;
        }

        ReleaseRetainedTemplates();
        _submissionPlansSealed = false;
        _boundSceneNativeGeneration = 0u;
        _visibilityGeometrySources = default;
    }

    /// <summary>
    /// Keeps the sealed stream of an earlier frame when every seal input is equal,
    /// and binds the scene publication and deformation output of this frame.
    /// </summary>
    internal bool TryReuseVisibilitySeal(
        VulkanResourceRuntime resources,
        in VulkanVisibilitySealSignature signature,
        ReadOnlySpan<AdvancedVisibilityPayload> payloads,
        ReadOnlySpan<EAdvancedGeometryProducer> producers,
        ReadOnlySpan<AdvancedDeformedArenaSlice> slices,
        ReadOnlySpan<AdvancedIndirectRange> ranges,
        ReadOnlySpan<int> payloadIndices,
        in AdvancedGpuDeformationPublication deformationPublication,
        in VulkanAdvancedScenePublicationState sceneState,
        out EAdvancedVisibilityStreamReuseMiss miss)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!_retainedSealValid || !_frozen || _submissionPlansSealed)
        {
            miss = EAdvancedVisibilityStreamReuseMiss.NoRetainedStream;
            return false;
        }
        miss = signature.FindMiss(in _retainedSealSignature);
        if (miss != EAdvancedVisibilityStreamReuseMiss.None)
            return false;
        if (!ColumnsMatch(payloads, producers, slices, ranges, payloadIndices, in deformationPublication))
        {
            miss = EAdvancedVisibilityStreamReuseMiss.Columns;
            return false;
        }

        VulkanVisibilityPreparedVertexSource currentVertices = new(sceneState.StaticVertices, default, 64u);
        VulkanVisibilityPreparedVertexSource previousVertices = currentVertices;
        if (_retainedRequiresDeformation &&
            (!TryCaptureDeformationSources(resources, in deformationPublication,
                out currentVertices, out previousVertices, out _) ||
             currentVertices.Length < _deformedVertexExtent ||
             previousVertices.Length < _deformedVertexExtent))
        {
            miss = EAdvancedVisibilityStreamReuseMiss.DeformationOutput;
            return false;
        }

        VulkanAdvancedVisibilityGeometrySlices sources = new(
            sceneState.StaticVertices,
            currentVertices,
            previousVertices,
            sceneState.Indices,
            sceneState.MeshletDescriptors,
            sceneState.MeshletVertexIndices,
            sceneState.MeshletTriangleWords,
            default);
        if (!sources.HasValidSources)
        {
            miss = EAdvancedVisibilityStreamReuseMiss.SceneGeometry;
            return false;
        }

        // The raster and shadow pipelines and the header native state belong to
        // the earlier frame's target closure and deformation output. The raster
        // pipeline step of this frame prepares them again.
        for (int headerIndex = 0; headerIndex < _headerCount; ++headerIndex)
        {
            _headers[headerIndex] = _headers[headerIndex] with
            {
                RasterPipeline = default,
                NativeState = default,
                ShadowRasterPipeline = default,
            };
        }
        _visibilityGeometrySources = sources;
        _boundSceneNativeGeneration = sceneState.NativeGeneration;
        _submissionPlansSealed = true;
        return true;
    }

    /// <summary>
    /// Keeps the seal inputs of a stream that this frame sealed, so that a later
    /// frame with equal inputs can keep the stream.
    /// </summary>
    internal void RetainVisibilitySeal(
        in VulkanVisibilitySealSignature signature,
        ReadOnlySpan<AdvancedVisibilityPayload> payloads,
        ReadOnlySpan<EAdvancedGeometryProducer> producers,
        ReadOnlySpan<AdvancedDeformedArenaSlice> slices,
        ReadOnlySpan<AdvancedIndirectRange> ranges,
        ReadOnlySpan<int> payloadIndices)
    {
        if (!_frozen || !_submissionPlansSealed)
        {
            _retainedSealValid = false;
            return;
        }

        // The columns grow only on a new high-water mark, so steady state does not allocate.
        EnsureRetainedCapacity(ref _retainedPayloads, payloads.Length);
        EnsureRetainedCapacity(ref _retainedProducers, producers.Length);
        EnsureRetainedCapacity(ref _retainedSlices, slices.Length);
        EnsureRetainedCapacity(ref _retainedPayloadIndices, payloadIndices.Length);
        EnsureRetainedCapacity(ref _retainedRanges, ranges.Length);
        payloads.CopyTo(_retainedPayloads);
        producers.CopyTo(_retainedProducers);
        payloadIndices.CopyTo(_retainedPayloadIndices);
        ranges.CopyTo(_retainedRanges);
        bool requiresDeformation = false;
        for (int index = 0; index < slices.Length; ++index)
        {
            _retainedSlices[index] = WithoutFrameSlots(in slices[index]);
            requiresDeformation |= payloads[index].Skinned;
        }
        _retainedPayloadCount = payloads.Length;
        _retainedRangeCount = ranges.Length;
        _retainedRequiresDeformation = requiresDeformation;
        _retainedSealSignature = signature;
        _retainedSealValid = true;
    }

    private bool ColumnsMatch(
        ReadOnlySpan<AdvancedVisibilityPayload> payloads,
        ReadOnlySpan<EAdvancedGeometryProducer> producers,
        ReadOnlySpan<AdvancedDeformedArenaSlice> slices,
        ReadOnlySpan<AdvancedIndirectRange> ranges,
        ReadOnlySpan<int> payloadIndices,
        in AdvancedGpuDeformationPublication deformationPublication)
    {
        int count = _retainedPayloadCount;
        if (payloads.Length != count || producers.Length != count ||
            slices.Length != count || payloadIndices.Length != count ||
            !payloads.SequenceEqual(_retainedPayloads.AsSpan(0, count)) ||
            !MemoryMarshal.AsBytes(producers).SequenceEqual(
                MemoryMarshal.AsBytes(_retainedProducers.AsSpan(0, count))) ||
            !payloadIndices.SequenceEqual(_retainedPayloadIndices.AsSpan(0, count)) ||
            !ranges.SequenceEqual(_retainedRanges.AsSpan(0, _retainedRangeCount)))
        {
            return false;
        }

        // The slice offsets must be equal. The frame slots are frame bindings, but
        // a deformed slice must name the slots of this frame's deformation output.
        ReadOnlySpan<AdvancedDeformedArenaSlice> retained = _retainedSlices.AsSpan(0, count);
        for (int index = 0; index < count; ++index)
        {
            ref readonly AdvancedDeformedArenaSlice slice = ref slices[index];
            if (WithoutFrameSlots(in slice) != retained[index])
                return false;
            if (payloads[index].Skinned &&
                (slice.CurrentFrameSlot != deformationPublication.CurrentFrameSlot ||
                 slice.PreviousFrameSlot != deformationPublication.PreviousFrameSlot))
                return false;
        }
        return true;
    }

    private void InvalidateRetainedVisibilitySeal()
    {
        _retainedSealValid = false;
        _rasterMembersValidated = false;
    }

    private static AdvancedDeformedArenaSlice WithoutFrameSlots(in AdvancedDeformedArenaSlice slice)
        => slice with { CurrentFrameSlot = 0u, PreviousFrameSlot = 0u };

    private static void EnsureRetainedCapacity<T>(ref T[] column, int length)
    {
        if (column.Length < length)
            column = new T[length];
    }
}
