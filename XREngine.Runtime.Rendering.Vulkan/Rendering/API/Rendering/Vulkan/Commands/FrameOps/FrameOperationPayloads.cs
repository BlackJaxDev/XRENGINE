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
/// Per-opcode dense storage owned exclusively by a sealed operation stream.
/// The arrays carry concrete payload records rather than a polymorphic
/// <see cref="FrameOp"/> sidecar.
/// </summary>
internal sealed class FrameOperationPayloadStore
{
    internal TextureUploadPayload[] TextureUploads = [];
    internal BlitPayload[] Blits = [];
    internal ClearPayload[] Clears = [];
    internal TransformFeedbackPayload[] TransformFeedbacks = [];
    internal QueryPayload[] Queries = [];
    internal MeshDrawPayload[] MeshDraws = [];
    internal IndirectDrawPayload[] IndirectDraws = [];
    internal MeshTaskDispatchIndirectCountPayload[] MeshTasks = [];
    internal ComputeDispatchPayload[] ComputeDispatches = [];
    internal ComputeDispatchIndirectPayload[] ComputeDispatchIndirects = [];
    internal BufferCopyPayload[] BufferCopies = [];
    internal SubmissionMarkerPayload[] SubmissionMarkers = [];
    internal MemoryBarrierPayload[] MemoryBarriers = [];
    internal PublishFramebufferPayload[] PublishedFramebuffers = [];
    internal DlssUpscalePayload[] DlssUpscales = [];
    internal DlssFrameGenerationPayload[] DlssFrameGenerations = [];

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
        }
    }

    private static void Ensure<T>(ref T[] values, int count)
    {
        if (values.Length < count)
            Array.Resize(ref values, Math.Max(count, values.Length == 0 ? 4 : values.Length * 2));
    }
}
