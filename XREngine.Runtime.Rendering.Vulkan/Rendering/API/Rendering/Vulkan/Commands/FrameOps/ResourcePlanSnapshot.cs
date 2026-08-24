namespace XREngine.Rendering.Vulkan;

internal readonly record struct ResourcePlanSnapshot(
    ulong Revision,
    ulong PhysicalImageSignature,
    ulong FramebufferSignature,
    ulong PipelineGeneration,
    ulong RenderArea = 0UL,
    uint QueueFamily = 0u,
    VulkanRecordedRenderTargetSnapshot NativeTarget = default)
{
    public bool HasCompleteNativeIdentity => NativeTarget.IsComplete;

    /// <summary>
    /// Packs the exact zero-origin render extent recorded for a packet. Vulkan render
    /// areas are part of secondary-command inheritance and must never be represented
    /// by the former zero placeholder.
    /// </summary>
    public static ulong PackRenderArea(uint width, uint height)
        => ((ulong)width << 32) | height;
}
