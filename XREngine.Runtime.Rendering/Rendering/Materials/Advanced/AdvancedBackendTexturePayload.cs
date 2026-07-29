using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Backend-owned payload used only while lowering a logical texture reference.
/// It is never stored in a material row.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedBackendTexturePayload(
    ulong OpenGlBindlessHandle,
    uint VulkanDescriptorIndex,
    uint VulkanHeapResourceIndex,
    uint TextureArrayIndex,
    uint TextureArrayLayer,
    uint SamplerIndex,
    uint LogicalGeneration,
    EAdvancedResourceReferenceFlags Flags);
