namespace XREngine.Rendering.Vulkan;

/// <summary>The executable native opaque family retained by a sealed frame operation.</summary>
internal readonly record struct VulkanAdvancedNativeComputePipelines(
    VulkanAdvancedComputePipeline Classify,
    VulkanAdvancedComputePipeline BuildArguments,
    VulkanAdvancedComputePipeline AmbientOcclusion,
    VulkanAdvancedComputePipeline BuildFroxels,
    VulkanAdvancedComputePipeline Background,
    VulkanAdvancedComputePipeline Shade,
    VulkanAdvancedComputePipeline ShadeMultisample)
{
    /// <summary>
    /// RVC shadelet telemetry kernel. Prepared with the family; it runs only when an
    /// RVC-hosted stage requests it.
    /// </summary>
    internal VulkanAdvancedComputePipeline RvcShadeletTelemetry { get; init; }

    internal bool IsCurrent => Classify.IsCurrent && BuildArguments.IsCurrent && AmbientOcclusion.IsCurrent &&
        BuildFroxels.IsCurrent && Background.IsCurrent && Shade.IsCurrent && ShadeMultisample.IsCurrent &&
        RvcShadeletTelemetry.IsCurrent;
}
