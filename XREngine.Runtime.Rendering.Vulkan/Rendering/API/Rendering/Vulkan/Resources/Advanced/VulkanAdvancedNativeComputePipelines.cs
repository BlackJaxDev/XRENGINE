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

    /// <summary>
    /// Copies block representatives to follower pixels after foveated quad shading.
    /// Prepared with the family; it runs only when an RVC-hosted stage requests it.
    /// </summary>
    internal VulkanAdvancedComputePipeline FoveatedQuadFill { get; init; }

    /// <summary>
    /// Standard single-sample shading kernel without the debug views and the per-light debug
    /// globals (XR_ADV_SHADE_LEAN). Frames with no shading debug view use it instead of
    /// <see cref="Shade"/>, because the unused paths still cost registers. Prepared with the
    /// family.
    /// </summary>
    internal VulkanAdvancedComputePipeline ShadeLean { get; init; }

    /// <summary>
    /// Native Uber shading kernel. A single-sample scene with Uber materials
    /// requires this kernel before the native closure is admitted.
    /// </summary>
    internal VulkanAdvancedComputePipeline ShadeUber { get; init; }

    /// <summary>
    /// Combined multisample kernel (standard and Uber in one pass, because one pixel can
    /// hold samples of both families). A multisample scene with Uber materials
    /// requires this kernel before the native closure is admitted.
    /// </summary>
    internal VulkanAdvancedComputePipeline ShadeMultisampleCombined { get; init; }

    /// <summary>
    /// Occlusion-culled bounds overlay kernel. It is requested only while the
    /// overlay is on and is not part of <see cref="IsCurrent"/>. While it compiles,
    /// shading records no overlay.
    /// </summary>
    internal VulkanAdvancedComputePipeline OcclusionCulledBounds { get; init; }

    internal bool IsCurrent => Classify.IsCurrent && BuildArguments.IsCurrent && AmbientOcclusion.IsCurrent &&
        BuildFroxels.IsCurrent && Background.IsCurrent && Shade.IsCurrent && ShadeLean.IsCurrent &&
        RvcShadeletTelemetry.IsCurrent && FoveatedQuadFill.IsCurrent;

    /// <summary>Checks the shading program selected by the sealed visibility mode.</summary>
    internal bool IsCurrentFor(bool multisampleVisibility, bool requiresUber)
        => IsCurrent &&
           (multisampleVisibility
               ? requiresUber ? ShadeMultisampleCombined.IsCurrent : ShadeMultisample.IsCurrent
               : !requiresUber || ShadeUber.IsCurrent);
}
