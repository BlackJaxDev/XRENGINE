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
    VulkanFrameDataSlice Draws,
    VulkanFrameDataSlice Instances,
    VulkanFrameDataSlice Geometry,
    VulkanFrameDataSlice StaticVertices,
    VulkanFrameDataSlice Indices,
    VulkanFrameDataSlice PreSkinnedCurrent,
    VulkanFrameDataSlice PreSkinnedPrevious,
    VulkanFrameDataSlice MeshletDescriptors,
    VulkanFrameDataSlice MeshletVertexIndices,
    VulkanFrameDataSlice MeshletTriangleWords,
    VulkanFrameDataSlice Transforms,
    VulkanFrameDataSlice Deformations,
    VulkanFrameDataSlice RenderStates,
    VulkanFrameDataSlice EditorIdentities,
    VulkanFrameDataSlice Materials,
    VulkanFrameDataSlice ShadingKernels,
    VulkanFrameDataSlice MaterialLayouts,
    VulkanFrameDataSlice MaterialConstants,
    VulkanFrameDataSlice MaterialTextureBindings,
    VulkanFrameDataSlice Textures,
    VulkanFrameDataSlice Samplers,
    VulkanFrameDataSlice Lights,
    VulkanFrameDataSlice Shadows,
    VulkanFrameDataSlice Probes,
    VulkanFrameDataSlice Environments,
    VulkanFrameDataSlice Decals,
    VulkanFrameDataSlice GiResources,
    VulkanFrameDataSlice Views,
    VulkanFrameDataSlice FrameMetadata,
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
           Draws.IsValid && Instances.IsValid && Geometry.IsValid &&
           StaticVertices.IsValid && Indices.IsValid &&
           PreSkinnedCurrent.IsValid && PreSkinnedPrevious.IsValid &&
           MeshletDescriptors.IsValid && MeshletVertexIndices.IsValid &&
           MeshletTriangleWords.IsValid &&
           Transforms.IsValid && Deformations.IsValid &&
           RenderStates.IsValid && EditorIdentities.IsValid &&
           Materials.IsValid && ShadingKernels.IsValid &&
           MaterialLayouts.IsValid && MaterialConstants.IsValid &&
           MaterialTextureBindings.IsValid && Textures.IsValid &&
           Samplers.IsValid && Lights.IsValid && Shadows.IsValid &&
           Probes.IsValid && Environments.IsValid && Decals.IsValid &&
           GiResources.IsValid && Views.IsValid && FrameMetadata.IsValid &&
           EncodedTextures.IsValid &&
           EncodedSamplers.IsValid && HandleLookups.IsValid &&
           FallbackTable.IsValid;
}
