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
    /// Native Uber shading kernel. It is prepared only for scenes with Uber kernels, after
    /// the standard kernels are ready. While it is not current, the standard kernel shades
    /// Uber pixels with a base-color fallback. It is not part of <see cref="IsCurrent"/>.
    /// </summary>
    internal VulkanAdvancedComputePipeline ShadeUber { get; init; }

    /// <summary>
    /// Combined multisample kernel (standard and Uber in one pass, because one pixel can
    /// hold samples of both families). It is prepared only for scenes with Uber kernels.
    /// While it is not current, <see cref="ShadeMultisample"/> shades Uber samples with the
    /// base-color fallback. It is not part of <see cref="IsCurrent"/>.
    /// </summary>
    internal VulkanAdvancedComputePipeline ShadeMultisampleCombined { get; init; }

    internal bool IsCurrent => Classify.IsCurrent && BuildArguments.IsCurrent && AmbientOcclusion.IsCurrent &&
        BuildFroxels.IsCurrent && Background.IsCurrent && Shade.IsCurrent &&
        RvcShadeletTelemetry.IsCurrent && FoveatedQuadFill.IsCurrent;

    /// <summary>Checks the shading program selected by the sealed visibility mode.</summary>
    internal bool IsCurrentFor(bool multisampleVisibility)
        => IsCurrent && (!multisampleVisibility || ShadeMultisample.IsCurrent);
}
