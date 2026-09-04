using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

// These records deliberately mirror the immutable data consumed by native
// recording.  They are not FrameOps: after Lower completes no producer object
// is retained by a frame plan or a prepared worker.
internal readonly record struct TextureUploadPayload(VulkanImportedTexturePendingUpload Upload);
internal readonly record struct BlitPayload(XRFrameBuffer? InFbo, XRFrameBuffer? OutFbo, int InX, int InY, uint InW, uint InH, int OutX, int OutY, uint OutW, uint OutH, EReadBufferMode ReadBufferMode, bool ColorBit, bool DepthBit, bool StencilBit, bool LinearFilter);
internal readonly record struct ClearPayload(bool ClearColor, bool ClearDepth, bool ClearStencil, ColorF4 Color, float Depth, uint Stencil, Rect2D Rect);
internal readonly record struct TransformFeedbackPayload(VkTransformFeedback TransformFeedback, EXRTransformFeedbackOperation Operation, XRDataBuffer? CounterBuffer, ulong FeedbackBufferOffset, ulong? FeedbackBufferSize, ulong CounterBufferOffset, uint CounterOffset, uint VertexStride, uint InstanceCount, uint FirstInstance);
internal readonly record struct QueryPayload(VkRenderQuery Query, RenderQueryDescriptor Descriptor, ERenderQueryOperation Operation, PipelineStageFlags2 TimestampStage, uint PointIndex, ReadOnlyMemory<ulong> SourceHandles, Buffer ResultDestination, ulong ResultDestinationOffset, ulong ResultStride, bool IncludeAvailability);
internal readonly record struct MeshDrawPayload(PendingMeshDraw Draw);
internal readonly record struct IndirectDrawPayload(VkDataBuffer IndirectBuffer, VkDataBuffer? ParameterBuffer, VkMeshRenderer MeshRenderer, PendingMeshDraw Draw, uint DrawCount, uint Stride, nuint ByteOffset, nuint CountByteOffset, bool UseCount, VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures, VulkanIndirectSecondaryRecordingContract SecondaryRecordingContract);
internal readonly record struct MeshTaskDispatchIndirectCountPayload(VkRenderProgram Program, ulong ProgramLinkGeneration, ComputeDispatchSnapshot ProgramBindingSnapshot, VulkanMeshProducerSnapshot ProducerSnapshot, Pipeline Pipeline, VkDataBuffer IndirectBuffer, VkDataBuffer CountBuffer, uint MaxDrawCount, uint Stride, nuint ByteOffset, nuint CountByteOffset, VulkanBindlessMaterialDescriptorBinding? BindlessMaterialTextures);
internal readonly record struct ComputeDispatchPayload(VkRenderProgram Program, uint GroupsX, uint GroupsY, uint GroupsZ, ComputeDispatchSnapshot Snapshot);
internal readonly record struct ComputeDispatchIndirectPayload(VkRenderProgram Program, ComputeDispatchSnapshot Snapshot, VkDataBuffer ArgumentOwner, Buffer ArgumentBuffer, ulong ArgumentOffset, string Label);
internal readonly record struct BufferCopyPayload(VkDataBuffer SourceOwner, Buffer SourceBuffer, ulong SourceOffset, VkDataBuffer DestinationOwner, Buffer DestinationBuffer, ulong DestinationOffset, ulong ByteCount, bool RequireGpuWriteVisibility, GpuDiagnosticSnapshotReceipt? DiagnosticReceipt, string Label);
internal readonly record struct SubmissionMarkerPayload(VulkanTimelineGpuFence Fence, string Label);
internal readonly record struct MemoryBarrierPayload(EMemoryBarrierMask Mask);
internal readonly record struct PublishFramebufferPayload(XRFrameBuffer FrameBuffer);
internal readonly record struct DlssUpscalePayload(NvidiaDlssManager.Native.NativeVulkanSession Session, VulkanStreamlineImage SourceColor, VulkanStreamlineImage Depth, VulkanStreamlineImage Motion, VulkanStreamlineImage OutputColor, VulkanStreamlineImage? Exposure, VulkanUpscaleBridgeDispatchParameters Parameters);
internal readonly record struct DlssFrameGenerationPayload(NvidiaDlssManager.Native.NativeFrameGenerationSession Session, VulkanStreamlineImage Depth, VulkanStreamlineImage Motion, VulkanStreamlineImage HudlessColor, VulkanUpscaleBridgeDispatchParameters Parameters, VulkanStreamlineImage UiColorAndAlpha);
/// <summary>
/// Sealed authoring request plus the exact set-1 frame-slot state admitted at
/// primary preparation. The logical attachment names intentionally remain in
/// <see cref="VulkanAdvancedVisibilityStageRequest"/> until recording can
/// resolve the frozen render-graph generation.
/// </summary>
internal readonly record struct VulkanAdvancedVisibilityOperationPayload(
    VulkanAdvancedVisibilityStageRequest Request,
    VulkanAdvancedVisibilityInputStorage Input,
    VulkanAdvancedVisibilityResourceState State,
    VulkanAdvancedScenePublicationState SceneState,
    VulkanAdvancedVisibilityTargetClosure TargetClosure,
    VulkanAdvancedVisibilityLateTargetClosure? LateTargetClosure,
    VulkanAdvancedNativeComputeClosure? NativeComputeClosure,
    DescriptorSet NativeComputeDescriptorSet,
    VkRenderProgram? EarlyVisibilityProgram,
    Pipeline EarlyVisibilityPipeline,
    ulong EarlyVisibilityLinkGeneration,
    VkRenderProgram? BuildIndirectProgram,
    Pipeline BuildIndirectPipeline,
    ulong BuildIndirectLinkGeneration,
    VkRenderProgram? BuildDepthPyramidProgram,
    Pipeline BuildDepthPyramidPipeline,
    ulong BuildDepthPyramidLinkGeneration,
    VkRenderProgram? LateVisibilityProgram,
    Pipeline LateVisibilityPipeline,
    ulong LateVisibilityLinkGeneration,
    VulkanAdvancedNativeComputePipelines NativeComputePipelines = default);

