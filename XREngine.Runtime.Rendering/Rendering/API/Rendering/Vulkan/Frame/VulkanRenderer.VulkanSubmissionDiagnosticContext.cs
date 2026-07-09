namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct VulkanSubmissionDiagnosticContext
    {
        public ulong SubmissionSerial { get; init; }
        public string? SubmissionKind { get; init; }
        public string? FrameOpKind { get; init; }
        public string? OutputTargetName { get; init; }
        public uint OutputWidth { get; init; }
        public uint OutputHeight { get; init; }
        public uint InternalWidth { get; init; }
        public uint InternalHeight { get; init; }
        public ulong FrameId { get; init; }
        public int? FrameSlot { get; init; }
        public uint? SwapchainImageIndex { get; init; }
        public long CommandBufferDirtyGeneration { get; init; }
        public ulong FrameOpsSignature { get; init; }
        public ulong PlannerRevision { get; init; }
        public ulong FrameOpContextId { get; init; }
        public ulong ResourceGeneration { get; init; }
        public ulong DescriptorGeneration { get; init; }
        public ulong WaitTimelineValue { get; init; }
        public ulong SignalTimelineValue { get; init; }
        public string? QueueKind { get; init; }
        public string? Caller { get; init; }
        public uint WaitSemaphoreCount { get; init; }
        public uint SignalSemaphoreCount { get; init; }
        public uint CommandBufferCount { get; init; }
        public ulong FirstCommandBufferHandle { get; init; }
        public ulong FenceHandle { get; init; }
        public ulong LastCommandMarkerSerial { get; init; }
        public ulong LastCommandMarkerGeneration { get; init; }
        public string? LastCommandMarkerKind { get; init; }
        public int LastCommandMarkerPassIndex { get; init; }
        public int LastCommandMarkerBatchIndex { get; init; }
        public ulong ImageLayoutTransitionSerial { get; init; }
        public ulong DescriptorTableGeneration { get; init; }
        public string? FirstFailingApi { get; init; }

        public bool IsEmpty =>
            SubmissionKind is null &&
            FrameOpKind is null &&
            OutputTargetName is null &&
            CommandBufferCount == 0 &&
            FirstCommandBufferHandle == 0 &&
            FenceHandle == 0;
    }
}
