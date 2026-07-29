using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Backend-owned payload used only while lowering a logical sampler reference.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedBackendSamplerPayload(
    uint OpenGlSamplerIndex,
    uint VulkanDescriptorIndex,
    uint VulkanHeapSamplerIndex,
    uint LogicalGeneration,
    EAdvancedResourceReferenceFlags Flags,
    uint Reserved0,
    uint Reserved1,
    uint Reserved2);
