namespace XREngine.Rendering;

/// <summary>
/// Production backend support and shader identity for aggregate deformation.
/// Both supported desktop APIs use the same logical job/output layout.
/// </summary>
public static class AdvancedDeformationBackendContract
{
    public const string AggregateShaderPath =
        "Advanced/Preparation/AggregateDeformation.comp";

    public static bool SupportsProductionAggregateCompute(
        RuntimeGraphicsApiKind backend)
        => backend is RuntimeGraphicsApiKind.OpenGL or
            RuntimeGraphicsApiKind.Vulkan;

    public static EAdvancedSynchronizationMode ResolveSynchronizationMode(
        RuntimeGraphicsApiKind backend,
        bool vulkanSynchronization2)
        => backend switch
        {
            RuntimeGraphicsApiKind.OpenGL
                => EAdvancedSynchronizationMode.OpenGlMemoryBarrier,
            RuntimeGraphicsApiKind.Vulkan when vulkanSynchronization2
                => EAdvancedSynchronizationMode.VulkanSynchronization2,
            RuntimeGraphicsApiKind.Vulkan
                => EAdvancedSynchronizationMode.VulkanLegacyBarriers,
            _ => throw new NotSupportedException(
                $"{backend} has no production aggregate deformation lowering."),
        };
}