/// <summary>
/// Per-opcode dense storage owned exclusively by a sealed operation stream.
/// The arrays carry concrete payload records rather than a polymorphic
/// <see cref="FrameOp"/> sidecar.
/// </summary>
internal sealed class FrameOperationPayloadStore
{
    private readonly bool _fixedCapacity;
    private readonly EVulkanAcceptedFrameLane _lane;

    internal TextureUploadPayload[] TextureUploads;
    internal BlitPayload[] Blits;
    internal ClearPayload[] Clears;
    internal TransformFeedbackPayload[] TransformFeedbacks;
    internal QueryPayload[] Queries;
    internal MeshDrawPayload[] MeshDraws;
    internal IndirectDrawPayload[] IndirectDraws;
    internal MeshTaskDispatchIndirectCountPayload[] MeshTasks;
    internal ComputeDispatchPayload[] ComputeDispatches;
    internal ComputeDispatchIndirectPayload[] ComputeDispatchIndirects;
    internal BufferCopyPayload[] BufferCopies;
    internal SubmissionMarkerPayload[] SubmissionMarkers;
    internal MemoryBarrierPayload[] MemoryBarriers;
    internal PublishFramebufferPayload[] PublishedFramebuffers;
    internal DlssUpscalePayload[] DlssUpscales;
    internal DlssFrameGenerationPayload[] DlssFrameGenerations;
    internal VulkanAdvancedVisibilityOperationPayload[] AdvancedVisibilities;
    internal VulkanAdvancedVisibilityLateClosureStorage[] AdvancedVisibilityLateClosures;
    internal VulkanAdvancedNativeComputeClosureStorage[] AdvancedVisibilityNativeComputeClosures;
    internal readonly VulkanAdvancedVisibilityInputStorage AdvancedVisibilityInput;

    internal FrameOperationPayloadStore()
        : this(
            generalCapacity: 0,
            meshCapacity: 0,
            textureCapacity: 0,
            advancedVisibilityDrawCapacity: 0,
            advancedVisibilityRangeCapacity: 0,
            fixedCapacity: false,
            EVulkanAcceptedFrameLane.MainScene)
    {
    }

