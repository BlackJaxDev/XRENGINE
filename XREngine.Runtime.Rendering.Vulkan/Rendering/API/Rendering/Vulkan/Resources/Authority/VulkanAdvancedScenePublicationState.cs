using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable Vulkan lowering of one exact retained canonical publication. All
/// slices belong to the stated frame slot and remain valid while its paired
/// publication use is owned by frame-slot retirement.
/// </summary>
internal readonly record struct VulkanAdvancedScenePublicationState(
    int FrameSlot,
    ulong FrameGeneration,
    ulong NativeGeneration,
    DescriptorSet GlobalDescriptorSet,
    DescriptorSet ResourceDescriptorSet,
    uint TextureDescriptorBase,
    uint TextureDescriptorCount,
    uint SamplerDescriptorBase,
    uint SamplerDescriptorCount,
    VulkanFrameDataSlice Materials,
    VulkanFrameDataSlice ShadingKernels,
    VulkanFrameDataSlice MaterialLayouts,
    VulkanFrameDataSlice MaterialConstants,
    VulkanFrameDataSlice MaterialTextureBindings,
    VulkanFrameDataSlice Textures,
    VulkanFrameDataSlice Samplers,
    VulkanFrameDataSlice EncodedTextures,
    VulkanFrameDataSlice EncodedSamplers,
    VulkanFrameDataSlice HandleLookups,
    VulkanFrameDataSlice FallbackTable,
    VulkanAdvancedSceneLookupSegments LookupSegments)
{
    internal bool IsValid
        => FrameSlot >= 0 && FrameGeneration != 0u &&
           NativeGeneration != 0u &&
           GlobalDescriptorSet.Handle != 0 &&
           ResourceDescriptorSet.Handle != 0 &&
           Materials.IsValid && ShadingKernels.IsValid &&
           MaterialLayouts.IsValid && MaterialConstants.IsValid &&
           MaterialTextureBindings.IsValid && Textures.IsValid &&
           Samplers.IsValid && EncodedTextures.IsValid &&
           EncodedSamplers.IsValid && HandleLookups.IsValid &&
           FallbackTable.IsValid;
}
