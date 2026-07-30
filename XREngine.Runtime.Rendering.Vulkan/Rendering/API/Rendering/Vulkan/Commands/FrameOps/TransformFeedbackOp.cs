namespace XREngine.Rendering.Vulkan;

internal sealed record TransformFeedbackOp(
    int PassIndex,
    XRFrameBuffer? Target,
    VkTransformFeedback TransformFeedback,
    EXRTransformFeedbackOperation Operation,
    XRDataBuffer? CounterBuffer,
    ulong FeedbackBufferOffset,
    ulong? FeedbackBufferSize,
    ulong CounterBufferOffset,
    uint CounterOffset,
    uint VertexStride,
    uint InstanceCount,
    uint FirstInstance,
    FrameOpContext Context) : FrameOp(PassIndex, Target, Context);