    /// <summary>
    /// Allocates the complete payload budget at frame-slot construction time.
    /// A sealed foreground frame may consume this storage, but never grow it.
    /// </summary>
    internal FrameOperationPayloadStore(
        int generalCapacity,
        int meshCapacity,
        int textureCapacity,
        int advancedVisibilityDrawCapacity,
        int advancedVisibilityRangeCapacity,
        bool fixedCapacity,
        EVulkanAcceptedFrameLane lane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generalCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(meshCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(textureCapacity);

        _fixedCapacity = fixedCapacity;
        _lane = lane;
        TextureUploads = new TextureUploadPayload[textureCapacity];
        Blits = new BlitPayload[generalCapacity];
        Clears = new ClearPayload[generalCapacity];
        TransformFeedbacks = new TransformFeedbackPayload[generalCapacity];
        Queries = new QueryPayload[generalCapacity];
        MeshDraws = new MeshDrawPayload[meshCapacity];
        IndirectDraws = new IndirectDrawPayload[generalCapacity];
        MeshTasks = new MeshTaskDispatchIndirectCountPayload[generalCapacity];
        ComputeDispatches = new ComputeDispatchPayload[generalCapacity];
        ComputeDispatchIndirects = new ComputeDispatchIndirectPayload[generalCapacity];
        BufferCopies = new BufferCopyPayload[generalCapacity];
        SubmissionMarkers = new SubmissionMarkerPayload[generalCapacity];
        MemoryBarriers = new MemoryBarrierPayload[generalCapacity];
        PublishedFramebuffers = new PublishFramebufferPayload[generalCapacity];
        DlssUpscales = new DlssUpscalePayload[generalCapacity];
        DlssFrameGenerations = new DlssFrameGenerationPayload[generalCapacity];
        AdvancedVisibilities = new VulkanAdvancedVisibilityOperationPayload[generalCapacity];
        AdvancedVisibilityLateClosures =
            CreateAdvancedVisibilityLateClosureStorage(generalCapacity);
        AdvancedVisibilityNativeComputeClosures =
            CreateAdvancedNativeComputeClosureStorage(generalCapacity);
        AdvancedVisibilityInput = new VulkanAdvancedVisibilityInputStorage(
            advancedVisibilityDrawCapacity,
            advancedVisibilityRangeCapacity,
            fixedCapacity,
            lane);
    }

    internal void EnsureCapacity(EVulkanPrimaryPlanNodeKind kind, int count)
    {
        switch (kind)
        {
            case EVulkanPrimaryPlanNodeKind.TextureUpload: Ensure(ref TextureUploads, count); break;
            case EVulkanPrimaryPlanNodeKind.Blit: Ensure(ref Blits, count); break;
            case EVulkanPrimaryPlanNodeKind.Clear: Ensure(ref Clears, count); break;
            case EVulkanPrimaryPlanNodeKind.TransformFeedback: Ensure(ref TransformFeedbacks, count); break;
            case EVulkanPrimaryPlanNodeKind.Query: Ensure(ref Queries, count); break;
            case EVulkanPrimaryPlanNodeKind.MeshDraw: Ensure(ref MeshDraws, count); break;
            case EVulkanPrimaryPlanNodeKind.IndirectDraw: Ensure(ref IndirectDraws, count); break;
            case EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount: Ensure(ref MeshTasks, count); break;
            case EVulkanPrimaryPlanNodeKind.ComputeDispatch: Ensure(ref ComputeDispatches, count); break;
            case EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect: Ensure(ref ComputeDispatchIndirects, count); break;
            case EVulkanPrimaryPlanNodeKind.BufferCopy: Ensure(ref BufferCopies, count); break;
            case EVulkanPrimaryPlanNodeKind.SubmissionMarker: Ensure(ref SubmissionMarkers, count); break;
            case EVulkanPrimaryPlanNodeKind.MemoryBarrier: Ensure(ref MemoryBarriers, count); break;
            case EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling: Ensure(ref PublishedFramebuffers, count); break;
            case EVulkanPrimaryPlanNodeKind.DlssUpscale: Ensure(ref DlssUpscales, count); break;
            case EVulkanPrimaryPlanNodeKind.DlssFrameGeneration: Ensure(ref DlssFrameGenerations, count); break;
            case EVulkanPrimaryPlanNodeKind.AdvancedVisibility:
                Ensure(ref AdvancedVisibilities, count);
                EnsureAdvancedVisibilityLateClosureCapacity(count);
                EnsureAdvancedNativeComputeClosureCapacity(count);
                break;
        }
    }

    /// <summary>
    /// Releases immutable storage leases owned by this physical payload store.
    /// Logical OpenXR streams can share this store and must not invoke this
    /// during header-only resets.
    /// </summary>
    internal void ReleaseReadOnlyStorageBindings()
    {
        for (int index = 0; index < MeshDraws.Length; ++index)
            MeshDraws[index].Draw.ProgramBindingSnapshot?.ReleaseReadOnlyStorageBindings();
        for (int index = 0; index < IndirectDraws.Length; ++index)
            IndirectDraws[index].Draw.ProgramBindingSnapshot?.ReleaseReadOnlyStorageBindings();
        for (int index = 0; index < MeshTasks.Length; ++index)
            MeshTasks[index].ProgramBindingSnapshot?.ReleaseReadOnlyStorageBindings();
        for (int index = 0; index < ComputeDispatches.Length; ++index)
            ComputeDispatches[index].Snapshot?.ReleaseReadOnlyStorageBindings();
        for (int index = 0; index < ComputeDispatchIndirects.Length; ++index)
            ComputeDispatchIndirects[index].Snapshot?.ReleaseReadOnlyStorageBindings();
        for (int index = 0; index < AdvancedVisibilityNativeComputeClosures.Length; ++index)
            AdvancedVisibilityNativeComputeClosures[index].ReleaseAcquiredViews();
        for (int index = 0; index < IndirectDraws.Length; ++index)
        {
            IndirectDrawPayload payload = IndirectDraws[index];
            payload.BindlessMaterialTextures?.Dispose();
            IndirectDraws[index] = payload with { BindlessMaterialTextures = null };
        }
        for (int index = 0; index < MeshTasks.Length; ++index)
        {
            MeshTaskDispatchIndirectCountPayload payload = MeshTasks[index];
            payload.BindlessMaterialTextures?.Dispose();
            MeshTasks[index] = payload with { BindlessMaterialTextures = null };
        }
    }

    private void Ensure<T>(ref T[] values, int count)
    {
        if (values.Length >= count)
            return;
        if (_fixedCapacity)
            throw new VulkanAcceptedFramePlanCapacityException(
                _lane,
                values.Length,
                count);

        Array.Resize(
            ref values,
            Math.Max(count, values.Length == 0 ? 4 : values.Length * 2));
    }

    private void EnsureAdvancedVisibilityLateClosureCapacity(int count)
    {
        if (AdvancedVisibilityLateClosures.Length >= count)
            return;
        if (_fixedCapacity)
            throw new VulkanAcceptedFramePlanCapacityException(
                _lane,
                AdvancedVisibilityLateClosures.Length,
                count);

        int previousLength = AdvancedVisibilityLateClosures.Length;
        Array.Resize(
            ref AdvancedVisibilityLateClosures,
            Math.Max(count, previousLength == 0 ? 4 : previousLength * 2));
        for (int index = previousLength;
             index < AdvancedVisibilityLateClosures.Length;
             ++index)
        {
            AdvancedVisibilityLateClosures[index] = new();
        }
    }

    private static VulkanAdvancedVisibilityLateClosureStorage[]
        CreateAdvancedVisibilityLateClosureStorage(int capacity)
    {
        VulkanAdvancedVisibilityLateClosureStorage[] storage =
            new VulkanAdvancedVisibilityLateClosureStorage[capacity];
        for (int index = 0; index < storage.Length; ++index)
            storage[index] = new();
        return storage;
    }

    private void EnsureAdvancedNativeComputeClosureCapacity(int count)
    {
        if (AdvancedVisibilityNativeComputeClosures.Length >= count)
            return;
        if (_fixedCapacity)
            throw new VulkanAcceptedFramePlanCapacityException(
                _lane, AdvancedVisibilityNativeComputeClosures.Length, count);

        int previousLength = AdvancedVisibilityNativeComputeClosures.Length;
        Array.Resize(ref AdvancedVisibilityNativeComputeClosures,
            Math.Max(count, previousLength == 0 ? 4 : previousLength * 2));
        for (int index = previousLength;
             index < AdvancedVisibilityNativeComputeClosures.Length;
             ++index)
        {
            AdvancedVisibilityNativeComputeClosures[index] = new();
        }
    }

    private static VulkanAdvancedNativeComputeClosureStorage[]
        CreateAdvancedNativeComputeClosureStorage(int capacity)
    {
        VulkanAdvancedNativeComputeClosureStorage[] storage =
            new VulkanAdvancedNativeComputeClosureStorage[capacity];
        for (int index = 0; index < storage.Length; ++index)
            storage[index] = new();
        return storage;
    }
}
