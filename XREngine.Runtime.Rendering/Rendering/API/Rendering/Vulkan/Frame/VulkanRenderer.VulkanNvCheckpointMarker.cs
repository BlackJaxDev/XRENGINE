namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct VulkanNvCheckpointMarker
    {
        public ulong Serial { get; init; }
        public string? OpKind { get; init; }
        public string? OutputTargetName { get; init; }
        public int PassIndex { get; init; }
        public int BatchIndex { get; init; }
        public int PipelineIdentity { get; init; }
        public int ViewportIdentity { get; init; }
        public ulong CommandBufferHandle { get; init; }
        public ulong CommandBufferRecordingGeneration { get; init; }
    }
}
