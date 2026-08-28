using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Stack-friendly view of a sealed numeric operation stream.</summary>
internal readonly struct FrameOperationSequence
{
    private readonly FrameOperationStream _stream;

    internal static FrameOperationSequence Empty { get; } =
        new(FrameOperationStream.Empty);

    internal FrameOperationSequence(FrameOperationStream stream)
        => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    internal int Length => _stream?.Count ?? 0;
    internal bool IsNumericStream => _stream is not null;
    internal FrameOperationStream Stream => _stream ?? throw new InvalidOperationException("The operation sequence is uninitialized.");

    internal ref readonly FrameOperationHeader GetHeader(int index) => ref Stream.GetHeader(index);
    internal ref readonly FrameOpContext GetContext(int index) => ref Stream.GetContext(index);
    internal ReadOnlySpan<FrameOpResourceUse> GetResourceUses(int index)
        => Stream.GetResourceUses(index);
    internal XRFrameBuffer? GetTarget(int index) => Stream.GetTarget(index);
    internal bool TryGetMeshDraw(int index, out MeshDrawPayload payload) => Stream.TryGetMeshDraw(index, out payload);
    internal bool TryGetIndirectDraw(int index, out IndirectDrawPayload payload) => Stream.TryGetIndirectDraw(index, out payload);
    internal bool TryGetComputeDispatch(int index, out ComputeDispatchPayload payload) => Stream.TryGetComputeDispatch(index, out payload);
    internal ref readonly TextureUploadPayload GetTextureUpload(int index) => ref Stream.GetTextureUpload(index);
    internal ref readonly BlitPayload GetBlit(int index) => ref Stream.GetBlit(index);
    internal ref readonly ClearPayload GetClear(int index) => ref Stream.GetClear(index);
    internal ref readonly TransformFeedbackPayload GetTransformFeedback(int index) => ref Stream.GetTransformFeedback(index);
    internal ref readonly QueryPayload GetQuery(int index) => ref Stream.GetQuery(index);
    internal ref readonly MeshDrawPayload GetMeshDraw(int index) => ref Stream.GetMeshDraw(index);
    internal ref readonly IndirectDrawPayload GetIndirectDraw(int index) => ref Stream.GetIndirectDraw(index);
    internal ref readonly MeshTaskDispatchIndirectCountPayload GetMeshTask(int index) => ref Stream.GetMeshTask(index);
    internal bool TryAssociateAdmittedMeshTaskPipeline(int index, VkRenderProgram program, ulong programLinkGeneration, ComputeDispatchSnapshot programBindingSnapshot, in VulkanMeshProducerSnapshot producerSnapshot, Pipeline pipeline)
        => Stream.TryAssociateAdmittedMeshTaskPipeline(index, program, programLinkGeneration, programBindingSnapshot, in producerSnapshot, pipeline);
    internal ref readonly ComputeDispatchPayload GetComputeDispatch(int index) => ref Stream.GetComputeDispatch(index);
    internal ref readonly ComputeDispatchIndirectPayload GetComputeDispatchIndirect(int index) => ref Stream.GetComputeDispatchIndirect(index);
    internal ref readonly BufferCopyPayload GetBufferCopy(int index) => ref Stream.GetBufferCopy(index);
    internal ref readonly SubmissionMarkerPayload GetSubmissionMarker(int index) => ref Stream.GetSubmissionMarker(index);
    internal ref readonly MemoryBarrierPayload GetMemoryBarrier(int index) => ref Stream.GetMemoryBarrier(index);
    internal ref readonly PublishFramebufferPayload GetPublishedFramebuffer(int index) => ref Stream.GetPublishedFramebuffer(index);
    internal ref readonly DlssUpscalePayload GetDlssUpscale(int index) => ref Stream.GetDlssUpscale(index);
    internal ref readonly DlssFrameGenerationPayload GetDlssFrameGeneration(int index) => ref Stream.GetDlssFrameGeneration(index);
    internal ref readonly VulkanAdvancedVisibilityOperationPayload GetAdvancedVisibility(int index) => ref Stream.GetAdvancedVisibility(index);

}
